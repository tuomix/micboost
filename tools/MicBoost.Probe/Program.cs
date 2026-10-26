using MicBoost.Audio;
using MicBoost.Core.Devices;
using MicBoost.Core.Dsp;
using MicBoost.Core.Settings;

// Developer probe. Usage:
//   MicBoost.Probe devices
//   MicBoost.Probe run <seconds> [output-name-substring] [gainDb] [--mute]
// --mute sends silence to the output (timing test on a real speaker device without making sound).
// Nothing is recorded; levels are printed only.

var cmd = args.Length > 0 ? args[0] : "devices";
using var devices = new DeviceService();
var captures = devices.GetCaptureDevices();
var renders = devices.GetRenderDevices();

if (cmd == "devices")
{
    Console.WriteLine("Capture devices:");
    foreach (var d in captures) Console.WriteLine($"  {(DevicePicker.IsHyperXCloud3(d.Name) ? "*" : " ")} {d.Name}");
    Console.WriteLine("Render devices:");
    foreach (var d in renders) Console.WriteLine($"    {d.Name}");
    Console.WriteLine($"VB-CABLE: {devices.GetVbCableState()}");
    var hx = DevicePicker.PickCapture(captures, null, null);
    if (hx is null)
    {
        Console.WriteLine("HyperX Cloud III microphone not found.");
        return 1;
    }
    Console.WriteLine($"Selected: {hx.Name}");
    var report = devices.ProbeHardwareBoost(hx.Id);
    Console.WriteLine($"Hardware boost probe: {report.Summary}");
    Console.WriteLine($"  endpoint step {report.EndpointStepDb} dB, topology controls: {report.TopologyVolumeControls.Count}");
    foreach (var c in report.TopologyVolumeControls) Console.WriteLine($"    {c.Name}: {c.MinDb} .. {c.MaxDb} dB (step {c.StepDb})");
    return 0;
}

if (cmd == "run")
{
    int seconds = args.Length > 1 ? int.Parse(args[1]) : 5;
    string? outName = args.Length > 2 ? args[2] : null;
    float gain = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 12f;
    var hx = DevicePicker.PickCapture(captures, null, null) ?? throw new InvalidOperationException("No HyperX mic");
    var output = outName is null ? null : renders.FirstOrDefault(r => r.Name.Contains(outName, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"Capture: {hx.Name}; output: {output?.Name ?? "(none)"}; gain {gain} dB");
    bool mute = args.Contains("--mute");
    using var engine = new AudioEngine { Settings = new ProcessorSettings { GainDb = gain, Mute = mute } };
    engine.Start(new EngineConfig(hx.Id, hx.Name, output?.Id, null, LatencyMode.Normal));
    var proc = System.Diagnostics.Process.GetCurrentProcess();
    var cpuStart = proc.TotalProcessorTime;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(500);
        var s = engine.Snapshot;
        var p = engine.Processor;
        Console.WriteLine($"{sw.Elapsed.TotalSeconds,5:0.0}s {s.Status,-16} in pk {p?.InputMeter.PeakDb,7:0.0} rms {p?.InputMeter.RmsDb,7:0.0} | " +
            $"out pk {p?.OutputMeter.PeakDb,7:0.0} rms {p?.OutputMeter.RmsDb,7:0.0} | GR {p?.GainReductionDb,5:0.0} | " +
            $"lat {s.LatencyMs,5:0.0} ms | underruns {s.Underruns} | {s.OutputMessage}");
        Console.WriteLine("        " + s.Diagnostics);
    }
    proc.Refresh();
    var cpu = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
    Console.WriteLine($"Capture format: {engine.Snapshot.CaptureFormat}; output format: {engine.Snapshot.OutputFormat}");
    Console.WriteLine($"Average CPU: {cpu:0.00}% of total machine");
    return 0;
}

Console.WriteLine("Unknown command");
return 2;
