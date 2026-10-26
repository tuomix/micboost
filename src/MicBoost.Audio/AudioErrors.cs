using System.Runtime.InteropServices;

namespace MicBoost.Audio;

public enum EngineStatus
{
    Stopped,
    Starting,
    Running,
    WaitingForDevice,
    Error,
}

/// <summary>Turns WASAPI/COM failures into messages a user can act on.</summary>
public static class AudioErrors
{
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    public const int AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED = unchecked((int)0x8889000E);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    public const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_NOTFOUND = unchecked((int)0x80070490);

    public static (EngineStatus Status, string Message) Classify(Exception ex, string deviceName)
    {
        int hr = ex is COMException com ? com.HResult : ex.HResult;
        return hr switch
        {
            AUDCLNT_E_DEVICE_INVALIDATED or AUDCLNT_E_RESOURCES_INVALIDATED or E_NOTFOUND =>
                (EngineStatus.WaitingForDevice, $"Waiting for {deviceName}… (unplugged or disabled)"),
            AUDCLNT_E_DEVICE_IN_USE or AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED =>
                (EngineStatus.Error, $"{deviceName} is being used in exclusive mode by another app. Close that app, or in " +
                    "Sound settings → the device → Properties → Advanced, untick \"Allow applications to take exclusive control\"."),
            E_ACCESSDENIED =>
                (EngineStatus.Error, "Windows is blocking microphone access. Open Settings → Privacy & security → Microphone " +
                    "and turn on \"Microphone access\" and \"Let desktop apps access your microphone\"."),
            AUDCLNT_E_SERVICE_NOT_RUNNING =>
                (EngineStatus.Error, "The Windows Audio service is not running."),
            AUDCLNT_E_UNSUPPORTED_FORMAT =>
                (EngineStatus.Error, $"{deviceName} uses an audio format MicBoost cannot open. Try a different default format " +
                    "in Sound settings → device Properties → Advanced."),
            _ when ex is TimeoutException =>
                (EngineStatus.WaitingForDevice, $"{deviceName} stopped delivering audio. Retrying…"),
            _ when ex is NotSupportedException =>
                (EngineStatus.Error, ex.Message),
            _ => (EngineStatus.Error, $"Audio error: {ex.Message} (0x{hr:X8})"),
        };
    }

    public static bool IsDeviceGone(Exception ex)
    {
        int hr = ex.HResult;
        return hr is AUDCLNT_E_DEVICE_INVALIDATED or AUDCLNT_E_RESOURCES_INVALIDATED or E_NOTFOUND;
    }
}
