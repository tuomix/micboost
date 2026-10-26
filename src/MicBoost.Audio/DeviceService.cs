using MicBoost.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicBoost.Audio;

public enum VbCableState
{
    NotInstalled,
    Disabled,
    Ready,
}

/// <summary>Enumerates audio endpoints and reports plug/unplug events. Create and use on one thread (the UI thread).</summary>
public sealed class DeviceService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly NotificationClient _notifications;
    private readonly Timer _debounce;
    private bool _disposed;

    public DeviceService()
    {
        _debounce = new Timer(_ => DevicesChanged?.Invoke(this, EventArgs.Empty));
        _notifications = new NotificationClient(() => _debounce.Change(400, Timeout.Infinite));
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
    }

    /// <summary>Raised (on a thread-pool thread, debounced) when devices are added, removed, enabled or disabled.</summary>
    public event EventHandler? DevicesChanged;

    public IReadOnlyList<DeviceEntry> GetCaptureDevices() => List(DataFlow.Capture);

    public IReadOnlyList<DeviceEntry> GetRenderDevices() => List(DataFlow.Render);

    public DeviceEntry? GetDefaultRender()
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new DeviceEntry(d.ID, d.FriendlyName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Checks whether VB-CABLE is installed and enabled (looks for its "CABLE Input" playback device).</summary>
    public VbCableState GetVbCableState()
    {
        bool disabled = false;
        foreach (var (name, state) in ListAllStates(DataFlow.Render))
        {
            if (!DevicePicker.IsVbCableInput(name)) continue;
            if (state == DeviceState.Active) return VbCableState.Ready;
            if (state is DeviceState.Disabled or DeviceState.Unplugged) disabled = true;
        }
        return disabled ? VbCableState.Disabled : VbCableState.NotInstalled;
    }

    public HardwareBoostReport ProbeHardwareBoost(string deviceId) => HardwareBoostProbe.Probe(_enumerator, deviceId);

    private List<DeviceEntry> List(DataFlow flow)
    {
        var result = new List<DeviceEntry>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d)
                {
                    try { result.Add(new DeviceEntry(d.ID, d.FriendlyName)); }
                    catch (Exception) { /* device vanished while enumerating */ }
                }
            }
        }
        catch (Exception) { }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private IEnumerable<(string Name, DeviceState State)> ListAllStates(DataFlow flow)
    {
        var result = new List<(string, DeviceState)>();
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged))
            {
                using (d)
                {
                    try { result.Add((d.FriendlyName, d.State)); }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception) { }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch (Exception) { }
        _debounce.Dispose();
        _enumerator.Dispose();
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly Action _changed;

        public NotificationClient(Action changed) => _changed = changed;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string pwstrDeviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
