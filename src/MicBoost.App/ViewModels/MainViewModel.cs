using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using MicBoost.App.Services;
using MicBoost.Audio;
using MicBoost.Core.Devices;
using MicBoost.Core.Dsp;
using MicBoost.Core.Settings;

namespace MicBoost.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const string ReleasesUrl = "https://github.com/tuomix/micboost/releases";

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly DeviceService _devices;
    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly string? _settingsLoadError;

    private bool _refreshing;
    private bool _syncingPreset;
    private DeviceEntry? _selectedCapture;
    private DeviceEntry? _selectedOutput;
    private DeviceEntry? _selectedMonitor;
    private bool _muted;
    private Preset? _selectedPreset;
    private string _gainText = "";
    private VbCableState _cableState = VbCableState.Ready;
    private string _statusText = "Starting…";
    private string _statusKind = "idle";
    private string? _engineMessage;
    private string? _outputMessage;
    private string _latencyText = "—";
    private string _formatText = "";
    private string _hardwareText = "";
    private string? _probedDeviceId;
    private string? _hotkeyError;
    private string? _startupError;
    private bool _startWithWindows;

    public MainViewModel(AppSettings settings, SettingsStore store, DeviceService devices, AudioEngine engine, string? settingsLoadError)
    {
        _settings = settings;
        _store = store;
        _devices = devices;
        _engine = engine;
        _settingsLoadError = settingsLoadError;
        _startWithWindows = StartupManager.IsEnabled;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        LatencyModes = Enum.GetValues<LatencyMode>();
        ReloadPresets();
        _gainText = FormatGain(_settings.GainDb);

        DownloadCableCommand = new RelayCommand(() => OpenUrl(DevicePicker.VbCableDownloadUrl));
        RecheckCommand = new RelayCommand(RefreshDevices);
        CheckUpdatesCommand = new RelayCommand(() => OpenUrl(ReleasesUrl));
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => _selectedPreset is { IsBuiltIn: false });
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
        ToggleBypassCommand = new RelayCommand(() => Bypass = !Bypass);
        SavePresetCommand = new RelayCommand(SavePresetInteractive);

        _engine.Settings = _settings.ToProcessorSettings(_muted);
        RefreshDevices();
        SyncPresetSelection();
        _statusTimer.Start();
    }

    /// <summary>Raised when hotkey text changes and the hotkeys need re-registering.</summary>
    public event EventHandler? HotkeysChanged;

    /// <summary>Raised when mute/bypass/status change (tray icon refresh).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Asks the user to confirm turning on monitoring (feedback warning).</summary>
    public Func<bool>? ConfirmMonitor { get; set; }

    /// <summary>Asks the user for a preset name (null = cancelled).</summary>
    public Func<string?, string?>? AskPresetName { get; set; }

    public AudioEngine Engine => _engine;

    // ---------------------------------------------------------------- Devices

    public ObservableCollection<DeviceEntry> CaptureDevices { get; } = new();
    public ObservableCollection<DeviceEntry> OutputDevices { get; } = new();
    public ObservableCollection<DeviceEntry> MonitorDevices { get; } = new();

    public DeviceEntry? SelectedCapture
    {
        get => _selectedCapture;
        set
        {
            if (!SetProperty(ref _selectedCapture, value) || _refreshing || value is null) return;
            _settings.CaptureDeviceId = value.Id;
            _settings.CaptureDeviceName = value.Name;
            ScheduleSave();
            ApplyEngine();
            ProbeHardware();
        }
    }

    public string CapturePlaceholder => string.IsNullOrEmpty(_settings.CaptureDeviceName)
        ? "HyperX Cloud III not found: plug it in or pick a microphone"
        : $"Waiting for {_settings.CaptureDeviceName}…";

    public DeviceEntry? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!SetProperty(ref _selectedOutput, value) || _refreshing || value is null) return;
            _settings.OutputDeviceId = DevicePicker.IsVbCableInput(value.Name) ? null : value.Id;
            ScheduleSave();
            ApplyEngine();
            OnPropertyChanged(nameof(OutputIsCable));
        }
    }

    public bool OutputIsCable => _selectedOutput is not null && DevicePicker.IsVbCableInput(_selectedOutput.Name);

    public string OutputPlaceholder => _cableState == VbCableState.Ready ? "Select an output" : "VB-CABLE not installed";

    public bool MonitorEnabled
    {
        get => _settings.MonitorEnabled;
        set
        {
            if (value == _settings.MonitorEnabled) return;
            if (value && !(ConfirmMonitor?.Invoke() ?? true))
            {
                OnPropertyChanged(); // revert the switch
                return;
            }
            _settings.MonitorEnabled = value;
            OnPropertyChanged();
            ScheduleSave();
            ApplyEngine();
        }
    }

    public DeviceEntry? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetProperty(ref _selectedMonitor, value) || _refreshing || value is null) return;
            _settings.MonitorDeviceId = value.Id;
            ScheduleSave();
            ApplyEngine();
        }
    }

    public bool ShowCableBanner => _cableState != VbCableState.Ready;

    public string CableBannerTitle => _cableState == VbCableState.Disabled
        ? "VB-CABLE is installed but disabled"
        : "VB-CABLE is not installed";

    public string CableBannerText => _cableState == VbCableState.Disabled
        ? "Open Sound settings → All sound devices, and enable \"CABLE Input\" and \"CABLE Output\". Then click Check again."
        : "MicBoost sends the boosted mic into the free VB-CABLE virtual cable so Discord, games and Windows can use it. " +
          "1) Download VB-CABLE  2) unzip it  3) right-click VBCABLE_Setup_x64.exe → Run as administrator → Install Driver  " +
          "4) restart Windows, then come back here.";

    public void RefreshDevices()
    {
        _refreshing = true;
        try
        {
            var captures = _devices.GetCaptureDevices().Where(d => !DevicePicker.IsVbCableOutput(d.Name)).ToList();
            Replace(CaptureDevices, captures);
            var cap = DevicePicker.PickCapture(captures, _settings.CaptureDeviceId, _settings.CaptureDeviceName);
            if (cap is not null && (cap.Id != _settings.CaptureDeviceId || cap.Name != _settings.CaptureDeviceName))
            {
                // First run (auto-detected) or the id changed after re-plugging: remember it.
                _settings.CaptureDeviceId = cap.Id;
                _settings.CaptureDeviceName = cap.Name;
                ScheduleSave();
            }
            SetProperty(ref _selectedCapture, cap, nameof(SelectedCapture));
            OnPropertyChanged(nameof(CapturePlaceholder));

            var renders = _devices.GetRenderDevices();
            Replace(OutputDevices, renders);
            SetProperty(ref _selectedOutput, DevicePicker.PickOutput(renders, _settings.OutputDeviceId), nameof(SelectedOutput));
            OnPropertyChanged(nameof(OutputIsCable));

            var monitors = renders.Where(d => !DevicePicker.IsVbCableInput(d.Name)).ToList();
            Replace(MonitorDevices, monitors);
            var defaultRender = _devices.GetDefaultRender();
            var monitor = monitors.FirstOrDefault(d => d.Id == _settings.MonitorDeviceId)
                ?? monitors.FirstOrDefault(d => d.Id == defaultRender?.Id && !DevicePicker.IsVbCableInput(d.Name))
                ?? monitors.FirstOrDefault(d => DevicePicker.IsHyperXCloud3(d.Name));
            SetProperty(ref _selectedMonitor, monitor, nameof(SelectedMonitor));

            _cableState = _devices.GetVbCableState();
            OnPropertyChanged(nameof(ShowCableBanner));
            OnPropertyChanged(nameof(CableBannerTitle));
            OnPropertyChanged(nameof(CableBannerText));
            OnPropertyChanged(nameof(OutputPlaceholder));
        }
        finally
        {
            _refreshing = false;
        }
        ApplyEngine();
        _engine.Kick();
        ProbeHardware();
    }

    private void ApplyEngine()
    {
        string? capId = _selectedCapture?.Id ?? _settings.CaptureDeviceId;
        string capName = _selectedCapture?.Name ?? _settings.CaptureDeviceName ?? "the microphone";
        if (capId is null)
        {
            _engine.Stop();
            return;
        }
        string? outId = _selectedOutput?.Id ?? _settings.OutputDeviceId;
        string? monId = _settings.MonitorEnabled ? _selectedMonitor?.Id : null;
        if (monId is not null && monId == outId) monId = null;
        _engine.Start(new EngineConfig(capId, capName, outId, monId, _settings.Latency));
    }

    private void ProbeHardware()
    {
        var id = _selectedCapture?.Id;
        if (id is null || id == _probedDeviceId) return;
        _probedDeviceId = id;
        HardwareText = _devices.ProbeHardwareBoost(id).Summary;
    }

    // ---------------------------------------------------------------- Processing

    public double GainDb
    {
        get => _settings.GainDb;
        set
        {
            float v = (float)Math.Round(Math.Clamp(value, ProcessorSettings.MinGainDb, ProcessorSettings.MaxGainDb), 1);
            if (v == _settings.GainDb) return;
            _settings.GainDb = v;
            OnPropertyChanged();
            SetProperty(ref _gainText, FormatGain(v), nameof(GainText));
            ProcessingChanged();
        }
    }

    /// <summary>Editable gain text. Accepts "18", "18.5", "18,5", "+18 dB".</summary>
    public string GainText
    {
        get => _gainText;
        set
        {
            if (TryParseDb(value, out var db)) GainDb = db;
            // Always re-format (also reverts invalid input).
            _gainText = FormatGain(_settings.GainDb);
            OnPropertyChanged();
        }
    }

    public bool Bypass
    {
        get => _settings.Bypass;
        set
        {
            if (value == _settings.Bypass) return;
            _settings.Bypass = value;
            OnPropertyChanged();
            ProcessingChanged(updatePreset: false);
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value)) return;
            ProcessingChanged(updatePreset: false, save: false);
        }
    }

    public bool GateEnabled
    {
        get => _settings.GateEnabled;
        set
        {
            if (value == _settings.GateEnabled) return;
            _settings.GateEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GateMarkerDb));
            ProcessingChanged();
        }
    }

    public double GateThresholdDb
    {
        get => _settings.GateThresholdDb;
        set
        {
            float v = (float)Math.Round(Math.Clamp(value, ProcessorSettings.MinGateThresholdDb, ProcessorSettings.MaxGateThresholdDb));
            if (v == _settings.GateThresholdDb) return;
            _settings.GateThresholdDb = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GateThresholdText));
            OnPropertyChanged(nameof(GateMarkerDb));
            ProcessingChanged();
        }
    }

    public string GateThresholdText => $"{_settings.GateThresholdDb:0} dB";

    /// <summary>Gate threshold marker for the input meter (NaN when the gate is off).</summary>
    public double GateMarkerDb => _settings.GateEnabled ? _settings.GateThresholdDb : double.NaN;

    public CompressorMode Compressor
    {
        get => _settings.Compressor;
        set
        {
            if (value == _settings.Compressor) return;
            _settings.Compressor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CompOff));
            OnPropertyChanged(nameof(CompLight));
            OnPropertyChanged(nameof(CompVoice));
            ProcessingChanged();
        }
    }

    public bool CompOff { get => Compressor == CompressorMode.Off; set { if (value) Compressor = CompressorMode.Off; } }
    public bool CompLight { get => Compressor == CompressorMode.Light; set { if (value) Compressor = CompressorMode.Light; } }
    public bool CompVoice { get => Compressor == CompressorMode.Voice; set { if (value) Compressor = CompressorMode.Voice; } }

    public string LimiterText =>
        $"Always on · ceiling {ProcessorSettings.DefaultCeilingDb.ToString("0.0", CultureInfo.InvariantCulture)} dBFS";

    private void ProcessingChanged(bool updatePreset = true, bool save = true)
    {
        _engine.Settings = _settings.ToProcessorSettings(_muted);
        if (updatePreset) SyncPresetSelection();
        if (save) ScheduleSave();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- Presets

    public ObservableCollection<Preset> Presets { get; } = new();

    public Preset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;
            CommandManager.InvalidateRequerySuggested();
            if (_syncingPreset || value is null) return;
            ApplyPreset(value);
        }
    }

    public IEnumerable<string> PresetNames => Presets.Select(p => p.Name);

    public void ApplyPresetByName(string name)
    {
        var p = Presets.FirstOrDefault(x => x.Name == name);
        if (p is not null) SelectedPreset = p;
    }

    private void ApplyPreset(Preset p)
    {
        _settings.ApplyProcessorSettings(p.ApplyTo(_settings.ToProcessorSettings()));
        _settings.ActivePreset = p.Name;
        _gainText = FormatGain(_settings.GainDb);
        foreach (var name in new[] { nameof(GainDb), nameof(GainText), nameof(GateEnabled), nameof(GateThresholdDb),
                     nameof(GateThresholdText), nameof(GateMarkerDb), nameof(Compressor), nameof(CompOff), nameof(CompLight), nameof(CompVoice) })
            OnPropertyChanged(name);
        ProcessingChanged(updatePreset: false);
    }

    private void SyncPresetSelection()
    {
        var current = _settings.ToProcessorSettings();
        var match = Presets.FirstOrDefault(p => Matches(p, current));
        _syncingPreset = true;
        try { SelectedPreset = match; }
        finally { _syncingPreset = false; }
        _settings.ActivePreset = match?.Name;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Matches(Preset p, ProcessorSettings s) =>
        Math.Abs(p.GainDb - s.GainDb) < 0.05f && p.GateEnabled == s.GateEnabled && p.Compressor == s.Compressor
        && (!p.GateEnabled || Math.Abs(p.GateThresholdDb - s.GateThresholdDb) < 0.5f);

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var p in BuiltInPresets.All) Presets.Add(p);
        foreach (var p in _settings.CustomPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) Presets.Add(p);
        OnPropertyChanged(nameof(PresetNames));
    }

    private void SavePresetInteractive()
    {
        var suggested = _selectedPreset is { IsBuiltIn: false } ? _selectedPreset.Name : "";
        var name = AskPresetName?.Invoke(suggested)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (BuiltInPresets.IsReservedName(name))
        {
            PresetError = $"\"{name}\" is a built-in preset name. Choose another name.";
            return;
        }
        PresetError = null;
        var preset = Preset.FromSettings(name, _settings.ToProcessorSettings());
        _settings.CustomPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.CustomPresets.Add(preset);
        ReloadPresets();
        SyncPresetSelection();
        ScheduleSave();
    }

    private void DeletePreset()
    {
        if (_selectedPreset is not { IsBuiltIn: false } p) return;
        _settings.CustomPresets.RemoveAll(x => x.Name == p.Name);
        ReloadPresets();
        SyncPresetSelection();
        ScheduleSave();
    }

    private string? _presetError;

    public string? PresetError
    {
        get => _presetError;
        private set => SetProperty(ref _presetError, value);
    }

    // ---------------------------------------------------------------- Status

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>"ok", "warn", "error", "muted" or "idle" (drives the status pill colour).</summary>
    public string StatusKind { get => _statusKind; private set => SetProperty(ref _statusKind, value); }

    public string? EngineMessage { get => _engineMessage; private set => SetProperty(ref _engineMessage, value); }

    public string? OutputMessage { get => _outputMessage; private set => SetProperty(ref _outputMessage, value); }

    public string LatencyText { get => _latencyText; private set => SetProperty(ref _latencyText, value); }

    public string FormatText { get => _formatText; private set => SetProperty(ref _formatText, value); }

    public string HardwareText { get => _hardwareText; private set => SetProperty(ref _hardwareText, value); }

    public string? SettingsLoadError => _settingsLoadError is null ? null
        : "Your settings file could not be read, so defaults are used. A copy was kept as settings.corrupt.json.";

    public TrayState TrayState { get; private set; }

    public string TrayTooltip { get; private set; } = "MicBoost";

    private void UpdateStatus()
    {
        var s = _engine.Snapshot;
        string text, kind;
        switch (s.Status)
        {
            case EngineStatus.Running when _muted:
                text = "Muted"; kind = "muted"; break;
            case EngineStatus.Running when _settings.Bypass:
                text = "Bypassed"; kind = "idle"; break;
            case EngineStatus.Running when !s.OutputActive:
                text = "Live · no output"; kind = "warn"; break;
            case EngineStatus.Running:
                text = "Live"; kind = "ok"; break;
            case EngineStatus.WaitingForDevice:
                text = "Waiting for device"; kind = "warn"; break;
            case EngineStatus.Error:
                text = "Error"; kind = "error"; break;
            case EngineStatus.Starting:
                text = "Starting…"; kind = "idle"; break;
            default:
                text = _settings.CaptureDeviceId is null ? "No microphone" : "Stopped"; kind = "warn"; break;
        }
        StatusText = text;
        StatusKind = kind;
        EngineMessage = s.Status is EngineStatus.Error or EngineStatus.WaitingForDevice ? s.Message
            : _settings.CaptureDeviceId is null ? CapturePlaceholder : null;
        OutputMessage = s.Status == EngineStatus.Running && !ShowCableBanner ? s.OutputMessage : null;
        LatencyText = s.Status == EngineStatus.Running && !double.IsNaN(s.LatencyMs) ? $"≈ {s.LatencyMs:0} ms" : "—";
        FormatText = s.CaptureFormat is null ? "" :
            $"Mic: {s.CaptureFormat}" + (s.OutputFormat is null ? "" : $" · Output: {s.OutputFormat}") +
            (s.Underruns > 0 ? $" · dropouts: {s.Underruns}" : "");

        var trayState = _muted ? TrayState.Muted
            : _settings.Bypass ? TrayState.Bypassed
            : kind is "warn" or "error" ? TrayState.Problem
            : TrayState.Normal;
        string tooltip = $"MicBoost · {text} · +{_settings.GainDb:0.#} dB";
        if (trayState != TrayState || tooltip != TrayTooltip)
        {
            TrayState = trayState;
            TrayTooltip = tooltip;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- App settings

    public IReadOnlyList<LatencyMode> LatencyModes { get; }

    public LatencyMode Latency
    {
        get => _settings.Latency;
        set
        {
            if (value == _settings.Latency) return;
            _settings.Latency = value;
            OnPropertyChanged();
            ScheduleSave();
            ApplyEngine();
        }
    }

    public string HotkeyMute
    {
        get => _settings.HotkeyMute;
        set
        {
            if (value == _settings.HotkeyMute) return;
            _settings.HotkeyMute = value;
            OnPropertyChanged();
            ScheduleSave();
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string HotkeyBypass
    {
        get => _settings.HotkeyBypass;
        set
        {
            if (value == _settings.HotkeyBypass) return;
            _settings.HotkeyBypass = value;
            OnPropertyChanged();
            ScheduleSave();
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? HotkeyError { get => _hotkeyError; set => SetProperty(ref _hotkeyError, value); }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            try
            {
                StartupManager.SetEnabled(value);
                StartupError = null;
            }
            catch (Exception ex)
            {
                StartupError = "Could not change the startup setting: " + ex.Message;
            }
            SetProperty(ref _startWithWindows, StartupManager.IsEnabled);
            OnPropertyChanged();
        }
    }

    public string? StartupError { get => _startupError; private set => SetProperty(ref _startupError, value); }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { if (value == _settings.StartMinimized) return; _settings.StartMinimized = value; OnPropertyChanged(); ScheduleSave(); }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { if (value == _settings.CloseToTray) return; _settings.CloseToTray = value; OnPropertyChanged(); ScheduleSave(); }
    }

    public string VersionText
    {
        get
        {
            var v = typeof(MainViewModel).Assembly.GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public ICommand DownloadCableCommand { get; }
    public ICommand RecheckCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenSettingsFolderCommand { get; }
    public ICommand SavePresetCommand { get; }
    public ICommand DeletePresetCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleBypassCommand { get; }

    // ---------------------------------------------------------------- Helpers

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try { _store.Save(_settings); }
        catch (Exception) { /* settings are a convenience; never crash over them */ }
    }

    private void OpenSettingsFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(_store.FilePath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    /// <summary>Opens a fixed https URL in the user's browser. MicBoost itself makes no network calls.</summary>
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private static string FormatGain(float db) => $"+{db.ToString("0.0", CultureInfo.InvariantCulture)}";

    public static bool TryParseDb(string? text, out double db)
    {
        db = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Replace("+", "").Replace(',', '.').Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out db) && double.IsFinite(db);
    }

    private static void Replace(ObservableCollection<DeviceEntry> target, IReadOnlyList<DeviceEntry> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _saveTimer.Stop();
        SaveNow();
    }
}
