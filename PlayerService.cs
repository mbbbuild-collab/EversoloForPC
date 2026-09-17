using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Eversolo;

/// <summary>Polls the device on a background loop and publishes state on the UI dispatcher.</summary>
public sealed class PlayerService : IDisposable
{
    readonly EversoloClient _c;
    readonly Dispatcher _disp;
    readonly CancellationTokenSource _cts = new();
    long _lastTrackId = -1, _lastQueueStamp = -1;
    int _refreshRequested, _coverRetries;

    public PlayerState? State { get; private set; }
    public DateTime StateAt { get; private set; }
    public BitmapImage? Cover { get; private set; }
    public IReadOnlyList<Track> Queue { get; private set; } = Array.Empty<Track>();
    public bool Connected { get; private set; }
    public int PollMs { get; set; } = 1000;
    /// <summary>Given a track's share-relative uri, returns artwork bytes found next to the file, or null.</summary>
    public Func<string, byte[]?>? LocalCoverProvider { get; set; }
    public event Action? Changed;

    public PlayerService(EversoloClient c, Dispatcher disp)
    {
        _c = c;
        _disp = disp;
        _ = Loop();
    }

    /// <summary>Returns the duration (ms) of a track file given its share-relative uri, when the device reports none.</summary>
    public Func<string, Task<long>>? DurationProbe { get; set; }
    /// <summary>Position (ms) from the local PC player when it is running, used when the device reports none.</summary>
    public Func<long?>? ExternalPosition { get; set; }

    long _fallbackDuration;
    DateTime _trackStartedAt;
    long _pausedAccumMs;
    DateTime? _pausedSince;

    bool _assumedPlaying = true;

    /// <summary>True while the device's position/duration report is trustworthy; some playback modes report 0/0 and a frozen state.</summary>
    public bool StateReliable => State != null && (State.DurationMs > 0 || State.PositionMs > 0);

    /// <summary>Playing flag: the device's when its report is reliable, otherwise what we last asked for.</summary>
    public bool IsPlaying => State != null && (StateReliable ? State.Playing : _assumedPlaying);

    /// <summary>Duration: the device's value, or the file's own duration when the device reports 0 (happens with DSF).</summary>
    public long DurationNow => State?.DurationMs > 0 ? State.DurationMs : Math.Max(0, _fallbackDuration);

    /// <summary>Estimated position right now, interpolated between polls.</summary>
    public long PositionNow
    {
        get
        {
            var s = State;
            if (s == null) return 0;
            if (s.DurationMs > 0 || s.PositionMs > 0)
            {
                if (!s.Playing) return s.PositionMs;
                var p = s.PositionMs + (long)(DateTime.UtcNow - StateAt).TotalMilliseconds;
                return s.DurationMs > 0 ? Math.Min(p, s.DurationMs) : p;
            }
            // Device reports nothing: trust the PC player if it runs, else wall-clock since the track started.
            var ext = ExternalPosition?.Invoke();
            if (ext is > 0) return ext.Value;
            var paused = _pausedAccumMs + (_pausedSince == null ? 0 : (long)(DateTime.UtcNow - _pausedSince.Value).TotalMilliseconds);
            var est = (long)(DateTime.UtcNow - _trackStartedAt).TotalMilliseconds - paused;
            return Math.Max(0, _fallbackDuration > 0 ? Math.Min(est, _fallbackDuration) : est);
        }
    }

