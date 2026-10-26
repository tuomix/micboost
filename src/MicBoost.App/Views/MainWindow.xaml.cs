using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MicBoost.App.Services;
using MicBoost.App.ViewModels;
using MicBoost.Core.Dsp;
using MicBoost.Core.Settings;

namespace MicBoost.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ClipHold = TimeSpan.FromSeconds(1.5);
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _meterTimer;
    private readonly Brush _ledOff;
    private readonly Brush _ledOn;
    private long _inClipSeen = -1, _outClipSeen = -1;
    private DateTime _inClipUntil, _outClipUntil;
    private MicProcessor? _lastProcessor;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _ledOff = (Brush)FindResource("Surface3Brush");
        _ledOn = (Brush)FindResource("DangerBrush");

        // ~30 fps meters; paused while the window is hidden or minimized to keep CPU use near zero.
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        SourceInitialized += (_, _) => NativeWindow.UseDarkTitleBar(this);
        IsVisibleChanged += (_, _) => UpdateTimer();
        StateChanged += (_, _) => UpdateTimer();
    }

    /// <summary>Set by the app when the user chose Exit, so closing really closes.</summary>
    public bool AllowClose { get; set; }

    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _vm.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // bring to front reliably
        Topmost = false;
        Focus();
    }

    private void UpdateTimer()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized;
        if (active && !_meterTimer.IsEnabled) _meterTimer.Start();
        else if (!active && _meterTimer.IsEnabled) _meterTimer.Stop();
    }

    private void UpdateMeters()
    {
        var p = _vm.Engine.Processor;
        if (p is null)
        {
            SetMeter(InputMeter, InputText, Db.Floor, Db.Floor);
            SetMeter(OutputMeter, OutputText, Db.Floor, Db.Floor);
            GainReductionText.Text = "0.0 dB";
            InputClip.Background = _ledOff;
            OutputClip.Background = _ledOff;
            _lastProcessor = null;
            return;
        }
        if (!ReferenceEquals(p, _lastProcessor))
        {
            _lastProcessor = p;
            _inClipSeen = p.InputMeter.ClipEvents;
            _outClipSeen = p.OutputMeter.ClipEvents;
        }

        SetMeter(InputMeter, InputText, p.InputMeter.PeakDb, p.InputMeter.RmsDb);
        SetMeter(OutputMeter, OutputText, p.OutputMeter.PeakDb, p.OutputMeter.RmsDb);
        GainReductionText.Text = $"{p.GainReductionDb.ToString("0.0", CultureInfo.InvariantCulture)} dB";

        var now = DateTime.UtcNow;
        long inClip = p.InputMeter.ClipEvents, outClip = p.OutputMeter.ClipEvents;
        if (inClip != _inClipSeen) { _inClipSeen = inClip; _inClipUntil = now + ClipHold; }
        if (outClip != _outClipSeen) { _outClipSeen = outClip; _outClipUntil = now + ClipHold; }
        InputClip.Background = now < _inClipUntil ? _ledOn : _ledOff;
        OutputClip.Background = now < _outClipUntil ? _ledOn : _ledOff;
    }

    private static void SetMeter(Controls.LevelMeterBar bar, TextBlock text, float peakDb, float rmsDb)
    {
        bar.PeakDb = peakDb;
        bar.RmsDb = rmsDb;
        text.Text = rmsDb <= -90 ? "-∞" : $"{rmsDb.ToString("0.0", CultureInfo.InvariantCulture)}";
    }

    private void GainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ((TextBox)sender).SelectAll();
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        bool isMute = (string)box.Tag == "mute";
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return; // keep keyboard navigation
        e.Handled = true;

        if (key is Key.Back or Key.Delete)
        {
            if (isMute) _vm.HotkeyMute = ""; else _vm.HotkeyBypass = "";
            return;
        }
        var gesture = HotkeyManager.GestureFromKeyEvent(e);
        if (gesture is null) return; // only modifiers so far
        if (!HotkeyGesture.TryParse(gesture, out _))
        {
            _vm.HotkeyError = "Use at least one modifier (Ctrl, Alt, Shift or Win), or a function key.";
            return;
        }
        if (isMute) _vm.HotkeyMute = gesture; else _vm.HotkeyBypass = gesture;
    }
}
