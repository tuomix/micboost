namespace MicBoost.Core.Devices;

/// <summary>A device as seen by the picker (platform-neutral).</summary>
public sealed record DeviceEntry(string Id, string Name);

/// <summary>Name matching and selection rules for the input and output devices.</summary>
public static class DevicePicker
{
    public const string VbCableDownloadUrl = "https://vb-audio.com/Cable/";

    /// <summary>The physical HyperX Cloud III microphone (not the NGENUITY virtual devices).</summary>
    public static bool IsHyperXCloud3(string name) =>
        Contains(name, "HyperX") && Contains(name, "Cloud III") && !Contains(name, "NGENUITY") && !Contains(name, "Virtual");

    /// <summary>VB-CABLE playback side. Audio sent here comes out of "CABLE Output" (a recording device).</summary>
    public static bool IsVbCableInput(string name) => Contains(name, "CABLE Input") && Contains(name, "VB-Audio");

    /// <summary>VB-CABLE recording side, i.e. what other apps should pick as their microphone.</summary>
    public static bool IsVbCableOutput(string name) => Contains(name, "CABLE Output") && Contains(name, "VB-Audio");

    /// <summary>
    /// Chooses the capture device: the remembered id, then the remembered name (ids can change when a USB
    /// device moves port), then any HyperX Cloud III. Returns null if nothing suitable is connected.
    /// </summary>
    public static DeviceEntry? PickCapture(IReadOnlyList<DeviceEntry> devices, string? savedId, string? savedName)
    {
        var usable = devices.Where(d => !IsVbCableOutput(d.Name)).ToList();
        if (!string.IsNullOrEmpty(savedId))
        {
            var byId = usable.FirstOrDefault(d => d.Id == savedId);
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrEmpty(savedName))
        {
            var byName = usable.FirstOrDefault(d => string.Equals(d.Name, savedName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }
        return usable.FirstOrDefault(d => IsHyperXCloud3(d.Name));
    }

    /// <summary>Chooses the output: the remembered id if present, else VB-CABLE's "CABLE Input".</summary>
    public static DeviceEntry? PickOutput(IReadOnlyList<DeviceEntry> renderDevices, string? savedId)
    {
        if (!string.IsNullOrEmpty(savedId))
        {
            var byId = renderDevices.FirstOrDefault(d => d.Id == savedId);
            if (byId is not null) return byId;
        }
        return renderDevices.FirstOrDefault(d => IsVbCableInput(d.Name));
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