    /// <summary>Duration (ms) of an audio file via ffprobe next to ffmpeg; 0 when unavailable.</summary>
    public static async Task<long> ProbeDurationMs(string ffmpegPath, string file)
    {
        try
        {
            var ffprobe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
            if (!System.IO.File.Exists(ffprobe)) return 0;
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe,
                $"-v error -show_entries format=duration -of csv=p=0 \"{file}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var s = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)(d * 1000) : 0;
        }
        catch { return 0; }
    }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Tick(); } catch { }
            var wait = Interlocked.Exchange(ref _refreshRequested, 0) == 1 ? 300 : Math.Max(250, PollMs);
            try { await Task.Delay(wait, _cts.Token); } catch { break; }
        }
    }

    async Task Tick()
    {
        var st = await _c.GetStateAsync();
        var at = DateTime.UtcNow;
        BitmapImage? cover = Cover;
        IReadOnlyList<Track>? queue = null;
        if (st != null)
        {
            var tid = st.Track?.Id ?? -1;
            bool trackChanged = tid != _lastTrackId;
            if (trackChanged)
            {
                _lastTrackId = tid;
                cover = null;
                _coverRetries = 6; // the device can be slow right after a track change; keep trying for a while
                _fallbackDuration = 0;
                _trackStartedAt = at;
                _pausedAccumMs = 0;
                _assumedPlaying = true;
                _pausedSince = st.Playing ? null : at;
            }
            else
            {
                bool reliable = st.DurationMs > 0 || st.PositionMs > 0;
                bool playing = reliable ? st.Playing : _assumedPlaying;
                if (!playing && _pausedSince == null) _pausedSince = at;
                if (playing && _pausedSince != null) { _pausedAccumMs += (long)(at - _pausedSince.Value).TotalMilliseconds; _pausedSince = null; }
            }
            if (trackChanged || st.QueueChangedAt != _lastQueueStamp)
            {
                _lastQueueStamp = st.QueueChangedAt;
                queue = await _c.GetQueueAsync();
            }
            if (cover == null && st.Track != null && _coverRetries > 0)
            {
                _coverRetries--;
                // Prefer real artwork next to the file on the NAS (the device returns a generic note icon when it has none).
                byte[]? bytes = null;
                var uri = st.Track.Uri;
                if (string.IsNullOrEmpty(uri)) uri = (queue ?? Queue).FirstOrDefault(q => q.Id == tid)?.Uri ?? "";
                if (uri != "" && LocalCoverProvider != null)
                    bytes = await Task.Run(() => { try { return LocalCoverProvider(uri); } catch { return null; } });
                bytes ??= await _c.GetImageAsync(tid, st.Track.Type);
                if (bytes != null) { try { cover = ToImage(bytes); } catch { cover = null; } }
            }
            if (st.Track != null && st.DurationMs == 0 && _fallbackDuration == 0 && DurationProbe != null)
            {
                var uri = st.Track.Uri;
                if (string.IsNullOrEmpty(uri)) uri = (queue ?? Queue).FirstOrDefault(q => q.Id == tid)?.Uri ?? "";
                if (uri != "")
                {
                    try { _fallbackDuration = await DurationProbe(uri); } catch { }
                    if (_fallbackDuration == 0) _fallbackDuration = -1; // probed, nothing found; don't retry every tick
                }
            }
        }
        else
        {
            _lastTrackId = -1;
            cover = null;
        }
        await _disp.InvokeAsync(() =>
        {
            State = st;
            StateAt = at;
            Connected = st != null;
            Cover = cover;
            if (queue != null) Queue = queue;
            Changed?.Invoke();
        });
    }

    static BitmapImage ToImage(byte[] b)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(b);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    void RefreshSoon() => Interlocked.Exchange(ref _refreshRequested, 1);

    public async Task PlayOrPause()
    {
        if (StateReliable) await _c.PlayOrPauseAsync();
        else
        {
            // The music API's state is frozen in this playback mode: keep our own flag and send explicit
            // remote-control keys so a double toggle cannot happen.
            _assumedPlaying = !_assumedPlaying;
            var now = DateTime.UtcNow;
            if (_assumedPlaying && _pausedSince != null) { _pausedAccumMs += (long)(now - _pausedSince.Value).TotalMilliseconds; _pausedSince = null; }
            else if (!_assumedPlaying && _pausedSince == null) _pausedSince = now;
            await _disp.InvokeAsync(() => Changed?.Invoke());
            await _c.SendKeyAsync(_assumedPlaying ? "Key.MediaPlay" : "Key.MediaPause");
        }
        RefreshSoon();
    }
    // Next/prev: the music API call when its state is trustworthy, otherwise the remote-control key (that one always works).
    public async Task Next() { if (StateReliable) await _c.NextAsync(); else await _c.SendKeyAsync("Key.MediaNext"); RefreshSoon(); }
    public async Task Prev() { if (StateReliable) await _c.PrevAsync(); else await _c.SendKeyAsync("Key.MediaPrevious"); RefreshSoon(); }
    public async Task NextViaRemote() { await _c.SendKeyAsync("Key.MediaNext"); RefreshSoon(); }
    public async Task<bool> Play(Track t) { var ok = await _c.PlayTrackAsync(t); RefreshSoon(); return ok; }

    public void Dispose() => _cts.Cancel();
}
