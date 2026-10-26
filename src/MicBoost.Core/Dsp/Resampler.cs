namespace MicBoost.Core.Dsp;

/// <summary>
/// Streaming mono band-limited resampler (Blackman-windowed sinc, interpolated polyphase table).
/// Supports a small run-time ratio correction so two independent device clocks can be kept in sync.
/// </summary>
public sealed class Resampler
{
    private const int HalfTaps = 16;            // zero crossings either side
    private const int Taps = HalfTaps * 2;
    private const int Phases = 512;

    private readonly float[] _table;            // (Phases + 1) * Taps
    private readonly double _baseStep;          // input samples advanced per output sample
    private float[] _buffer;
    private int _count;
    private double _pos;
    private double _step;

    public Resampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        _baseStep = (double)inputRate / outputRate;
        _step = _baseStep;
        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.92;
        _table = BuildTable(cutoff);
        _buffer = new float[4096];
        Reset();
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Delay introduced by the filter, in input samples.</summary>
    public int LatencyInputSamples => HalfTaps;

    /// <summary>Current output/input ratio correction (1 = nominal).</summary>
    public double Correction => _baseStep / _step;

    /// <summary>Number of buffered input samples not yet fully consumed.</summary>
    public int BufferedInputSamples => Math.Max(0, _count - (int)_pos);

    /// <summary>
    /// Adjusts the effective ratio. Values above 1 produce slightly more output per input,
    /// below 1 slightly less. Clamped to +/-1%.
    /// </summary>
    public void SetCorrection(double correction)
    {
        correction = Math.Clamp(correction, 0.99, 1.01);
        _step = _baseStep / correction;
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _count = HalfTaps;   // left context of zeros
        _pos = HalfTaps;
    }

    /// <summary>Upper bound for the number of output samples produced from <paramref name="inputLength"/> new samples.</summary>
    public int MaxOutputLength(int inputLength) => (int)Math.Ceiling((inputLength + HalfTaps * 2) / (_baseStep * 0.99)) + 2;

    /// <summary>
    /// Pushes input samples and writes as many output samples as are available.
    /// Returns the number of samples written to <paramref name="output"/>.
    /// </summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        Append(input);

        int written = 0;
        float[] buf = _buffer;
        float[] table = _table;
        while (written < output.Length)
        {
            int i = (int)_pos;
            if (i + HalfTaps >= _count) break;
            double frac = _pos - i;
            double phase = frac * Phases;
            int p0 = (int)phase;
            float a = (float)(phase - p0);
            int row0 = p0 * Taps;
            int row1 = row0 + Taps;
            int start = i - HalfTaps + 1;

            float sum = 0f;
            for (int k = 0; k < Taps; k++)
            {
                float h = table[row0 + k] + a * (table[row1 + k] - table[row0 + k]);
                sum += buf[start + k] * h;
            }
            output[written++] = sum;
            _pos += _step;
        }

        Compact();
        return written;
    }

    private void Append(ReadOnlySpan<float> input)
    {
        int needed = _count + input.Length;
        if (needed > _buffer.Length)
        {
            int size = _buffer.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref _buffer, size);
        }
        input.CopyTo(_buffer.AsSpan(_count));
        _count += input.Length;
    }

    private void Compact()
    {
        // Keep the samples still needed as left context for the next output.
        int keepFrom = (int)_pos - HalfTaps + 1;
        if (keepFrom <= 0) return;
        if (keepFrom > _count) keepFrom = _count;
        int remaining = _count - keepFrom;
        Array.Copy(_buffer, keepFrom, _buffer, 0, remaining);
        _count = remaining;
        _pos -= keepFrom;
    }

    private static float[] BuildTable(double cutoff)
    {
        var table = new float[(Phases + 1) * Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            int row = p * Taps;
            for (int k = 0; k < Taps; k++)
            {
                // Tap k corresponds to input offset (k - HalfTaps + 1) relative to floor(pos).
                double d = (k - HalfTaps + 1) - frac;
                double h = cutoff * Sinc(cutoff * d) * Blackman(d / HalfTaps);
                table[row + k] = (float)h;
                sum += h;
            }
            // Normalise every phase to unity DC gain.
            for (int k = 0; k < Taps; k++) table[row + k] = (float)(table[row + k] / sum);
        }
        return table;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    private static double Blackman(double u)
    {
        if (Math.Abs(u) >= 1.0) return 0.0;
        return 0.42 + 0.5 * Math.Cos(Math.PI * u) + 0.08 * Math.Cos(2 * Math.PI * u);
    }
}
