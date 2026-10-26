using MicBoost.Core.Dsp;
using MicBoost.Core.Settings;

namespace MicBoost.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "micboost-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Round_trips_settings_and_custom_presets()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var s = new AppSettings
        {
            CaptureDeviceId = "{0.0.1.00000000}.{abc}",
            CaptureDeviceName = "Microphone (HyperX Cloud III)",
            GainDb = 21.5f,
            GateEnabled = true,
            Compressor = CompressorMode.Voice,
            HotkeyMute = "Ctrl+Shift+F9",
            CustomPresets = { new Preset { Name = "Late night", GainDb = 25f, Compressor = CompressorMode.Light } },
        };
        store.Save(s);
        var loaded = store.Load();
        Assert.Null(store.LastLoadError);
        Assert.Equal(s.CaptureDeviceId, loaded.CaptureDeviceId);
        Assert.Equal(21.5f, loaded.GainDb);
        Assert.Equal(CompressorMode.Voice, loaded.Compressor);
        Assert.Equal("Ctrl+Shift+F9", loaded.HotkeyMute);
        Assert.Single(loaded.CustomPresets);
        Assert.Equal("Late night", loaded.CustomPresets[0].Name);
        Assert.False(loaded.MonitorEnabled);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_keeps_a_copy()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);
        var loaded = store.Load();
        Assert.NotNull(store.LastLoadError);
        Assert.Equal(12f, loaded.GainDb);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.corrupt.json")));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_load()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            { "GainDb": 80, "GateThresholdDb": -300, "Compressor": "Voice",
              "CustomPresets": [ { "Name": "Discord", "GainDb": 5 }, { "Name": "  Mine ", "GainDb": 99 } ] }
            """);
        var loaded = new SettingsStore(path).Load();
        Assert.Equal(30f, loaded.GainDb);
        Assert.Equal(-80f, loaded.GateThresholdDb);
        var preset = Assert.Single(loaded.CustomPresets); // built-in name is rejected
        Assert.Equal("Mine", preset.Name);
        Assert.Equal(30f, preset.GainDb);
    }

    [Fact]
    public void Builtin_presets_exist_with_expected_names()
    {
        Assert.Equal(new[] { "Quiet room", "Noisy room", "Discord" }, BuiltInPresets.All.Select(p => p.Name));
        Assert.All(BuiltInPresets.All, p => Assert.InRange(p.GainDb, 0f, 30f));
    }

    [Fact]
    public void Preset_apply_keeps_bypass_and_mute()
    {
        var current = new ProcessorSettings { Bypass = true, Mute = true, GainDb = 3f };
        var applied = BuiltInPresets.Discord.ApplyTo(current);
        Assert.True(applied.Bypass);
        Assert.True(applied.Mute);
        Assert.Equal(BuiltInPresets.Discord.GainDb, applied.GainDb);
    }

    [Theory]
    [InlineData("Ctrl+Alt+M", true, "Ctrl+Alt+M")]
    [InlineData("alt + ctrl + b", true, "Ctrl+Alt+B")]
    [InlineData("F9", true, "F9")]
    [InlineData("M", false, "")]
    [InlineData("Ctrl+A+B", false, "")]
    [InlineData("", false, "")]
    public void Hotkeys_parse(string text, bool ok, string normalized)
    {
        Assert.Equal(ok, HotkeyGesture.TryParse(text, out var g));
        if (ok) Assert.Equal(normalized, g.ToString());
    }

    [Fact]
    public void Pcm_conversion_round_trips_all_encodings()
    {
        var mono = new float[] { 0f, 0.5f, -0.5f, 0.999f, -1f };
        foreach (var enc in Enum.GetValues<SampleEncoding>())
        {
            int channels = 2;
            var bytes = new byte[mono.Length * channels * PcmConvert.BytesPerSample(enc)];
            PcmConvert.FromMono(mono, enc, channels, bytes);
            var back = new float[mono.Length];
            PcmConvert.ToMono(bytes, enc, channels, back);
            for (int i = 0; i < mono.Length; i++) Assert.Equal(mono[i], back[i], 3);
        }
    }
}
