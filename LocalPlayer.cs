using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Eversolo;

/// <summary>
/// "Listen on this PC": plays the same file the device is playing, read from the NAS share, in step with the device.
/// Decoding: ffmpeg (bit-exact for FLAC/WAV/AIFF/ALAC; DSD is converted to PCM with soxr).
/// Output: NAudio WASAPI, exclusive mode by default (no Windows mixer/resampler) at the file's own sample rate,
/// capped to what the DAC accepts (DragonFly Cobalt: 24 bit / 96 kHz).
/// </summary>
public sealed class LocalPlayer : IDisposable
{
    readonly PlayerService _p;
    readonly Settings _s;
    readonly System.Windows.Threading.Dispatcher _disp;
    Process? _ff;
    WasapiOut? _out;
    BufferedWaveProvider? _buf;
    CountingProvider? _counter;
    Thread? _pump;
    long _openedTrackId = -1;
    long _startMs;
    bool _enabled, _ready, _ended, _nudged;
    DateTime _endedAt, _trackOpenedAt;
    DateTime _lastSeek = DateTime.MinValue;
    string _outDesc = "";

    public string Status { get; private set; } = "";
    public SpectrumAnalyzer Spectrum { get; } = new(48);
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _s.LocalPlay = value;
            _s.Save();
            if (value) Sync(); else { Stop(); Status = ""; }
            Changed?.Invoke();
        }
    }
    public event Action? Changed;

    public LocalPlayer(PlayerService p, Settings s)
    {
        _p = p;
        _s = s;
        _disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _p.Changed += () => { if (_enabled) Sync(); };
        _enabled = s.LocalPlay;
        if (_enabled) Sync();
    }

    // ---------- ffmpeg ----------

    public static string? FindFfmpeg(string configured)
    {
        var cands = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) cands.Add(configured);
        var exeDir = AppContext.BaseDirectory;
        cands.Add(Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe"));
        cands.Add(Path.Combine(exeDir, "ffmpeg.exe"));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        cands.Add(Path.Combine(local, @"Microsoft\WinGet\Links\ffmpeg.exe"));
        foreach (var c in cands) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(c)) return c;
        }
        try
        {
            var pk = Path.Combine(local, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.EnumerateDirectories(pk, "Gyan.FFmpeg*"))
                {
                    var hit = Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
        }
        catch { }
        return null;
    }

    /// <summary>Parses "44100", "44.1 kHz", "2,82 MHz", "2822400" into Hz.</summary>
    static int ParseRate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        if (long.TryParse(s, out var n)) return (int)n;
        var num = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        var u = s.ToLowerInvariant();
        if (u.Contains("mhz")) return (int)Math.Round(v * 1_000_000);
        if (u.Contains("khz")) return (int)Math.Round(v * 1000);
        return (int)Math.Round(v);
    }

    /// <summary>Output rate: the file's own rate when the DAC accepts it, else the nearest lower rate in the same family.</summary>
    int OutputRate(Track t)
    {
        var src = ParseRate(t.SampleRate);
        if (src <= 0) src = 44100;
        var max = Math.Max(44100, _s.MaxSampleRate);
        if (src >= 2_000_000)
        {
            // DSD64/128/256 (2.8224 / 5.6448 / 11.2896 MHz, or the rounded "2,82 MHz" the device reports):
            // pick the 44.1k family unless the rate is clearly a 48k multiple, then decode to 8x-decimated PCM.
            double k44 = src / 44100.0, k48 = src / 48000.0;
            bool is48 = Math.Abs(k48 - Math.Round(k48)) < Math.Abs(k44 - Math.Round(k44));
            src = is48 ? 384000 : 352800;
        }
        while (src > max) src /= 2;
        return src;
    }

    // ---------- output ----------

    MMDevice? PickDevice()
    {
        using var en = new MMDeviceEnumerator();
        var want = _s.AudioDevice?.Trim() ?? "";
        if (want.Length > 0)
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (d.FriendlyName.Contains(want, StringComparison.OrdinalIgnoreCase)) return d;
        return en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null;
    }

    /// <summary>Opens WASAPI on the chosen device; tries exclusive 24/32/16-bit first, then shared float. Returns the ffmpeg sample format.</summary>
    string OpenOutput(int rate)
    {
        CloseOutput();
        var dev = PickDevice() ?? throw new InvalidOperationException(Loc.T("lp.nodevice"));
        var attempts = new List<(WaveFormat fmt, bool excl, string ff)>();
        if (_s.Exclusive)
        {
            attempts.Add((new WaveFormatExtensible(rate, 24, 2), true, "s24le"));
            attempts.Add((new WaveFormatExtensible(rate, 32, 2), true, "s32le"));
            attempts.Add((new WaveFormatExtensible(rate, 16, 2), true, "s16le"));
        }
        attempts.Add((WaveFormat.CreateIeeeFloatWaveFormat(rate, 2), false, "f32le"));
        Exception? last = null;
        foreach (var (fmt, excl, ff) in attempts)
        {
            try
            {
                var o = new WasapiOut(dev, excl ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, 200);
                Spectrum.LatencyMs = 200;
                _buf = new BufferedWaveProvider(fmt, TimeSpan.FromSeconds(6)) { DiscardOnBufferOverflow = false, ReadFully = true };
                Spectrum.SetFormat(fmt);
                _counter = new CountingProvider(_buf, Spectrum);
                o.Init(_counter);
                _out = o;
                _outDesc = $"{ShortDevice(dev.FriendlyName)} · {(excl ? "excl" : "shared")} · {rate / 1000.0:0.#}k/{fmt.BitsPerSample}";
                Log($"output: {dev.FriendlyName} · {(excl ? "exclusive" : "shared")} · {rate / 1000.0:0.#} kHz / {fmt.BitsPerSample} bit");
                return ff;
            }
            catch (Exception ex) { last = ex; Log($"output {fmt.SampleRate}/{fmt.BitsPerSample} {(excl ? "excl" : "shared")} failed: {ex.Message}"); }
        }
        throw new InvalidOperationException(Loc.T("lp.outfail", last?.Message));
    }

    void CloseOutput()
    {
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _out = null;
        _buf = null;
        _counter = null;
    }

    // ---------- pipeline ----------

    void StartPipeline(Track t, string path, long fromMs)
    {
        StopPipeline();
        var ffmpeg = FindFfmpeg(_s.FfmpegPath) ?? throw new InvalidOperationException(Loc.T("lp.noffmpeg"));
        var rate = OutputRate(t);
        var srcRate = ParseRate(t.SampleRate);
        var sampleFmt = OpenOutput(rate);
        _startMs = fromMs;
        _ready = false;
        _ended = false;
        var ss = (fromMs / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        // Filter chain:
        //  - DSD: decoded 1-bit audio carries strong ultrasonic shaping noise and peaks above 0 dBFS (SACD allows +3 dB),
        //    which clips when written as 24-bit integers -> audible crackle on loud passages. Standard DSD2PCM practice:
        //    low-pass ~24 kHz and 6 dB of headroom before the integer conversion.
        //  - PCM at a rate the DAC accepts: no filter at all (bit-exact). Only resample (soxr) when the rate must drop.
        var filters = new List<string>();
        bool isDsd = srcRate >= 2_000_000;
        if (isDsd)
        {
            var gain = _s.DsdGainDb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            // Steep low-pass well above the audible band for the shaping noise (a gentle 24 kHz/2-pole filter cost
            // ~1.7 dB at 20 kHz and was heard as lost detail), modest headroom, then a transparent limiter at -1 dBFS.
            if (_s.DsdLowpassHz > 0)
            {
                // Linear-phase FIR "wall": flat to the cutoff, -90 dB from cutoff+4 kHz. (ffmpeg's IIR lowpass only
                // offers 1-2 poles, which rolls off audibly below the cutoff.)
                int c = _s.DsdLowpassHz, c2 = c + 4000;
                // accuracy=250 Hz -> ~1400-tap FIR at the decoder's 352.8 kHz (accuracy=2 makes ffmpeg refuse the filter)
                filters.Add($"firequalizer=gain='if(lt(f\\,{c})\\,0\\,if(gt(f\\,{c2})\\,-90\\,-90*(f-{c})/{c2 - c}))':accuracy=250:wfunc=nuttall:fixed=true");
            }
            filters.Add($"volume={gain}dB" + (_s.DsdLimiter ? ",alimiter=limit=0.89:attack=5:release=50:level=false" : ""));
        }
        if (isDsd || (srcRate > 0 && srcRate != rate)) filters.Add($"aresample=resampler=soxr:precision=28:osr={rate}");
        var af = filters.Count == 0 ? "" : $"-af \"{string.Join(',', filters)}\" ";
        var args = $"-hide_banner -loglevel error -nostdin -ss {ss} -i \"{path}\" -vn -map 0:a:0 " +
                   $"{af}-ar {rate} -ac 2 -f {sampleFmt} pipe:1";
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = null
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("lp.ffstart"));
        _ff = proc;
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("ffmpeg: " + e.Data); };
        proc.BeginErrorReadLine();
        Log($"ffmpeg {args}");

        var buf = _buf!;
        var stream = proc.StandardOutput.BaseStream;
        var preroll = buf.WaveFormat.AverageBytesPerSecond / 2; // start after 0.5 s is buffered
        _pump = new Thread(() =>
        {
            var chunk = new byte[65536];
            try
            {
                while (_ff == proc)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    while (_ff == proc && buf.BufferedBytes + n > buf.BufferLength) Thread.Sleep(20); // back-pressure
                    if (_ff != proc) break;
                    buf.AddSamples(chunk, 0, n);
                    if (!_ready && buf.BufferedBytes >= preroll)
                    {
                        _ready = true;
                        _disp.BeginInvoke(() => Sync(force: true));
                    }
                }
                if (!_ready) { _ready = true; _disp.BeginInvoke(() => Sync(force: true)); }
                // ffmpeg finished: wait for the buffer to drain, then mark the file as ended.
                while (_ff == proc && buf.BufferedBytes > 0) Thread.Sleep(50);
                if (_ff == proc) { _ended = true; _disp.BeginInvoke(() => Sync()); }
            }
            catch (Exception ex) { Log("pump: " + ex.Message); }
        }) { IsBackground = true, Name = "ffmpeg-pump" };
        _pump.Start();
    }

    void StopPipeline()
    {
        var p = _ff;
        _ff = null;
        try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        try { p?.Dispose(); } catch { }
        CloseOutput();
        _ready = false;
    }

    long LocalPositionMs => _counter == null || _buf == null ? 0
        : _startMs + _counter.BytesRead * 1000 / _buf.WaveFormat.AverageBytesPerSecond - (_out?.OutputWaveFormat != null ? 200 : 0);

    /// <summary>Current PC playback position, or null when nothing is running. Frozen at the end once the file is over.</summary>
    public long? PositionMs => _ready && _out != null && _out.PlaybackState != PlaybackState.Stopped
        ? Math.Max(0, _ended ? Math.Min(LocalPositionMs, _p.DurationNow > 0 ? _p.DurationNow : LocalPositionMs) : LocalPositionMs)
        : null;

    /// <summary>True when the PC finished the current file (the device may not have advanced).</summary>
    public bool Ended => _ended;

    static string ShortDevice(string name)
    {
        var i = name.IndexOf('('); var j = name.LastIndexOf(')');
        if (i >= 0 && j > i) name = name.Substring(i + 1, j - i - 1);
        return name.Replace("AudioQuest ", "").Replace(" v1.0", "").Trim();
    }

    /// <summary>Re-opens the output and decoder with the current settings (device, mode, rate, DSD chain).</summary>
    public void Restart()
    {
        if (!_enabled) return;
        Stop();
        Sync();
    }

    // ---------- sync with the device ----------

    string? PathFor(Track t)
    {
        var uri = t.Uri;
        if (string.IsNullOrEmpty(uri))
            uri = _p.Queue.FirstOrDefault(q => q.Id == t.Id)?.Uri ?? "";
        if (string.IsNullOrEmpty(uri)) return null;
        var resolved = NasPaths.ResolveFile(_s.NasRoot, uri);
        if (resolved == null) { Log($"missing: {NasPaths.ToUnc(_s.NasRoot, uri)}"); return ""; }
        if (resolved != NasPaths.ToUnc(_s.NasRoot, uri)) Log($"path fallback -> {resolved}");
        return resolved;
    }

    void Sync(bool force = false)
    {
        var st = _p.State;
        var t = st?.Track;
        if (st == null || t == null) { Stop(); Status = Loc.T("lp.notrack"); Changed?.Invoke(); return; }

        if (t.Id != _openedTrackId)
        {
            if (t.Id != _lastEndedTrackId) _lastEndedTrackId = -1;
            // Nothing open and the device is paused (or we already finished this file and released the DAC):
            // do not grab the sound card again until playback actually resumes / the track changes.
            if (!_p.IsPlaying || t.Id == _lastEndedTrackId)
            {
                Status = t.Id == _lastEndedTrackId ? Loc.T("lp.ended.released") : Loc.T("lp.paused.released");
                Changed?.Invoke();
                return;
            }
            var path = PathFor(t);
            if (path == null) { Status = Loc.T("lp.nopath"); Changed?.Invoke(); return; }
            bool transition = _openedTrackId != -1; // a track change while we were already following (not the first open)
            _openedTrackId = t.Id;
            StopPipeline(); // so the previous track's PC position cannot leak into the new track's start time
            if (path == "")
            {
                var uri = t.Uri is { Length: > 0 } ? t.Uri : _p.Queue.FirstOrDefault(q => q.Id == t.Id)?.Uri ?? "";
                Status = Loc.T("lp.missing", Path.GetFileName(uri.Replace('/', '\\')));
                Changed?.Invoke();
                return;
            }
            Status = Loc.T("lp.opening", Path.GetFileName(path));
            // Start position:
            //  - device reporting 0/0 (DSF) right after a change means "just started": 0;
            //  - on a track transition the device's position often still belongs to the previous track for a poll
            //    or two (e.g. 4:20), which made the PC play a bit of the middle and then jump back: treat anything
            //    over 15 s as stale and start at 0 (the drift check fixes the rare real seek later);
            //  - first open after launch/enable: trust the device (we may be joining mid-track).
            //    Any stale value (even a small one) shows up as "plays a bit of the wrong place, then jumps", so on a
            //    transition always start at 0 and let the drift check catch a real seek once the device's position is fresh.
            long from = !transition && (st.DurationMs > 0 || st.PositionMs > 0) ? _p.PositionNow : 0;
            _trackOpenedAt = DateTime.UtcNow;
            try { StartPipeline(t, path, from); }
            catch (Exception ex) { Status = ex.Message; Log(Status); }
            Changed?.Invoke();
            return;
        }
        if (!_ready || _out == null) return;

        if (_ended)
        {
            if (_out.PlaybackState == PlaybackState.Playing) { _out.Pause(); _endedAt = DateTime.UtcNow; _nudged = false; }
            // Track over and the device did not move on (or we already nudged it): release the DAC after a while.
            if ((DateTime.UtcNow - _endedAt).TotalSeconds > 8 && (_nudged || _p.StateReliable || !_p.IsPlaying))
            {
                StopPipeline();
                _lastEndedTrackId = t.Id;
                _openedTrackId = -1;
                Status = Loc.T("lp.ended.released");
                Log("ended: output released");
                Changed?.Invoke();
                return;
            }
            // The device's state is frozen in this mode and it may sit on the finished track: if it has not moved on
            // by itself within a few seconds, press "next" for it (only once per track).
            if (!_p.StateReliable && !_nudged && (DateTime.UtcNow - _endedAt).TotalSeconds > 3 && _p.IsPlaying)
            {
                _nudged = true;
                Log($"file ended, device still on track {t.Id}: sending Key.MediaNext");
                _ = _p.NextViaRemote();
            }
            Status = _p.StateReliable ? Loc.T("lp.ended") : Loc.T("lp.ended.nudge");
            Changed?.Invoke();
            return;
        }

        var target = _p.PositionNow;
        var drift = LocalPositionMs - target;
        bool known = st.DurationMs > 0; // the device sometimes reports 0/0 right after a track change
        bool fresh = (DateTime.UtcNow - _trackOpenedAt).TotalSeconds > 3; // device position lags a poll or two after a change
        if (known && fresh && !force && Math.Abs(drift) > 2000 && (DateTime.UtcNow - _lastSeek).TotalSeconds > 4)
        {
            _lastSeek = DateTime.UtcNow;
            var path = PathFor(t);
            if (!string.IsNullOrEmpty(path))
            {
                try { StartPipeline(t, path, target + 300); } catch (Exception ex) { Status = ex.Message; Log(Status); }
                Changed?.Invoke();
                return;
            }
        }
        bool playing = _p.IsPlaying;
        if (playing) { if (_out.PlaybackState != PlaybackState.Playing) _out.Play(); _pausedAt = null; }
        else
        {
            if (_out.PlaybackState == PlaybackState.Playing) _out.Pause();
            _pausedAt ??= DateTime.UtcNow;
            // Exclusive mode owns the DAC; after a few seconds of pause hand it back so Windows and other apps can
            // use it. Resuming re-opens the pipeline at the device's position (about half a second).
            if ((DateTime.UtcNow - _pausedAt.Value).TotalSeconds > 4)
            {
                StopPipeline();
                _openedTrackId = -1; // next Sync re-opens as a "first open" (position taken from the device)
                _pausedAt = null;
                Status = Loc.T("lp.paused.released");
                Log("paused: output released");
                Changed?.Invoke();
                return;
            }
        }
        Status = playing ? $"PC: {_outDesc}{(known ? $" ({drift:+0;-0} ms)" : "")}" : $"PC ⏸ {_outDesc}";
        Changed?.Invoke();
    }

    DateTime? _pausedAt;
    long _lastEndedTrackId = -1;

    void Stop()
    {
        StopPipeline();
        _openedTrackId = -1;
    }

    static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(Settings.Dir, "localplay.log"), $"{DateTime.Now:HH:mm:ss} {m}\n"); } catch { }
    }

    public void Dispose() => Stop();

    /// <summary>Counts bytes actually pulled by the output so position can be derived from it.</summary>
    sealed class CountingProvider : IWaveProvider
    {
        readonly IWaveProvider _inner;
        readonly SpectrumAnalyzer _spectrum;
        public long BytesRead;
        public CountingProvider(IWaveProvider inner, SpectrumAnalyzer spectrum) { _inner = inner; _spectrum = spectrum; }
        public WaveFormat WaveFormat => _inner.WaveFormat;
        public int Read(Span<byte> buffer)
        {
            var n = _inner.Read(buffer);
            Interlocked.Add(ref BytesRead, n);
            if (n > 0) _spectrum.Push(buffer.Slice(0, n));
            return n;
        }

        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    }
}
