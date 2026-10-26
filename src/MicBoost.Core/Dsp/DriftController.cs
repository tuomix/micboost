namespace MicBoost.Core.Dsp;

/// <summary>
/// Keeps the FIFO between a capture device and a render device near a target fill level by
/// nudging the resampling ratio (the two devices run on independent clocks that drift apart).
/// </summary>
public sealed class DriftController
{
    private const double MaxCorrection = 0.004; // +/-0.4%, inaudible
    private double _avgFill = -1;

    public DriftController(int targetFillSamples)
    {
        TargetFill = Math.Max(1, targetFillSamples);
    }

    public int TargetFill { get; set; }

    /// <summary>Smoothed fill level, in samples.</summary>
    public double AverageFill => Math.Max(0, _avgFill);

    public double Correction { get; private set; } = 1.0;

    /// <summary>Feeds the current FIFO fill (samples) and returns the ratio correction to apply.</summary>
    public double Update(int fillSamples)
    {
        _avgFill = _avgFill < 0 ? fillSamples : _avgFill + 0.02 * (fillSamples - _avgFill);
        double error = (TargetFill - _avgFill) / TargetFill; // >0: too empty, produce more
        double c = 1.0 + Math.Clamp(error * 0.002, -MaxCorrection, MaxCorrection);
        Correction = c;
        return c;
    }

    /// <summary>True if the FIFO holds so much that we should drop data to restore low latency.</summary>
    public bool ShouldFlush(int fillSamples) => fillSamples > TargetFill * 4 + 512;

    public void Reset()
    {
        _avgFill = -1;
        Correction = 1.0;
    }
}
