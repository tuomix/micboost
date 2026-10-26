using MicBoost.Core.Dsp;

namespace MicBoost.Core.Tests;

internal delegate void BlockProcessor(Span<float> block);

/// <summary>Synthetic test signals and measurements.</summary>
internal static class Signals
{
    public static float[] Sine(int sampleRate, double frequency, double amplitudeDb, double seconds, double phase = 0)
    {
        int n = (int)(sampleRate * seconds);
        double amp = Db.ToLinear(amplitudeDb);
        var x = new float[n];
        for (int i = 0; i < n; i++)
            x[i] = (float)(amp * Math.Sin(2 * Math.PI * frequency * i / sampleRate + phase));
        return x;
    }

    public static float[] Silence(int sampleRate, double seconds) => new float[(int)(sampleRate * seconds)];

    public static float[] WhiteNoise(int sampleRate, double amplitudeDb, double seconds, int seed = 1)
    {
        var rng = new Random(seed);
        int n = (int)(sampleRate * seconds);
        float amp = (float)Db.ToLinear(amplitudeDb);
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = amp * (float)(rng.NextDouble() * 2 - 1);
        return x;
    }

    public static float[] Square(int sampleRate, double frequency, double amplitudeDb, double seconds)
    {
        int n = (int)(sampleRate * seconds);
        float amp = (float)Db.ToLinear(amplitudeDb);
        var x = new float[n];
        for (int i = 0; i < n; i++)
            x[i] = Math.Sin(2 * Math.PI * frequency * i / sampleRate) >= 0 ? amp : -amp;
        return x;
    }

    public static double RmsDb(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var v in x) sum += (double)v * v;
        return Db.FromLinear(Math.Sqrt(sum / x.Length));
    }

    public static double PeakDb(ReadOnlySpan<float> x) => Db.FromLinear(MaxAbs(x));

    public static double MaxAbs(ReadOnlySpan<float> x)
    {
        double peak = 0;
        foreach (var v in x) peak = Math.Max(peak, Math.Abs(v));
        return peak;
    }

    /// <summary>Feeds a signal through a block processor in irregular block sizes, like a real device would.</summary>
    public static void ProcessInBlocks(float[] x, BlockProcessor process, int seed = 7)
    {
        var rng = new Random(seed);
        int i = 0;
        while (i < x.Length)
        {
            int len = Math.Min(x.Length - i, rng.Next(1, 1025));
            process(x.AsSpan(i, len));
            i += len;
        }
    }

    /// <summary>Estimates frequency from positive-going zero crossings.</summary>
    public static double EstimateFrequency(ReadOnlySpan<float> x, int sampleRate)
    {
        int first = -1, last = -1, crossings = 0;
        double firstPos = 0, lastPos = 0;
        for (int i = 1; i < x.Length; i++)
        {
            if (x[i - 1] < 0 && x[i] >= 0)
            {
                double pos = i - 1 + x[i - 1] / (x[i - 1] - x[i]);
                if (first < 0) { first = i; firstPos = pos; }
                else { crossings++; last = i; lastPos = pos; }
            }
        }
        return last < 0 ? 0 : crossings * sampleRate / (lastPos - firstPos);
    }
}
