using System.Text.Json.Serialization;
using MicBoost.Core.Dsp;

namespace MicBoost.Core.Settings;

/// <summary>A named set of processing values.</summary>
public sealed record Preset
{
    public string Name { get; init; } = "";
    public float GainDb { get; init; } = 12f;
    public bool GateEnabled { get; init; }
    public float GateThresholdDb { get; init; } = -50f;
    public CompressorMode Compressor { get; init; } = CompressorMode.Off;

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    public ProcessorSettings ApplyTo(ProcessorSettings current) => current with
    {
        GainDb = GainDb,
        GateEnabled = GateEnabled,
        GateThresholdDb = GateThresholdDb,
        Compressor = Compressor,
    };

    public static Preset FromSettings(string name, ProcessorSettings s) => new()
    {
        Name = name,
        GainDb = s.GainDb,
        GateEnabled = s.GateEnabled,
        GateThresholdDb = s.GateThresholdDb,
        Compressor = s.Compressor,
    };
}

public static class BuiltInPresets
{
    public static readonly Preset QuietRoom = new()
    {
        Name = "Quiet room", GainDb = 15f, GateEnabled = false, GateThresholdDb = -60f,
        Compressor = CompressorMode.Light, IsBuiltIn = true,
    };

    public static readonly Preset NoisyRoom = new()
    {
        Name = "Noisy room", GainDb = 12f, GateEnabled = true, GateThresholdDb = -42f,
        Compressor = CompressorMode.Voice, IsBuiltIn = true,
    };

    public static readonly Preset Discord = new()
    {
        Name = "Discord", GainDb = 18f, GateEnabled = true, GateThresholdDb = -55f,
        Compressor = CompressorMode.Voice, IsBuiltIn = true,
    };

    public static IReadOnlyList<Preset> All { get; } = new[] { QuietRoom, NoisyRoom, Discord };

    public static bool IsReservedName(string name) =>
        All.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
