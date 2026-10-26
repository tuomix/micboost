namespace MicBoost.Core.Dsp;

/// <summary>Simple bounded FIFO of mono samples (single-threaded use; the engine uses one audio thread).</summary>
public sealed class SampleFifo
{
    private readonly float[] _data;
    private int _read;
    private int _count;

    public SampleFifo(int capacity)
    {
        _data = new float[Math.Max(16, capacity)];
    }

    public int Count => _count;

    public int Capacity => _data.Length;

    /// <summary>Writes samples; if full, the oldest samples are discarded. Returns samples dropped.</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        int dropped = 0;
        if (samples.Length >= _data.Length)
        {
            dropped = _count + samples.Length - _data.Length;
            samples = samples[^_data.Length..];
            _read = 0;
            _count = 0;
        }
        int free = _data.Length - _count;
        if (samples.Length > free)
        {
            int drop = samples.Length - free;
            Skip(drop);
            dropped += drop;
        }
        int write = (_read + _count) % _data.Length;
        int first = Math.Min(samples.Length, _data.Length - write);
        samples[..first].CopyTo(_data.AsSpan(write));
        samples[first..].CopyTo(_data);
        _count += samples.Length;
        return dropped;
    }

    /// <summary>Reads up to destination.Length samples; returns how many were read.</summary>
    public int Read(Span<float> destination)
    {
        int n = Math.Min(destination.Length, _count);
        int first = Math.Min(n, _data.Length - _read);
        _data.AsSpan(_read, first).CopyTo(destination);
        _data.AsSpan(0, n - first).CopyTo(destination[first..]);
        _read = (_read + n) % _data.Length;
        _count -= n;
        return n;
    }

    public void Skip(int samples)
    {
        samples = Math.Min(samples, _count);
        _read = (_read + samples) % _data.Length;
        _count -= samples;
    }

    /// <summary>
    /// Removes <paramref name="samples"/> from the front of the FIFO, cross-fading the old head into the new
    /// head over <paramref name="fadeLength"/> samples so the cut is click-free.
    /// </summary>
    public void SpliceSkip(int samples, int fadeLength)
    {
        samples = Math.Min(samples, _count);
        if (samples <= 0) return;
        int fade = Math.Min(fadeLength, _count - samples);
        if (fade <= 0)
        {
            Skip(samples);
            return;
        }
        int len = _data.Length;
        Span<float> head = fade <= 512 ? stackalloc float[fade] : new float[fade];
        for (int i = 0; i < fade; i++) head[i] = _data[(_read + i) % len];
        Skip(samples);
        for (int i = 0; i < fade; i++)
        {
            float t = (i + 1f) / (fade + 1f);
            int idx = (_read + i) % len;
            _data[idx] = head[i] * (1f - t) + _data[idx] * t;
        }
    }

    public void Clear()
    {
        _read = 0;
        _count = 0;
    }
}
