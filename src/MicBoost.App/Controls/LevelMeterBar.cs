using System;
using System.Windows;
using System.Windows.Media;

namespace MicBoost.App.Controls;

/// <summary>
/// Horizontal level meter: a filled bar for RMS, a thin line for peak, dB ticks and an optional
/// marker (used for the noise-gate threshold). Drawn directly for low CPU use.
/// </summary>
public sealed class LevelMeterBar : FrameworkElement
{
    public static readonly DependencyProperty RmsDbProperty = DependencyProperty.Register(
        nameof(RmsDb), typeof(double), typeof(LevelMeterBar), new FrameworkPropertyMetadata(-120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakDbProperty = DependencyProperty.Register(
        nameof(PeakDb), typeof(double), typeof(LevelMeterBar), new FrameworkPropertyMetadata(-120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerDbProperty = DependencyProperty.Register(
        nameof(MarkerDb), typeof(double), typeof(LevelMeterBar), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public const double MinDb = -60;

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x33)));
    private static readonly Brush PeakBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEF)));
    private static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x0F, 0x10, 0x12)), 1));
    private static readonly Pen MarkerPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0xB1, 0x4C)), 2));

    public double RmsDb
    {
        get => (double)GetValue(RmsDbProperty);
        set => SetValue(RmsDbProperty, value);
    }

    public double PeakDb
    {
        get => (double)GetValue(PeakDbProperty);
        set => SetValue(PeakDbProperty, value);
    }

    /// <summary>Optional marker position in dBFS; NaN hides it.</summary>
    public double MarkerDb
    {
        get => (double)GetValue(MarkerDbProperty);
        set => SetValue(MarkerDbProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 14);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var full = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track, null, full, 4, 4);

        double rms = Fraction(RmsDb) * w;
        if (rms > 0.5)
        {
            var gradient = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(w, 0),
            };
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x36, 0xD3, 0x99), 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x36, 0xD3, 0x99), Fraction(-18)));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0xB1, 0x4C), Fraction(-9)));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0x54, 0x5B), Fraction(-3)));
            gradient.Freeze();
            dc.PushClip(new RectangleGeometry(full, 4, 4));
            dc.DrawRectangle(gradient, null, new Rect(0, 0, rms, h));
            dc.Pop();
        }

        foreach (double tick in new[] { -48.0, -36, -24, -18, -12, -6 })
        {
            double x = Math.Round(Fraction(tick) * w) + 0.5;
            dc.DrawLine(TickPen, new Point(x, 0), new Point(x, h));
        }

        double peak = Fraction(PeakDb) * w;
        if (peak > 1)
            dc.DrawRectangle(PeakBrush, null, new Rect(Math.Min(w - 2, peak - 1), 0, 2, h));

        if (!double.IsNaN(MarkerDb))
        {
            double x = Math.Round(Fraction(MarkerDb) * w);
            dc.DrawLine(MarkerPen, new Point(x, -2), new Point(x, h + 2));
        }
    }

    private static double Fraction(double db) => Math.Clamp((db - MinDb) / -MinDb, 0, 1);

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
