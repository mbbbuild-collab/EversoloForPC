using NAudio.Dsp;
using NAudio.Wave;

namespace Eversolo;

/// <summary>
/// Taps the PCM stream on its way to the sound card (cheap conversion on the audio thread),
/// and computes a smoothed log-spaced band spectrum on demand (UI timer, ~30 fps).
/// The sound card pulls audio in chunks (~100 ms) ahead of what is audible, so the analysis
/// window is placed inside the last chunk according to wall-clock time, minus the output latency,
/// which keeps the bars in step with what you hear instead of pulsing once per chunk.
/// </summary>
public sealed class SpectrumAnalyzer
{
    const int N = 4096;              // FFT size
    const int M = 12;                // log2(N)
    const int Ring = 1 << 17;        // 131072 samples ≈ 1.5 s at 88.2 kHz
    readonly float[] _ring = new float[Ring];
    long _written;                   // total mono samples pushed
    long _chunkStart;                // _written before the last Push
    DateTime _chunkAt = DateTime.MinValue;
    long _lastAnalyzedEnd = -1;
    WaveFormat? _fmt;
    readonly Complex[] _fft = new Complex[N];
    readonly float[] _window = new float[N];
    float _ceiling = -80f;

    public int Bands { get; }
    public float[] Levels { get; }          // 0..1, smoothed
    public float Peak { get; private set; } // 0..1 overall level
    public int LatencyMs { get; set; } = 120;

    public SpectrumAnalyzer(int bands = 48)
    {
        Bands = bands;
        Levels = new float[bands];
        for (int i = 0; i < N; i++) _window[i] = (float)FastFourierTransform.HannWindow(i, N);
    }

    public void SetFormat(WaveFormat fmt)
    {
        _fmt = fmt;
        lock (_ring) { Array.Clear(_ring); _written = 0; _chunkStart = 0; _lastAnalyzedEnd = -1; }
    }

    /// <summary>Called on the audio thread with interleaved PCM in the output format; stores mono samples.</summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        var f = _fmt;
        if (f == null || data.Length == 0) return;
        int ch = f.Channels, bps = f.BitsPerSample / 8, frame = ch * bps;
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (f is WaveFormatExtensible we && we.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        lock (_ring)
        {
            _chunkStart = _written;
            for (int i = 0; i + frame <= data.Length; i += frame)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = i + c * bps;
                    float v = bps switch
                    {
                        2 => (short)(data[o] | data[o + 1] << 8) / 32768f,
                        3 => ((data[o] | data[o + 1] << 8 | data[o + 2] << 16) << 8 >> 8) / 8388608f,
                        4 => isFloat ? BitConverter.ToSingle(data.Slice(o, 4)) : BitConverter.ToInt32(data.Slice(o, 4)) / 2147483648f,
                        _ => 0
                    };
                    sum += v;
                }
                _ring[_written & (Ring - 1)] = sum / ch;
                _written++;
            }
            _chunkAt = DateTime.UtcNow;
        }
    }

    /// <summary>Recomputes Levels for the audio that is playing right now; decays to silence when the stream stops.</summary>
    public void Update()
    {
        var f = _fmt;
        bool have = false;
        if (f != null)
        {
            lock (_ring)
            {
                var sinceChunk = (DateTime.UtcNow - _chunkAt).TotalMilliseconds;
                if (_written >= N && sinceChunk < 600)
                {
                    // Estimated audible sample = start of the last chunk + time elapsed since it was pulled - output latency.
                    long chunkLen = _written - _chunkStart;
                    long advance = (long)((sinceChunk - LatencyMs) * f.SampleRate / 1000.0);
                    long end = Math.Clamp(_chunkStart + advance, Math.Max(N, _written - Ring + N), _written);
                    if (end != _lastAnalyzedEnd)
                    {
                        _lastAnalyzedEnd = end;
                        long start = end - N;
                        for (int i = 0; i < N; i++)
                        {
                            _fft[i].X = _ring[(start + i) & (Ring - 1)] * _window[i];
                            _fft[i].Y = 0;
                        }
                        have = true;
                    }
                    else return; // same window as last frame: keep the bars where they are
                }
            }
        }
        if (!have || f == null)
        {
            for (int b = 0; b < Bands; b++) Levels[b] *= 0.85f;
            Peak *= 0.85f;
            return;
        }
        FastFourierTransform.FFT(true, M, _fft);

        double nyq = f.SampleRate / 2.0, binHz = f.SampleRate / (double)N;
        double fLo = 30, fHi = Math.Min(18000, nyq * 0.95);
        var raw = new float[Bands];
        float frameMax = -200f; // dB values are negative; start below anything real
        for (int b = 0; b < Bands; b++)
        {
            double lo = fLo * Math.Pow(fHi / fLo, (double)b / Bands);
            double hi = fLo * Math.Pow(fHi / fLo, (double)(b + 1) / Bands);
            int i0 = Math.Max(1, (int)(lo / binHz)), i1 = Math.Max(i0 + 1, (int)(hi / binHz));
            double sum = 0;
            for (int i = i0; i < i1 && i < N / 2; i++) sum += _fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y;
            double db = 10 * Math.Log10(sum / (i1 - i0) + 1e-12);
            raw[b] = (float)db;
            if (db > frameMax) frameMax = (float)db;
        }
        // Auto gain: the loudest band over the last seconds defines the top of the display (slow release).
        _ceiling = frameMax > _ceiling ? frameMax : _ceiling - 0.05f;
        if (_ceiling < -80) _ceiling = -80;
        const float range = 55f;
        float peak = 0;
        for (int b = 0; b < Bands; b++)
        {
            float lvl = Math.Clamp((raw[b] - (_ceiling - range)) / range, 0, 1);
            lvl = lvl * lvl * (3 - 2 * lvl);
            Levels[b] = lvl > Levels[b] ? Levels[b] * 0.4f + lvl * 0.6f : Levels[b] * 0.86f + lvl * 0.14f;
            if (Levels[b] > peak) peak = Levels[b];
        }
        Peak = peak > Peak ? peak : Peak * 0.9f + peak * 0.1f;
    }
}
