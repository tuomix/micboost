namespace MicBoost.Core.Dsp;

/// <summary>
/// Peak + RMS level meter. Written by the audio thread, read by the UI thread
/// (single 32-bit fields, so reads are atomic; slight staleness is fine for a meter).
/// </summary>
public sealed class LevelMeter
{
    /// <summary>Samples at or above this absolute value count as clipping (about -0.01 dBFS).</summary>
    public const float ClipLevel = 0.999f;

    private readonly int _sampleRate;
    private readonly float _rmsCoef;
    private readonly float _peakDecayDbPerSecond;
    private float _meanSquare;
    private volatile float _peak;
    private volatile float _rms;
    private long _clipEvents;

    public LevelMeter(int sampleRate, float rmsWindowMs = 300f, float peakDecayDbPerSecond = 20f)
    {
        _sampleRate = sampleRate;
        _rmsCoef = 1f - Db.TimeCoefficient(rmsWindowMs, sampleRate);
        _peakDecayDbPerSecond = peakDecayDbPerSecond;
    }

    /// <summary>Peak level (linear), held and decaying at the configured rate.</summary>
    public float Peak => _peak;

    /// <summary>RMS level (linear) over the averaging window.</summary>
    public float Rms => _rms;

    public float PeakDb => Db.FromLinear(_peak);

    public float RmsDb => Db.FromLinear(_rms);

    /// <summary>Monotonic counter, incremented for each block that contained a clipped sample.</summary>
    public long ClipEvents => Interlocked.Read(ref _clipEvents);

    public void Reset()
    {
        _meanSquare = 0f;
        _peak = 0f;
        _rms = 0f;
    }

    public void Process(ReadOnlySpan<float> buffer)
    {
        if (buffer.IsEmpty) return;
        float blockPeak = 0f;
        float ms = _meanSquare;
        float coef = _rmsCoef;
        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float a = MathF.Abs(x);
            if (a > blockPeak) blockPeak = a;
            ms += coef * (x * x - ms);
        }
        if (ms < 1e-15f) ms = 0f;
        _meanSquare = ms;
        _rms = MathF.Sqrt(ms);

        float decay = Db.ToLinear(-_peakDecayDbPerSecond * buffer.Length / _sampleRate);
        float held = _peak * decay;
        _peak = blockPeak > held ? blockPeak : held;

        if (blockPeak >= ClipLevel) Interlocked.Increment(ref _clipEvents);
    }
}
