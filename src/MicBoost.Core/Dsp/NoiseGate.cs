namespace MicBoost.Core.Dsp;

/// <summary>
/// Downward noise gate with hysteresis and hold. When the input envelope stays below the
/// threshold the signal is attenuated by <see cref="RangeDb"/> (not hard-muted, which sounds unnatural).
/// </summary>
public sealed class NoiseGate
{
    private readonly int _sampleRate;
    private readonly float _envAttack;
    private readonly float _envRelease;
    private readonly float _attackSamples;
    private readonly float _releaseSamples;
    private readonly int _holdSamples;

    private float _envelope;
    private float _gainDb;
    private float _gain = 1f;
    private int _holdCounter;
    private bool _open = true;

    public NoiseGate(int sampleRate, float attackMs = 2f, float holdMs = 120f, float releaseMs = 150f)
    {
        _sampleRate = sampleRate;
        _envAttack = Db.TimeCoefficient(0.5f, sampleRate);
        _envRelease = Db.TimeCoefficient(40f, sampleRate);
        _attackSamples = Math.Max(1f, attackMs * 0.001f * sampleRate);
        _releaseSamples = Math.Max(1f, releaseMs * 0.001f * sampleRate);
        _holdSamples = (int)(holdMs * 0.001f * sampleRate);
    }

    /// <summary>Opening threshold in dBFS (measured on the gate's input).</summary>
    public float ThresholdDb { get; set; } = -50f;

    /// <summary>The gate closes again this many dB below the threshold.</summary>
    public float HysteresisDb { get; set; } = 4f;

    /// <summary>Attenuation applied while closed (negative dB).</summary>
    public float RangeDb { get; set; } = -40f;

    public bool IsOpen => _open;

    /// <summary>Current gate gain in dB (0 when fully open).</summary>
    public float CurrentGainDb => _gainDb;

    public void Reset()
    {
        _envelope = 0f;
        _gainDb = 0f;
        _gain = 1f;
        _holdCounter = 0;
        _open = true;
    }

    public void Process(Span<float> buffer)
    {
        float openLevel = Db.ToLinear(ThresholdDb);
        float closeLevel = Db.ToLinear(ThresholdDb - HysteresisDb);
        float range = MathF.Min(0f, RangeDb);
        // Gain moves linearly in dB: fully open → closed in releaseMs, closed → open in attackMs.
        float openStep = -range / _attackSamples;
        float closeStep = -range / _releaseSamples;

        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float rectified = MathF.Abs(x);
            float coef = rectified > _envelope ? _envAttack : _envRelease;
            _envelope = rectified + coef * (_envelope - rectified);

            if (_envelope >= openLevel)
            {
                _open = true;
                _holdCounter = _holdSamples;
            }
            else if (_open && _envelope < closeLevel)
            {
                if (_holdCounter > 0) _holdCounter--;
                else _open = false;
            }

            float target = _open ? 0f : range;
            if (_gainDb != target)
            {
                _gainDb = _gainDb < target
                    ? MathF.Min(target, _gainDb + openStep)
                    : MathF.Max(target, _gainDb - closeStep);
                _gain = Db.ToLinear(_gainDb);
            }
            buffer[i] = x * _gain;
        }

        if (_envelope < 1e-12f) _envelope = 0f; // avoid denormals
    }

    internal int SampleRate => _sampleRate;
}
