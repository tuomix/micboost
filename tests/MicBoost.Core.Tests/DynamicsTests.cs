using MicBoost.Core.Dsp;

namespace MicBoost.Core.Tests;

public class DynamicsTests
{
    private const int Rate = 48000;

    [Fact]
    public void Gate_attenuates_signal_below_threshold()
    {
        var gate = new NoiseGate(Rate) { ThresholdDb = -40f, RangeDb = -40f };
        var noise = Signals.WhiteNoise(Rate, -60, 1);
        double before = Signals.RmsDb(noise.AsSpan(Rate / 2));
        Signals.ProcessInBlocks(noise, b => gate.Process(b));
        double after = Signals.RmsDb(noise.AsSpan(Rate / 2));
        Assert.False(gate.IsOpen);
        Assert.InRange(after - before, -40.5, -39.5);
    }

    [Fact]
    public void Gate_passes_signal_above_threshold()
    {
        var gate = new NoiseGate(Rate) { ThresholdDb = -40f };
        var x = Signals.Sine(Rate, 300, -20, 1);
        double before = Signals.RmsDb(x.AsSpan(Rate / 2));
        Signals.ProcessInBlocks(x, b => gate.Process(b));
        Assert.True(gate.IsOpen);
        Assert.InRange(Signals.RmsDb(x.AsSpan(Rate / 2)) - before, -0.05, 0.05);
    }

    [Fact]
    public void Gate_reopens_when_speech_returns()
    {
        var gate = new NoiseGate(Rate) { ThresholdDb = -40f };
        gate.Process(Signals.WhiteNoise(Rate, -70, 1));
        Assert.False(gate.IsOpen);
        var x = Signals.Sine(Rate, 300, -20, 0.1);
        gate.Process(x);
        Assert.True(gate.IsOpen);
        Assert.InRange(Signals.RmsDb(x.AsSpan(x.Length / 2)), -23.5, -22.5); // sine RMS = peak - 3 dB
    }

    [Fact]
    public void Gate_holds_briefly_before_closing()
    {
        var gate = new NoiseGate(Rate, holdMs: 120f) { ThresholdDb = -40f };
        gate.Process(Signals.Sine(Rate, 300, -20, 0.2));
        gate.Process(Signals.Silence(Rate, 0.08)); // shorter than the hold time
        Assert.True(gate.IsOpen);
        gate.Process(Signals.Silence(Rate, 0.3));
        Assert.False(gate.IsOpen);
    }

    [Theory]
    [InlineData(-30f, -30f)]   // below threshold: unchanged
    [InlineData(-10f, -18f)]   // 4:1 above -20: -20 + 10/4 = -17.5 (knee adds a little)
    public void Compressor_gain_curve_matches_ratio(float inputDb, float expectedApprox)
    {
        var s = new CompressorSettings(-20f, 4f, 0f, 5f, 100f, 0f);
        float y = Compressor.GainComputer(inputDb, s);
        Assert.InRange(y, expectedApprox - 0.6f, expectedApprox + 0.6f);
    }

    [Fact]
    public void Compressor_soft_knee_is_continuous()
    {
        var s = CompressorSettings.ForMode(CompressorMode.Voice);
        float prev = Compressor.GainComputer(-60f, s);
        for (float x = -59.9f; x <= 0f; x += 0.1f)
        {
            float y = Compressor.GainComputer(x, s);
            Assert.True(y >= prev - 1e-4f, "curve must be monotonic");
            Assert.True(y - prev < 0.11f, "curve must not jump");
            prev = y;
        }
    }

    [Fact]
    public void Voice_compressor_reduces_dynamic_range()
    {
        var comp = new Compressor(Rate, CompressorSettings.ForMode(CompressorMode.Voice));
        var loud = Signals.Sine(Rate, 300, -6, 1);
        var quiet = Signals.Sine(Rate, 300, -36, 1);
        Signals.ProcessInBlocks(loud, b => comp.Process(b));
        comp.Reset();
        Signals.ProcessInBlocks(quiet, b => comp.Process(b));
        double spreadIn = 30;
        double spreadOut = Signals.RmsDb(loud.AsSpan(Rate / 2)) - Signals.RmsDb(quiet.AsSpan(Rate / 2));
        Assert.True(spreadOut < spreadIn - 10, $"spread {spreadOut:F1} dB");
    }

    [Fact]
    public void Meter_reports_sine_peak_and_rms()
    {
        var meter = new LevelMeter(Rate);
        var x = Signals.Sine(Rate, 1000, -12, 3); // RMS window is 300 ms; let it settle
        Signals.ProcessInBlocks(x, b => meter.Process(b));
        Assert.InRange(meter.PeakDb, -12.1, -11.9);
        Assert.InRange(meter.RmsDb, -15.11, -14.91); // -12 - 3.01
        Assert.Equal(0, meter.ClipEvents);
    }

    [Fact]
    public void Meter_peak_decays_after_signal_stops()
    {
        var meter = new LevelMeter(Rate, peakDecayDbPerSecond: 20f);
        meter.Process(Signals.Sine(Rate, 1000, -6, 0.1));
        meter.Process(Signals.Silence(Rate, 1));
        Assert.InRange(meter.PeakDb, -26.5, -25.5);
    }

    [Fact]
    public void Meter_counts_clipping()
    {
        var meter = new LevelMeter(Rate);
        meter.Process(new float[] { 0.2f, 1.0f, -0.3f });
        Assert.Equal(1, meter.ClipEvents);
    }
}
