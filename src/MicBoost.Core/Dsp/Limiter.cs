namespace MicBoost.Core.Dsp;

/// <summary>
/// Look-ahead brickwall peak limiter.
/// <para>
/// For each incoming sample the gain needed to keep it under the ceiling is computed. A sliding minimum
/// over the look-ahead window of L samples, followed by a box average of the same length, gives a smooth
/// gain curve that is provably at or below the required gain for the sample being output (the signal is
/// delayed by L-1 samples). A final clamp guards against floating-point rounding, so the output can never
/// exceed the ceiling.
/// </para>
/// </summary>
public sealed class Limiter
{
    private readonly int _length;
    private readonly float[] _delay;
    private readonly float[] _box;
    private readonly int[] _dequeIndex;
    private readonly float[] _dequeValue;
    private readonly float _releaseCoef;

    private int _dequeHead;
    private int _dequeCount;
    private long _n;
    private int _pos;
    private double _boxSum;
    private float _release = 1f;
    private float _ceiling;
    private float _currentGain = 1f;
    private float _minGainInBlock = 1f;

    public Limiter(int sampleRate, float ceilingDb = -1f, float lookAheadMs = 1.5f, float releaseMs = 60f)
    {
        _length = Math.Max(2, (int)MathF.Round(lookAheadMs * 0.001f * sampleRate));
        _delay = new float[_length];
        _box = new float[_length];
        _dequeIndex = new int[_length + 1];
        _dequeValue = new float[_length + 1];
        _releaseCoef = 1f - Db.TimeCoefficient(releaseMs, sampleRate);
        CeilingDb = ceilingDb;
        Reset();
    }

    public float CeilingDb
    {
        get => Db.FromLinear(_ceiling);
        set => _ceiling = Math.Clamp(Db.ToLinear(value), 0.01f, 1f);
    }

    public float CeilingLinear => _ceiling;

    /// <summary>Added latency in samples.</summary>
    public int LatencySamples => _length - 1;

    /// <summary>Largest gain reduction (positive dB) applied during the last processed block.</summary>
    public float LastBlockGainReductionDb => -Db.FromLinear(_minGainInBlock);

    public void Reset()
    {
        Array.Clear(_delay);
        Array.Fill(_box, 1f);
        _boxSum = _length;
        _dequeHead = 0;
        _dequeCount = 0;
        _n = 0;
        _pos = 0;
        _release = 1f;
        _currentGain = 1f;
    }

    public void Process(Span<float> buffer)
    {
        float ceiling = _ceiling;
        float minGain = 1f;
        int cap = _dequeIndex.Length;

        for (int i = 0; i < buffer.Length; i++)
        {
            float x = buffer[i];
            float ax = MathF.Abs(x);
            float required = ax > ceiling ? ceiling / ax : 1f;

            // Sliding window minimum of `required` over the last L samples (monotonic deque).
            int n = (int)(_n & 0x3FFFFFFF);
            while (_dequeCount > 0 && _dequeValue[(_dequeHead + _dequeCount - 1) % cap] >= required)
                _dequeCount--;
            int tail = (_dequeHead + _dequeCount) % cap;
            _dequeIndex[tail] = n;
            _dequeValue[tail] = required;
            _dequeCount++;
            int oldest = n - _length + 1;
            while (IndexBefore(_dequeIndex[_dequeHead], oldest))
            {
                _dequeHead = (_dequeHead + 1) % cap;
                _dequeCount--;
            }
            float windowMin = _dequeValue[_dequeHead];

            // Exponential release that can only ever go down to the window minimum (never above it).
            _release += (1f - _release) * _releaseCoef;
            if (windowMin < _release) _release = windowMin;

            // Box average over L values.
            _boxSum += _release - _box[_pos];
            _box[_pos] = _release;

            float delayed = _delay[(_pos + 1) % _length];
            _delay[_pos] = x;

            float gain = (float)(_boxSum / _length);
            if (gain > 1f) gain = 1f;
            float y = delayed * gain;
            if (y > ceiling) y = ceiling;
            else if (y < -ceiling) y = -ceiling;
            buffer[i] = y;

            if (gain < minGain) minGain = gain;
            _pos = (_pos + 1) % _length;
            _n++;

            // Periodically recompute the running sum to prevent floating-point drift.
            if ((_n & 0xFFFF) == 0) RecomputeSum();
        }

        _currentGain = minGain;
        _minGainInBlock = minGain;
    }

    private static bool IndexBefore(int index, int oldest) =>
        // Indices wrap at 2^30; compare with wrap-around tolerance.
        ((oldest - index) & 0x3FFFFFFF) is > 0 and < 0x20000000;

    private void RecomputeSum()
    {
        double sum = 0;
        for (int k = 0; k < _box.Length; k++) sum += _box[k];
        _boxSum = sum;
    }

    internal float CurrentGain => _currentGain;
}
