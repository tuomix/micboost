using NAudio.CoreAudioApi;

namespace MicBoost.Audio;

public sealed record HardwareControl(string Name, float MinDb, float MaxDb, float StepDb);

public sealed record HardwareBoostReport(
    string DeviceName,
    float EndpointMinDb,
    float EndpointMaxDb,
    float EndpointStepDb,
    float EndpointCurrentDb,
    IReadOnlyList<HardwareControl> TopologyVolumeControls,
    string? Error)
{
    /// <summary>True if any hardware control can amplify above 0 dB.</summary>
    public bool HasNativeBoost =>
        EndpointMaxDb > 0.01f || TopologyVolumeControls.Any(c => c.MaxDb > 0.01f);

    public string Summary
    {
        get
        {
            if (Error is not null) return $"Could not read hardware controls: {Error}";
            var s = $"Endpoint volume range {EndpointMinDb:0.#} to {EndpointMaxDb:+0.#;-0.#;0} dB (now {EndpointCurrentDb:0.#} dB).";
            if (TopologyVolumeControls.Count > 0)
                s += " Driver controls: " + string.Join("; ", TopologyVolumeControls.Select(c =>
                    $"{c.Name} {c.MinDb:0.#}…{c.MaxDb:+0.#;-0.#;0} dB"));
            s += HasNativeBoost
                ? " A native boost above 0 dB exists; MicBoost still uses software gain so it works the same on every device."
                : " No native boost above 0 dB: software gain is required.";
            return s;
        }
    }
}

/// <summary>
/// Looks for a driver-level microphone boost: an endpoint volume range above 0 dB, or volume parts in the
/// device topology (where "Microphone Boost" lives on drivers that have it).
/// </summary>
public static class HardwareBoostProbe
{
    public static HardwareBoostReport Probe(MMDeviceEnumerator enumerator, string deviceId)
    {
        string name = deviceId;
        try
        {
            using var device = enumerator.GetDevice(deviceId);
            name = device.FriendlyName;
            var vol = device.AudioEndpointVolume;
            var range = vol.VolumeRange;
            float current = vol.MasterVolumeLevel;
            var controls = WalkTopology(device);
            return new HardwareBoostReport(name, range.MinDecibels, range.MaxDecibels, range.IncrementDecibels, current, controls, null);
        }
        catch (Exception ex)
        {
            return new HardwareBoostReport(name, 0, 0, 0, 0, Array.Empty<HardwareControl>(), ex.Message);
        }
    }

    private static List<HardwareControl> WalkTopology(MMDevice device)
    {
        var controls = new List<HardwareControl>();
        try
        {
            var topology = device.DeviceTopology;
            var seen = new HashSet<string>();
            var queue = new Queue<Part>();
            for (uint c = 0; c < topology.ConnectorCount; c++)
            {
                try
                {
                    var connector = topology.GetConnector(c);
                    if (!connector.IsConnected) continue;
                    var other = connector.ConnectedTo;
                    queue.Enqueue(other.Part);
                }
                catch (Exception) { }
            }

            while (queue.Count > 0 && seen.Count < 64)
            {
                var part = queue.Dequeue();
                string id;
                try { id = part.GlobalId; } catch (Exception) { continue; }
                if (!seen.Add(id)) continue;

                TryReadVolume(part, controls);
                foreach (var list in new[] { TryGet(() => part.PartsIncoming), TryGet(() => part.PartsOutgoing) })
                {
                    if (list is null) continue;
                    for (uint i = 0; i < list.Count; i++)
                    {
                        var next = TryGet(() => list[i]);
                        if (next is not null) queue.Enqueue(next);
                    }
                }
            }
        }
        catch (Exception) { }
        return controls;
    }

    private static void TryReadVolume(Part part, List<HardwareControl> controls)
    {
        try
        {
            var level = part.AudioVolumeLevel;
            if (level is null || level.ChannelCount == 0) return;
            level.GetLevelRange(0, out float min, out float max, out float step);
            string name = TryGet(() => part.Name) ?? "Volume";
            if (string.IsNullOrWhiteSpace(name)) name = "Volume";
            controls.Add(new HardwareControl(name, min, max, step));
        }
        catch (Exception) { /* part has no volume interface */ }
    }

    private static T? TryGet<T>(Func<T> f) where T : class
    {
        try { return f(); } catch (Exception) { return null; }
    }
}
