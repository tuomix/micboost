namespace MicBoost.Core.Dsp;

/// <summary>Immutable processing parameters. Swap the whole object to change settings from another thread.</summary>
public sealed record ProcessorSettings
{
    public const float MinGainDb = 0f;
    public const float MaxGainDb = 30f;
    public const float MinGateThresholdDb = -80f;
    public const float MaxGateThresholdDb = -20f;
    public const float DefaultCeilingDb = -1f;

    public float GainDb { get; init; } = 12f;
    public bool Bypass { get; init; }
    public bool Mute { get; init; }
    public bool GateEnabled { get; init; }
    public float GateThresholdDb { get; init; } = -50f;
    public CompressorMode Compressor { get; init; } = CompressorMode.Off;
    public float LimiterCeilingDb { get; init; } = DefaultCeilingDb;

    public ProcessorSettings Normalized() => this with
    {
        GainDb = Math.Clamp(float.IsFinite(GainDb) ? GainDb : 0f, MinGainDb, MaxGainDb),
        GateThresholdDb = Math.Clamp(float.IsFinite(GateThresholdDb) ? GateThresholdDb : -50f, MinGateThresholdDb, MaxGateThresholdDb),
        LimiterCeilingDb = Math.Clamp(float.IsFinite(LimiterCeilingDb) ? LimiterCeilingDb : DefaultCeilingDb, -12f, 0f),
    };
}

/// <summary>
/// The complete mono processing chain:
/// input meter → noise gate → gain → compressor → limiter (always on) → output meter.
/// Bypass passes the raw input through; mute outputs silence. Both fade linearly over 10 ms to avoid clicks.
/// </summary>
public sealed class MicProcessor
{
    private readonly NoiseGate _gate;
    private readonly Compressor _compressor;
    private readonly Limiter _limiter;
    private readonly float _smoothCoef;
    private readonly float _fadeStep;
    private float[] _dry = new float[1024];
    private float[] _delayedDry;
    private int _delayedDryPos;

    private ProcessorSettings _settings;
    private ProcessorSettings? _applied;
    private float _gain;          // current linear gain (smoothed)
    private float _wet = 1f;      // 1 = processed, 0 = bypassed
    private float _level = 1f;    // 0 = muted

    public MicProcessor(int sampleRate, ProcessorSettings? settings = null)
    {
        SampleRate = sampleRate;
        _gate = new NoiseGate(sampleRate);
        _compressor = new Compressor(sampleRate, CompressorSettings.ForMode(CompressorMode.Off));
        _limiter = new Limiter(sampleRate);
        _smoothCoef = Db.TimeCoefficient(10f, sampleRate);
        _fadeStep = 1f / (0.010f * sampleRate); // 10 ms linear fades
        _delayedDry = new float[_limiter.LatencySamples];
        _settings = (settings ?? new ProcessorSettings()).Normalized();
        _gain = Db.ToLinear(_settings.GainDb);
        _wet = _settings.Bypass ? 0f : 1f;
        _level = _settings.Mute ? 0f : 1f;
        InputMeter = new LevelMeter(sampleRate);
        OutputMeter = new LevelMeter(sampleRate);
    }

    public int SampleRate { get; }

    public LevelMeter InputMeter { get; }

    public LevelMeter OutputMeter { get; }

    /// <summary>Total gain reduction from compressor and limiter in the last block (positive dB).</summary>
    public float GainReductionDb { get; private set; }

    /// <summary>Latency added by the processing chain, in samples.</summary>
    public int LatencySamples => _limiter.LatencySamples;

    public bool GateOpen => _gate.IsOpen;

    public ProcessorSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, (value ?? throw new ArgumentNullException(nameof(value))).Normalized());
    }

    public void Reset()
    {
        _gate.Reset();
        _compressor.Reset();
        _limiter.Reset();
        Array.Clear(_delayedDry);
        InputMeter.Reset();
        OutputMeter.Reset();
    }

    /// <summary>Processes a block of mono samples in place.</summary>
    public void Process(Span<float> buffer)
    {
        var s = Volatile.Read(ref _settings);
        if (!ReferenceEquals(s, _applied))
        {
            _gate.ThresholdDb = s.GateThresholdDb;
            _compressor.Settings = CompressorSettings.ForMode(s.Compressor);
            _limiter.CeilingDb = s.LimiterCeilingDb;
            _applied = s;
        }

        for (int i = 0; i < buffer.Length; i++)
            if (!float.IsFinite(buffer[i])) buffer[i] = 0f;

        InputMeter.Process(buffer);

        if (_dry.Length < buffer.Length) _dry = new float[buffer.Length * 2];
        var dry = _dry.AsSpan(0, buffer.Length);
        buffer.CopyTo(dry);

        if (s.GateEnabled) _gate.Process(buffer);

        // Gain with ~10 ms smoothing to avoid zipper noise; snaps exactly to target when close.
        float targetGain = Db.ToLinear(s.GainDb);
        float c = _smoothCoef;
        float g = _gain;
        for (int i = 0; i < buffer.Length; i++)
        {
            g = targetGain + c * (g - targetGain);
            buffer[i] *= g;
        }
        if (MathF.Abs(g - targetGain) <= targetGain * 1e-6f) g = targetGain;
        _gain = g;

        if (s.Compressor != CompressorMode.Off) _compressor.Process(buffer);
        else _compressor.Reset();

        _limiter.Process(buffer);
        float compGr = s.Compressor != CompressorMode.Off ? _compressor.GainReductionDb : 0f;
        GainReductionDb = compGr + _limiter.LastBlockGainReductionDb;

        // Delay the dry signal by the limiter latency so bypass cross-fades stay time-aligned.
        DelayDry(dry);

        float targetWet = s.Bypass ? 0f : 1f;
        float targetLevel = s.Mute ? 0f : 1f;
        float wet = _wet, level = _level;
        if (wet != 1f || level != 1f || targetWet != 1f || targetLevel != 1f)
        {
            float step = _fadeStep;
            for (int i = 0; i < buffer.Length; i++)
            {
                wet = wet < targetWet ? MathF.Min(targetWet, wet + step) : MathF.Max(targetWet, wet - step);
                level = level < targetLevel ? MathF.Min(targetLevel, level + step) : MathF.Max(targetLevel, level - step);
                float mixed = wet >= 1f ? buffer[i] : wet * buffer[i] + (1f - wet) * dry[i];
                buffer[i] = mixed * level;
            }
            _wet = wet;
            _level = level;
        }

        OutputMeter.Process(buffer);
    }

    private void DelayDry(Span<float> dry)
    {
        int len = _delayedDry.Length;
        if (len == 0) return;
        for (int i = 0; i < dry.Length; i++)
        {
            float x = dry[i];
            dry[i] = _delayedDry[_delayedDryPos];
            _delayedDry[_delayedDryPos] = x;
            _delayedDryPos = (_delayedDryPos + 1) % len;
        }
    }
}
