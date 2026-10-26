using System.Runtime.InteropServices;
using MicBoost.Core.Dsp;
using MicBoost.Core.Settings;
using NAudio.CoreAudioApi;

namespace MicBoost.Audio;

/// <summary>What the engine should run. Null output/monitor ids mean "none".</summary>
public sealed record EngineConfig(
    string CaptureDeviceId,
    string CaptureDeviceName,
    string? OutputDeviceId,
    string? MonitorDeviceId,
    LatencyMode Latency);

/// <summary>Snapshot of engine state for the UI. All values are safe to read from any thread.</summary>
public sealed record EngineSnapshot
{
    public EngineStatus Status { get; init; }
    public string Message { get; init; } = "";
    public string? OutputMessage { get; init; }
    public bool OutputActive { get; init; }
    public bool MonitorActive { get; init; }
    public string? CaptureFormat { get; init; }
    public string? OutputFormat { get; init; }
    public double LatencyMs { get; init; } = double.NaN;
    public long Underruns { get; init; }
    public string Diagnostics { get; init; } = "";
}

/// <summary>
/// Runs capture → <see cref="MicProcessor"/> → resample → render on one dedicated MMCSS thread.
/// Survives unplugging: on any device failure it reports the problem and retries every 2 s, or immediately
/// when <see cref="Kick"/> is called (e.g. from a device-change notification).
/// Audio is only ever held in small in-memory buffers; nothing is recorded or written to disk.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private EngineConfig? _config;
    private int _generation;
    private volatile bool _kick;
    private volatile bool _disposed;
    private volatile bool _degraded;
    private ProcessorSettings _settings = new();
    private volatile MicProcessor? _processor;
    private volatile EngineSnapshot _snapshot = new() { Status = EngineStatus.Stopped, Message = "Stopped" };

    public AudioEngine()
    {
        _thread = new Thread(SupervisorLoop) { IsBackground = true, Name = "MicBoost audio", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Raised on the audio thread whenever <see cref="Snapshot"/>'s status or message changes.</summary>
    public event EventHandler? StatusChanged;

    public EngineSnapshot Snapshot => _snapshot;

    /// <summary>The active processor (for meters), or null when not running.</summary>
    public MicProcessor? Processor => _processor;

    public ProcessorSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            Volatile.Write(ref _settings, value.Normalized());
            var p = _processor;
            if (p is not null) p.Settings = value;
        }
    }

    public EngineConfig? Config
    {
        get { lock (_lock) return _config; }
    }

    /// <summary>Starts or reconfigures the engine. Passing the same config again does nothing.</summary>
    public void Start(EngineConfig config)
    {
        lock (_lock)
        {
            if (_config == config) return;
            _config = config;
            _generation++;
        }
        _wake.Set();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_config is null) return;
            _config = null;
            _generation++;
        }
        _wake.Set();
    }

    /// <summary>Asks the engine to retry now if it is waiting for a device or running without an output.</summary>
    public void Kick()
    {
        _kick = true;
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) { _config = null; _generation++; }
        _wake.Set();
        _thread.Join(3000);
        _wake.Dispose();
    }

    private void SupervisorLoop()
    {
        using var mmcss = Mmcss.Enter();
        while (!_disposed)
        {
            EngineConfig? cfg;
            int gen;
            lock (_lock) { cfg = _config; gen = _generation; }

            if (cfg is null)
            {
                _processor = null;
                Publish(new EngineSnapshot { Status = EngineStatus.Stopped, Message = "Stopped" });
                _wake.WaitOne();
                continue;
            }

            try
            {
                Publish(new EngineSnapshot { Status = EngineStatus.Starting, Message = $"Opening {cfg.CaptureDeviceName}…" });
                RunSession(cfg, gen);
                continue; // config changed or restart requested
            }
            catch (Exception ex)
            {
                _processor = null;
                var (status, message) = AudioErrors.Classify(ex, cfg.CaptureDeviceName);
                Publish(new EngineSnapshot { Status = status, Message = message });
            }

            _kick = false;
            _wake.WaitOne(TimeSpan.FromSeconds(2));
        }
    }

    private void RunSession(EngineConfig cfg, int gen)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var capture = new CaptureStream(enumerator, cfg.CaptureDeviceId);
        double marginMs = cfg.Latency switch { LatencyMode.Low => 1.5, LatencyMode.Safe => 10, _ => 3 };

        var outputs = new List<RenderStream>();
        string? outputMessage = null;
        RenderStream? main = null;
        bool monitorActive = false;
        try
        {
            if (cfg.OutputDeviceId is not null)
            {
                try
                {
                    main = new RenderStream(enumerator, cfg.OutputDeviceId, capture.Format.SampleRate, marginMs);
                    outputs.Add(main);
                }
                catch (Exception ex) when (ex is COMException or NotSupportedException or ArgumentException)
                {
                    outputMessage = "Output device unavailable: " + AudioErrors.Classify(ex, "the output device").Message;
                }
            }
            else
            {
                outputMessage = "No output device: install VB-CABLE so other apps can use the boosted mic.";
            }

            if (cfg.MonitorDeviceId is not null)
            {
                try
                {
                    outputs.Add(new RenderStream(enumerator, cfg.MonitorDeviceId, capture.Format.SampleRate, marginMs));
                    monitorActive = true;
                }
                catch (Exception ex) when (ex is COMException or NotSupportedException or ArgumentException)
                {
                    outputMessage = (outputMessage is null ? "" : outputMessage + " ") + "Monitor device unavailable.";
                }
            }

            _degraded = main is null && cfg.OutputDeviceId is not null || cfg.MonitorDeviceId is not null && !monitorActive;

            var processor = new MicProcessor(capture.Format.SampleRate, Settings);
            _processor = processor;

            WaitHandle[] handleArray = Array.Empty<WaitHandle>();
            void RebuildHandles()
            {
                var handles = new List<WaitHandle> { _wake, capture.Event };
                handles.AddRange(outputs.Select(o => o.Event));
                handleArray = handles.ToArray();
            }
            RebuildHandles();

            // A failing output (e.g. VB-CABLE or headphones removed) is dropped; capture and meters keep running.
            void ServiceOutput(RenderStream o)
            {
                try
                {
                    o.Service();
                }
                catch (COMException)
                {
                    outputs.Remove(o);
                    o.Dispose();
                    if (ReferenceEquals(o, main))
                    {
                        main = null;
                        outputMessage = "The output device was disconnected. Waiting for it to come back…";
                    }
                    else
                    {
                        monitorActive = false;
                        outputMessage = "The monitor device was disconnected.";
                    }
                    _degraded = true;
                    RebuildHandles();
                    Publish(MakeSnapshot(cfg, capture, main, monitorActive, outputMessage, processor));
                }
            }

            void ServiceAll()
            {
                foreach (var o in outputs.ToArray()) ServiceOutput(o);
            }

            foreach (var o in outputs) o.Start();
            capture.Start();

            int silentWaits = 0;
            long lastPublish = 0;
            MonoSink sink = mono =>
            {
                processor.Process(mono);
                foreach (var o in outputs) o.Push(mono);
            };

            Publish(MakeSnapshot(cfg, capture, main, monitorActive, outputMessage, processor));

            while (true)
            {
                int idx = WaitHandle.WaitAny(handleArray, 500);
                if (idx == WaitHandle.WaitTimeout)
                {
                    if (++silentWaits >= 6) throw new TimeoutException("Capture stalled");
                    ServiceAll(); // keep outputs playing silence
                    continue;
                }

                if (idx == 0)
                {
                    lock (_lock) { if (_generation != gen) return; }
                    if (_kick) { _kick = false; if (_degraded) return; }
                    continue;
                }

                if (idx == 1)
                {
                    silentWaits = 0;
                    capture.Drain(sink);
                    // WaitAny reports only the lowest signalled handle, so service the outputs here as well.
                    ServiceAll();
                }
                else
                {
                    ServiceOutput(outputs[idx - 2]);
                }

                long now = Environment.TickCount64;
                if (now - lastPublish > 250)
                {
                    lastPublish = now;
                    Publish(MakeSnapshot(cfg, capture, main, monitorActive, outputMessage, processor));
                }
            }
        }
        finally
        {
            foreach (var o in outputs) o.Dispose();
        }
    }

    private static EngineSnapshot MakeSnapshot(EngineConfig cfg, CaptureStream capture, RenderStream? main,
        bool monitorActive, string? outputMessage, MicProcessor processor)
    {
        double latency = double.NaN;
        if (main is not null)
        {
            double rate = main.Format.SampleRate;
            latency = capture.PeriodMs + capture.StreamLatencyMs
                + processor.LatencySamples * 1000.0 / capture.Format.SampleRate
                + main.Resampler.LatencyInputSamples * 1000.0 / capture.Format.SampleRate
                + Math.Max(0, main.Drift.AverageFill - main.PeriodFrames / 2.0) * 1000.0 / rate
                + main.AveragePaddingFrames * 1000.0 / rate
                + main.StreamLatencyMs;
        }
        return new EngineSnapshot
        {
            Status = EngineStatus.Running,
            Message = $"Running · {capture.Name}",
            OutputMessage = outputMessage,
            OutputActive = main is not null,
            MonitorActive = monitorActive,
            CaptureFormat = capture.Format.ToString(),
            OutputFormat = main?.Format.ToString(),
            LatencyMs = latency,
            Underruns = main?.Underruns ?? 0,
            Diagnostics = main is null ? $"capture period {capture.PeriodMs:0.0} ms" :
                $"cap period {capture.PeriodMs:0.0} ms, fifo avg {main.Drift.AverageFill:0}, slack {main.Drift.AverageSlack:0} " +
                $"(target {main.Drift.TargetSlack}), splices {main.Splices}, pad avg {main.AveragePaddingFrames:0}, out period {main.PeriodFrames}, " +
                $"out lat {main.StreamLatencyMs:0.0} ms, margin {main.MarginMs} ms, corr {main.Drift.Correction:0.0000}",
        };
    }

    private void Publish(EngineSnapshot snapshot)
    {
        var old = _snapshot;
        _snapshot = snapshot;
        if (old.Status != snapshot.Status || old.Message != snapshot.Message || old.OutputMessage != snapshot.OutputMessage
            || old.MonitorActive != snapshot.MonitorActive || old.OutputActive != snapshot.OutputActive)
        {
            try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch (Exception) { }
        }
    }

    /// <summary>Registers the thread with the Multimedia Class Scheduler so audio keeps priority under load.</summary>
    private sealed class Mmcss : IDisposable
    {
        private readonly IntPtr _handle;

        private Mmcss(IntPtr handle) => _handle = handle;

        public static Mmcss Enter()
        {
            int index = 0;
            IntPtr h = IntPtr.Zero;
            try { h = AvSetMmThreadCharacteristics("Pro Audio", ref index); } catch (Exception) { }
            return new Mmcss(h);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                try { AvRevertMmThreadCharacteristics(_handle); } catch (Exception) { }
            }
        }

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    }
}
