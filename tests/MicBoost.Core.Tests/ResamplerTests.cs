using MicBoost.Core.Dsp;

namespace MicBoost.Core.Tests;

public class ResamplerTests
{
    private static float[] Run(Resampler r, float[] input)
    {
        var output = new List<float>();
        var buf = new float[r.MaxOutputLength(1024)];
        Signals.ProcessInBlocks(input, block =>
        {
            int n = r.Process(block, buf);
            output.AddRange(buf.AsSpan(0, n).ToArray());
        });
        return output.ToArray();
    }

    [Theory]
    [InlineData(48000, 44100)]
    [InlineData(44100, 48000)]
    [InlineData(48000, 48000)]
    [InlineData(16000, 48000)]
    [InlineData(96000, 48000)]
    public void Preserves_frequency_amplitude_and_duration(int inRate, int outRate)
    {
        var r = new Resampler(inRate, outRate);
        var input = Signals.Sine(inRate, 1000, -6, 2);
        var output = Run(r, input);

        double expectedLength = input.Length * (double)outRate / inRate;
        // The filter holds back HalfTaps (16) input samples of look-ahead at the end of the stream.
        double filterTail = 17.0 * outRate / inRate + 2;
        Assert.InRange(output.Length, expectedLength - filterTail, expectedLength + 1);

        var steady = output.AsSpan(outRate / 4, outRate);
        Assert.InRange(Signals.EstimateFrequency(steady, outRate), 999.5, 1000.5);
        Assert.InRange(Signals.RmsDb(steady), -9.11, -8.91); // -6 dBFS sine → -9.01 dB RMS
    }

    [Fact]
    public void Downsampling_suppresses_content_above_new_nyquist()
    {
        // 30 kHz tone at 96 kHz must not alias into the 48 kHz output.
        var r = new Resampler(96000, 48000);
        var output = Run(r, Signals.Sine(96000, 30000, -6, 1));
        Assert.True(Signals.RmsDb(output.AsSpan(4800)) < -60, $"alias level {Signals.RmsDb(output.AsSpan(4800)):F1} dB");
    }

    [Fact]
    public void Correction_changes_output_rate_slightly()
    {
        var nominal = Run(new Resampler(48000, 48000), Signals.Sine(48000, 500, -6, 1));
        var faster = new Resampler(48000, 48000);
        faster.SetCorrection(1.004);
        var more = Run(faster, Signals.Sine(48000, 500, -6, 1));
        Assert.InRange(more.Length - nominal.Length, 150, 230); // ~0.4% of 48000
    }

    [Fact]
    public void Drift_controller_splices_out_large_surplus()
    {
        var dc = new DriftController(targetSlack: 240, windowSamples: 4800);
        for (int i = 0; i < 10; i++) dc.ObserveRead(fillBeforeRead: 3000, requested: 480);
        Assert.Equal(3000 - 480 - 240, dc.TakePendingSkip());
        Assert.Equal(0, dc.TakePendingSkip());
        Assert.Equal(1, dc.Splices);
    }

    [Fact]
    public void Drift_controller_nudges_ratio_for_small_errors()
    {
        var dc = new DriftController(targetSlack: 240, windowSamples: 480);
        for (int i = 0; i < 50; i++) dc.ObserveRead(480 + 40, 480); // slack 40 < 240: running low
        Assert.True(dc.Correction > 1.0);
        Assert.True(dc.Correction <= 1.001);
        dc.Reset();
        for (int i = 0; i < 50; i++) dc.ObserveRead(480 + 400, 480); // slack 400: a bit high, below splice level
        Assert.True(dc.Correction < 1.0);
        Assert.Equal(0, dc.TakePendingSkip());
    }

    [Fact]
    public void Fifo_splice_removes_samples_without_a_jump()
    {
        var fifo = new SampleFifo(4096);
        var ramp = Enumerable.Range(0, 2000).Select(i => MathF.Sin(i * 0.01f)).ToArray();
        fifo.Write(ramp);
        fifo.SpliceSkip(500, 48);
        Assert.Equal(1500, fifo.Count);
        var outBuf = new float[1500];
        fifo.Read(outBuf);
        for (int i = 1; i < outBuf.Length; i++)
            Assert.True(MathF.Abs(outBuf[i] - outBuf[i - 1]) < 0.1f, $"jump at {i}");
        Assert.Equal(ramp[1000], outBuf[500]);
    }

    [Fact]
    public void Fifo_reads_back_in_order_and_drops_oldest_when_full()
    {
        var fifo = new SampleFifo(16);
        fifo.Write(Enumerable.Range(0, 10).Select(i => (float)i).ToArray());
        var tmp = new float[4];
        Assert.Equal(4, fifo.Read(tmp));
        Assert.Equal(new float[] { 0, 1, 2, 3 }, tmp);
        int dropped = fifo.Write(Enumerable.Range(10, 12).Select(i => (float)i).ToArray());
        Assert.Equal(2, dropped);
        var rest = new float[32];
        int n = fifo.Read(rest);
        Assert.Equal(16, n);
        Assert.Equal(6f, rest[0]);
        Assert.Equal(21f, rest[15]);
    }
}
