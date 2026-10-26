namespace MicBoost.Core.Dsp;

public enum CompressorMode
{
    Off = 0,
    Light = 1,
    Voice = 2,
}

/// <summary>Parameters for <see cref="Compressor"/>.</summary>
public readonly record struct CompressorSettings(
    float ThresholdDb,
    float Ratio,
    float KneeDb,
    float AttackMs,
    float ReleaseMs,
    float MakeupDb)
{
    public static CompressorSettings ForMode(CompressorMode mode) => mode switch
    {
        CompressorMode.Light => new(-20f, 2f, 6f, 10f, 150f, 2f),
        CompressorMode.Voice => new(-26f, 4f, 8f, 5f, 120f, 6f),
        _ => new(0f, 1f, 0f, 10f, 100f, 0f),
    };
}

/// <summary>
/// Feed-forward soft-knee compressor (log-domain detector, smoothed gain reduction).
/// </summary>
public sealed class Compressor
{
    private readonly int _sampleRate;
    private CompressorSettings _settings;
    private float _attack;
    private float _release;
    private float _makeup;
    private float _smoothedGrDb; // <= 0

    public Compressor(int sampleRate, CompressorSettings settings)
    {
        _sampleRate = sampleRate;
        Settings = settings;
    }

    public CompressorSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _attack = Db.TimeCoefficient(value.AttackMs, _sampleRate);
            _release = Db.TimeCoefficient(value.ReleaseMs, _sampleRate);
            _makeup = Db.ToLinear(value.MakeupDb);
        }
    }

    /// <summary>Current gain reduction in dB (positive number, 0 = none).</summary>
    public float GainReductionDb => -_smoothedGrDb;

    public void Reset() => _smoothedGrDb = 0f;

    /// <summary>Static gain curve: output level (dB) for an input level (dB).</summary>
    public static float GainComputer(float inputDb, CompressorSettings s)
    {
        float over = inputDb - s.ThresholdDb;
        if (s.Ratio <= 1f) return inputDb;
        if (s.KneeDb > 0f && 2f * MathF.Abs(over) <= s.KneeDb)
        {
            float t = over + s.KneeDb / 2f;
            return inputDb + (1f / s.Ratio - 1f) * t * t / (2f * s.KneeDb);
        }
        return over <= 0f ? inputDb : s.ThresholdDb + over / s.Ratio;
    }

    public void Process(Span<float> buffer)
    {
        var s = _settings;
        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float levelDb = Db.FromLinear(x);
            float grDb = GainComputer(levelDb, s) - levelDb; // <= 0
            float coef = grDb < _smoothedGrDb ? _attack : _release;
            _smoothedGrDb = grDb + coef * (_smoothedGrDb - grDb);
            buffer[i] = x * Db.ToLinear(_smoothedGrDb) * _makeup;
        }
    }
}
