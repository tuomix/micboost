using MicBoost.Core.Devices;

namespace MicBoost.Core.Tests;

public class DevicePickerTests
{
    private static readonly DeviceEntry HyperX = new("id-hx", "Microphone (HyperX Cloud III)");
    private static readonly DeviceEntry Ngenuity = new("id-ng", "NGENUITY - Microphone (HyperX Virtual Audio Device)");
    private static readonly DeviceEntry Webcam = new("id-cam", "Microphone (HD Pro Webcam C920)");
    private static readonly DeviceEntry CableOut = new("id-co", "CABLE Output (VB-Audio Virtual Cable)");
    private static readonly DeviceEntry CableIn = new("id-ci", "CABLE Input (VB-Audio Virtual Cable)");
    private static readonly DeviceEntry Speakers = new("id-sp", "Headphones (HyperX Cloud III)");

    [Fact]
    public void Picks_physical_hyperx_not_ngenuity_virtual_device()
    {
        var picked = DevicePicker.PickCapture(new[] { Ngenuity, Webcam, HyperX }, null, null);
        Assert.Equal(HyperX, picked);
    }

    [Fact]
    public void Remembered_device_wins_and_falls_back_to_name_when_id_changes()
    {
        var devices = new[] { HyperX, Webcam };
        Assert.Equal(Webcam, DevicePicker.PickCapture(devices, "id-cam", null));
        Assert.Equal(Webcam, DevicePicker.PickCapture(devices, "stale-id", Webcam.Name));
        Assert.Equal(HyperX, DevicePicker.PickCapture(devices, "stale-id", "Unplugged mic"));
    }

    [Fact]
    public void Never_picks_cable_output_as_input_and_returns_null_when_headset_missing()
    {
        Assert.Null(DevicePicker.PickCapture(new[] { CableOut, Webcam }, "id-co", null));
    }

    [Fact]
    public void Output_defaults_to_cable_input()
    {
        Assert.Equal(CableIn, DevicePicker.PickOutput(new[] { Speakers, CableIn }, null));
        Assert.Null(DevicePicker.PickOutput(new[] { Speakers }, null));
        Assert.Equal(Speakers, DevicePicker.PickOutput(new[] { Speakers, CableIn }, "id-sp"));
    }

    [Fact]
    public void Cable_names_are_recognised()
    {
        Assert.True(DevicePicker.IsVbCableInput(CableIn.Name));
        Assert.True(DevicePicker.IsVbCableOutput(CableOut.Name));
        Assert.False(DevicePicker.IsVbCableInput(CableOut.Name));
        Assert.False(DevicePicker.IsHyperXCloud3(Speakers.Name.Replace("Cloud III", "Cloud II")));
    }
}
