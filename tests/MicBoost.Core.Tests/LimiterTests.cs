using MicBoost.Core.Dsp;

namespace MicBoost.Core.Tests;

public class LimiterTests
{
    private const int Rate = 48000;

    public static IEnumerable<object[]> LoudSignals()
    {
        yield return new object[] { "sine 0 dBFS", Signals.Sine(Rate, 1000, 0, 1) };
        yield return new object[] { "sine +12 dBFS (pre-boosted)", Signals.Sine(Rate, 220, 12, 1) };
        yield return new object[] { "square +6 dBFS", Signals.Square(Rate, 100, 6, 1) };
        yield return new object[] { "white noise +20 dBFS", Signals.WhiteNoise(Rate, 20, 1) };
        yield return new object[] { "impulses", Impulses() };
        yield return new object[] { "full-scale clipped input", Signals.Square(Rate, 1000, 0, 0.5) };
    }

    private static float[] Impulses()
    {
        var x = new float[Rate];
        for (int i = 100; i < x.Length; i += 997) x[i] = (i / 997) % 2 == 0 ? 8f : -30f;
        return x;
    }

    [Theory]
    [MemberData(nameof(LoudSignals))]
    public void Output_never_exceeds_ceiling(string name, float[] signal)
    {
        _ = name;
        var limiter = new Limiter(Rate, ceilingDb: -1f);
        float ceiling = limiter.CeilingLinear;
        Signals.ProcessInBlocks(signal, b => limiter.Process(b));
        Assert.True(Signals.MaxAbs(signal) <= ceiling, $"peak {Signals.PeakDb(signal):F3} dBFS exceeded ceiling");
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(-1f)]
    [InlineData(-3f)]
    [InlineData(-6f)]
    public void Ceiling_respected_for_any_setting(float ceilingDb)
    {
        var limiter = new Limiter(Rate, ceilingDb);
        var x = Signals.WhiteNoise(Rate, 30, 2, seed: 42);
        Signals.ProcessInBlocks(x, b => limiter.Process(b));
        Assert.True(Signals.MaxAbs(x) <= Db.ToLinear(ceilingDb) * (1 + 1e-6));
    }

    [Fact]
    public void Ceiling_respected_for_random_bursts_with_full_processor_at_max_gain()
    {
        var proc = new MicProcessor(Rate, new ProcessorSettings { GainDb = 30f, Compressor = CompressorMode.Off });
        float ceiling = Db.ToLinear(ProcessorSettings.DefaultCeilingDb);
        var rng = new Random(3);
        var x = new float[Rate * 3];
        for (int i = 0; i < x.Length; i++)
        {
            double burst = (i / 4800) % 3 == 0 ? 1.0 : 0.05;
            x[i] = (float)(burst * (rng.NextDouble() * 2 - 1));
        }
        Signals.ProcessInBlocks(x, b => proc.Process(b));
        Assert.True(Signals.MaxAbs(x) <= ceiling, $"peak {Signals.PeakDb(x):F3} dBFS");
    }

    [Fact]
    public void Quiet_signal_passes_unchanged_after_lookahead_delay()
    {
        var limiter = new Limiter(Rate, -1f);
        var input = Signals.Sine(Rate, 440, -20, 0.5);
        var output = (float[])input.Clone();
        limiter.Process(output);
        int d = limiter.LatencySamples;
        for (int i = d; i < output.Length; i++)
            Assert.Equal(input[i - d], output[i], 6);
    }

    [Fact]
    public void Loud_sine_is_limited_close_to_ceiling_without_heavy_overshoot_loss()
    {
        var limiter = new Limiter(Rate, -1f);
        var x = Signals.Sine(Rate, 1000, 6, 1);
        Signals.ProcessInBlocks(x, b => limiter.Process(b));
        double peakDb = Signals.PeakDb(x.AsSpan(Rate / 2));
        Assert.InRange(peakDb, -2.0, -1.0 + 1e-6);
    }

    [Fact]
    public void Silence_stays_silent()
    {
        var limiter = new Limiter(Rate);
        var x = Signals.Silence(Rate, 1);
        limiter.Process(x);
        Assert.All(x, v => Assert.Equal(0f, v));
    }
}
