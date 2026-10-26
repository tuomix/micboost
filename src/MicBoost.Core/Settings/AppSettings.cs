using MicBoost.Core.Dsp;

namespace MicBoost.Core.Settings;

public enum LatencyMode
{
    Low = 0,
    Normal = 1,
    Safe = 2,
}

/// <summary>Everything MicBoost remembers between runs. Serialised to %AppData%\MicBoost\settings.json.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Devices (null/empty = automatic)
    public string? CaptureDeviceId { get; set; }
    public string? CaptureDeviceName { get; set; }
    public string? OutputDeviceId { get; set; }
    public bool MonitorEnabled { get; set; }
    public string? MonitorDeviceId { get; set; }
    public bool MonitorWarningAccepted { get; set; }

    // Processing
    public float GainDb { get; set; } = 12f;
    public bool Bypass { get; set; }
    public bool GateEnabled { get; set; }
    public float GateThresholdDb { get; set; } = -50f;
    public CompressorMode Compressor { get; set; } = CompressorMode.Off;
    public string? ActivePreset { get; set; }
    public List<Preset> CustomPresets { get; set; } = new();

    // Behaviour
    public string HotkeyMute { get; set; } = "Ctrl+Alt+M";
    public string HotkeyBypass { get; set; } = "Ctrl+Alt+B";
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public LatencyMode Latency { get; set; } = LatencyMode.Normal;

    public ProcessorSettings ToProcessorSettings(bool mute = false) => new ProcessorSettings
    {
        GainDb = GainDb,
        Bypass = Bypass,
        Mute = mute,
        GateEnabled = GateEnabled,
        GateThresholdDb = GateThresholdDb,
        Compressor = Compressor,
    }.Normalized();

    public void ApplyProcessorSettings(ProcessorSettings s)
    {
        GainDb = s.GainDb;
        Bypass = s.Bypass;
        GateEnabled = s.GateEnabled;
        GateThresholdDb = s.GateThresholdDb;
        Compressor = s.Compressor;
    }

    /// <summary>Clamps values loaded from disk into valid ranges.</summary>
    public void Sanitize()
    {
        var p = ToProcessorSettings().Normalized();
        GainDb = p.GainDb;
        GateThresholdDb = p.GateThresholdDb;
        if (!Enum.IsDefined(Compressor)) Compressor = CompressorMode.Off;
        if (!Enum.IsDefined(Latency)) Latency = LatencyMode.Normal;
        CustomPresets ??= new();
        CustomPresets = CustomPresets
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name) && !BuiltInPresets.IsReservedName(x.Name))
            .Select(x => x with
            {
                Name = x.Name.Trim(),
                GainDb = Math.Clamp(float.IsFinite(x.GainDb) ? x.GainDb : 0f, ProcessorSettings.MinGainDb, ProcessorSettings.MaxGainDb),
                GateThresholdDb = Math.Clamp(float.IsFinite(x.GateThresholdDb) ? x.GateThresholdDb : -50f,
                    ProcessorSettings.MinGateThresholdDb, ProcessorSettings.MaxGateThresholdDb),
                Compressor = Enum.IsDefined(x.Compressor) ? x.Compressor : CompressorMode.Off,
            })
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        HotkeyMute ??= "";
        HotkeyBypass ??= "";
    }
}
