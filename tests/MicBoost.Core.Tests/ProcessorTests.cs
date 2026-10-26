using MicBoost.Core.Dsp;

namespace MicBoost.Core.Tests;

public class ProcessorTests
{
    private const int Rate = 48000;

    [Theory]
    [InlineData(0f)]
    [InlineData(6f)]
    [InlineData(12f)]
    [InlineData(20.5f)]
    [InlineData(30f)]
    public void Gain_is_accurate_within_0_1_dB(float gainDb)
    {
        // Input level chosen so the boosted signal stays well below the limiter ceiling.
        double inputDb = -40;
        var proc = new MicProcessor(Rate, new ProcessorSettings { GainDb = gainDb });
        var x = Signals.Sine(Rate, 1000, inputDb, 2);
        double inRms = Signals.RmsDb(x.AsSpan(Rate));
        Signals.ProcessInBlocks(x, b => proc.Process(b));
        double outRms = Signals.RmsDb(x.AsSpan(Rate)); // skip the first second (smoothing settles)
        Assert.InRange(outRms - inRms, gainDb - 0.1, gainDb + 0.1);
    }

    [Fact]
    public void Gain_change_is_smoothed_and_reaches_target()
    {
        var proc = new MicProcessor(Rate, new ProcessorSettings { GainDb = 0f });
        var x = Signals.Sine(Rate, 500, -40, 0.2);
        proc.Process(x);
        proc.Settings = proc.Settings with { GainDb = 24f };
        var y = Signals.Sine(Rate, 500, -40, 1);
        var original = (float[])y.Clone();
        Signals.ProcessInBlocks(y, b => proc.Process(b));

        // No instantaneous jump: the first few samples are still close to unity gain.
        double early = Signals.MaxAbs(y.AsSpan(0, 24)) / Signals.MaxAbs(original.AsSpan(0, 24));
        Assert.True(early < Db.ToLinear(6.0), $"gain jumped too fast ({Db.FromLinear(early):F1} dB)");
        double late = Signals.RmsDb(y.AsSpan(Rate / 2)) - Signals.RmsDb(original.AsSpan(Rate / 2));
        Assert.InRange(late, 23.9, 24.1);
    }

    [Fact]
    public void Silence_in_silence_out_with_all_stages_enabled()
    {
        var proc = new MicProcessor(Rate, new ProcessorSettings
        {
            GainDb = 30f, GateEnabled = true, Compressor = CompressorMode.Voice,
        });
        var x = Signals.Silence(Rate, 1);
        Signals.ProcessInBlocks(x, b => proc.Process(b));
        Assert.All(x, v => Assert.Equal(0f, v));
        Assert.Equal(Db.Floor, proc.OutputMeter.PeakDb);
    }

    [Fact]
    public void Clipping_input_with_max_boost_never_exceeds_ceiling_and_is_flagged()
    {
        var proc = new MicProcessor(Rate, new ProcessorSettings { GainDb = 30f, Compressor = CompressorMode.Voice });
        var x = Signals.Square(Rate, 300, 0, 1);
        Signals.ProcessInBlocks(x, b => proc.Process(b));
        Assert.True(Signals.MaxAbs(x) <= Db.ToLinear(-1f));
        Assert.True(proc.InputMeter.ClipEvents > 0);
        Assert.Equal(0, proc.OutputMeter.ClipEvents);
        Assert.True(proc.GainReductionDb > 20f);
    }

    [Fact]
    public void Non_finite_input_is_sanitised()
    {
        var proc = new MicProcessor(Rate, new ProcessorSettings { GainDb = 10f });
        var x = new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.1f };
        proc.Process(x);
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void Bypass_passes_input_through_unprocessed()
    {
        var proc = new MicProcessor(Rate, new ProcessorSettings { GainDb = 20f, Bypass = true });
        var x = Signals.Sine(Rate, 1000, -30, 1);
        double inRms = Signals.RmsDb(x);
        Signals.ProcessInBlocks(x, b => proc.Process(b));
        Assert.InRange(Signals.RmsDb(x.AsSpan(Rate / 2)) - inRms, -0.1, 0.1);
    }

    [Fact]
    public void Mute_outputs_silence_after_fade()
    {
        var proc = new MicProcessor(Rate, new ProcessorSettings { GainDb = 12f });
        var x = Signals.Sine(Rate, 1000, -20, 0.5);
        proc.Process(x);
        proc.Settings = proc.Settings with { Mute = true };
        var y = Signals.Sine(Rate, 1000, -20, 0.5);
        Signals.ProcessInBlocks(y, b => proc.Process(b));
        Assert.True(Signals.MaxAbs(y.AsSpan(Rate / 10)) < 1e-6);
        Assert.True(Signals.MaxAbs(y.AsSpan(0, 48)) > 0.01, "mute should fade, not cut instantly");
    }

    [Fact]
    public void Settings_are_clamped()
    {
        var s = new ProcessorSettings { GainDb = 99f, GateThresholdDb = -500f, LimiterCeilingDb = 5f }.Normalized();
        Assert.Equal(30f, s.GainDb);
        Assert.Equal(-80f, s.GateThresholdDb);
        Assert.Equal(0f, s.LimiterCeilingDb);
        Assert.Equal(0f, new ProcessorSettings { GainDb = float.NaN }.Normalized().GainDb);
    }
}
