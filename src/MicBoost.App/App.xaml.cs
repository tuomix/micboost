using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MicBoost.App.Services;
using MicBoost.App.ViewModels;
using MicBoost.App.Views;
using MicBoost.Audio;
using MicBoost.Core.Settings;

namespace MicBoost.App;

public partial class App : Application
{
    // Inno Setup's AppMutex uses the same name to detect a running MicBoost during install/uninstall.
    public const string MutexName = "MicBoost-SingleInstance-7f3c2a";
    private const string ShowEventName = "MicBoost-Show-7f3c2a";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private DeviceService? _devices;
    private AudioEngine? _engine;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Already running: ask that instance to show its window, then quit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (Exception) { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var showEvent = _showEvent;
        var showThread = new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne())
                    Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
            }
            catch (ObjectDisposedException) { /* shutting down */ }
        }) { IsBackground = true, Name = "MicBoost show listener" };
        showThread.Start();

        DispatcherUnhandledException += OnUnhandledException;

        // MICBOOST_SETTINGS_DIR lets developers run MicBoost without touching the real %AppData% settings.
        string settingsDir = Environment.GetEnvironmentVariable("MICBOOST_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : SettingsStore.DefaultDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();

        _devices = new DeviceService();
        _engine = new AudioEngine();
        _vm = new MainViewModel(settings, store, _devices, _engine, store.LastLoadError);
        _window = new MainWindow(_vm);
        _vm.ConfirmMonitor = () => Dialogs.Confirm(_window, "Turn on monitoring?",
            "Monitoring plays your boosted microphone back to you. With speakers, or with a high boost, this can create echo or a loud " +
            "feedback howl. Use headphones and keep the volume low.\n\nOther apps never hear the monitor; it only affects what you hear.",
            "Turn on monitoring");
        _vm.AskPresetName = initial => Dialogs.AskText(_window, "Save preset", "Preset name", initial ?? "");

        _devices.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(() => _vm.RefreshDevices());

        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/micboost.ico"))!.Stream;
        using (var baseIcon = new System.Drawing.Icon(iconStream))
        {
            _tray = new TrayIcon(baseIcon,
                show: () => _window.ShowFromTray(),
                toggleMute: () => _vm.Muted = !_vm.Muted,
                toggleBypass: () => _vm.Bypass = !_vm.Bypass,
                exit: ExitApp);
        }
        _vm.StateChanged += (_, _) => UpdateTray();
        _vm.Presets.CollectionChanged += (_, _) => UpdateTrayPresets();
        _vm.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(MainViewModel.SelectedPreset)) UpdateTrayPresets(); };
        _window.HiddenToTray += (_, _) => _tray.ShowStillRunningOnce();

        // Hotkeys need a window handle even if the window starts hidden.
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        _hotkeys = new HotkeyManager(HwndSource.FromHwnd(handle));
        _vm.HotkeysChanged += (_, _) => RegisterHotkeys();
        RegisterHotkeys();
        UpdateTray();
        UpdateTrayPresets();

        StartupManager.RefreshPathIfEnabled();

        var screenshot = Array.IndexOf(e.Args, "--screenshot");
        if (screenshot >= 0 && screenshot + 1 < e.Args.Length)
        {
            SaveScreenshot(e.Args[screenshot + 1]);
            return;
        }

        bool minimized = settings.StartMinimized || e.Args.Contains("--minimized");
        if (!minimized) _window.Show();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null || _vm is null) return;
        _hotkeys.UnregisterAll();
        var errors = new[]
        {
            _hotkeys.Register(_vm.HotkeyMute, () => _vm.Muted = !_vm.Muted),
            _hotkeys.Register(_vm.HotkeyBypass, () => _vm.Bypass = !_vm.Bypass),
        }.Where(x => x is not null);
        _vm.HotkeyError = errors.Any() ? string.Join(" ", errors) : null;
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _vm is null) return;
        _tray.Update(_vm.TrayState, _vm.Muted, _vm.Bypass, _vm.TrayTooltip, _vm.HotkeyMute, _vm.HotkeyBypass);
    }

    private void UpdateTrayPresets()
    {
        if (_tray is null || _vm is null) return;
        _tray.SetPresets(_vm.PresetNames.ToList(), _vm.SelectedPreset?.Name, name => _vm.ApplyPresetByName(name));
    }

    private void ExitApp()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _vm?.Dispose();
        _engine?.Dispose();
        _devices?.Dispose();
        _showEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch (Exception) { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show($"Something went wrong, but MicBoost keeps running.\n\n{e.Exception.Message}", "MicBoost",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Developer option: renders the window to a PNG (used for the README screenshot), then exits.</summary>
    private void SaveScreenshot(string path)
    {
        _window!.Left = -10000;
        _window.Height = 1010;
        _window.ShowInTaskbar = false;
        _window.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var content = (FrameworkElement)_window.Content;
            double scale = 1.5;
            var bmp = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen())
                dc.DrawRectangle((Brush)FindResource("BgBrush"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bmp.Render(background);
            bmp.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
            ExitApp();
        };
        timer.Start();
    }
}
