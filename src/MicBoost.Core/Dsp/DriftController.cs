namespace MicBoost.Core.Dsp;

/// <summary>
/// Keeps the FIFO between a capture device and a render device small but never empty.
/// <para>
/// At every render read the controller records the "slack": samples left in the FIFO after the read.
/// Once per window it looks at the minimum slack (the closest we came to an underrun):
/// a large surplus is removed at once by a short cross-faded splice (<see cref="TakePendingSkip"/>),
/// and slow clock drift between the two devices is corrected by nudging the resampling ratio.
/// </para>
/// </summary>
public sealed class DriftController
{
    private const double MaxCorrection = 0.001; // +/-0.1%: inaudible, more than enough for clock drift
    private readonly int _windowSamples;
    private int _windowMin = int.MaxValue;
    private int _consumed;
    private double _avgSlack = double.NaN;
    private double _avgFill = double.NaN;
    private int _pendingSkip;

    /// <param name="targetSlack">Safety margin, in samples, that should remain in the FIFO at its emptiest.</param>
    /// <param name="windowSamples">How many consumed samples make up one observation window.</param>
    public DriftController(int targetSlack, int windowSamples)
    {
        TargetSlack = Math.Max(1, targetSlack);
        _windowSamples = Math.Max(1, windowSamples);
    }

    public int TargetSlack { get; set; }

    /// <summary>Smoothed per-window minimum slack, in samples.</summary>
    public double AverageSlack => double.IsNaN(_avgSlack) ? 0 : _avgSlack;

    /// <summary>Smoothed FIFO fill just before each read, in samples (used for latency estimates).</summary>
    public double AverageFill => double.IsNaN(_avgFill) ? 0 : _avgFill;

    /// <summary>Ratio correction to apply to the resampler (above 1 = produce more samples).</summary>
    public double Correction { get; private set; } = 1.0;

    public long Splices { get; private set; }

    /// <summary>Call before every render read with the FIFO fill and the number of samples about to be read.</summary>
    public void ObserveRead(int fillBeforeRead, int requested)
    {
        _avgFill = double.IsNaN(_avgFill) ? fillBeforeRead : _avgFill + 0.05 * (fillBeforeRead - _avgFill);
        int slack = fillBeforeRead - requested;
        if (slack < _windowMin) _windowMin = slack;
        _consumed += requested;
        if (_consumed >= _windowSamples) EndWindow();
    }

    /// <summary>Samples to splice out of the FIFO now (0 if none). Resets after reading.</summary>
    public int TakePendingSkip()
    {
        int s = _pendingSkip;
        _pendingSkip = 0;
        return s;
    }

    public void Reset()
    {
        _windowMin = int.MaxValue;
        _consumed = 0;
        _avgSlack = double.NaN;
        _avgFill = double.NaN;
        _pendingSkip = 0;
        Correction = 1.0;
    }

    private void EndWindow()
    {
        int min = _windowMin;
        _windowMin = int.MaxValue;
        _consumed = 0;

        // A surplus of more than one target beyond the margin is cut out immediately.
        if (min > TargetSlack * 2)
        {
            _pendingSkip = min - TargetSlack;
            Splices++;
            min = TargetSlack;
        }

        _avgSlack = double.IsNaN(_avgSlack) ? min : _avgSlack + 0.25 * (min - _avgSlack);
        double error = (TargetSlack - _avgSlack) / TargetSlack; // >0: too close to empty, produce more
        Correction = 1.0 + Math.Clamp(error * 0.0005, -MaxCorrection, MaxCorrection);
    }
}
