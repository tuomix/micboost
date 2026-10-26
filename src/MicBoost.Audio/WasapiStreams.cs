using System.Runtime.InteropServices;
using MicBoost.Core.Dsp;
using NAudio.CoreAudioApi;

namespace MicBoost.Audio;

/// <summary>Shared-mode, event-driven WASAPI capture. Must be created and used on the engine thread.</summary>
internal sealed class CaptureStream : IDisposable
{
    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private byte[] _bytes = new byte[4096];
    private float[] _mono = new float[1024];

    public CaptureStream(MMDeviceEnumerator enumerator, string deviceId)
    {
        _device = enumerator.GetDevice(deviceId);
        try
        {
            if (_device.State != DeviceState.Active)
                throw new COMException("Device not active", AudioErrors.E_NOTFOUND);
            Name = _device.FriendlyName;
            _client = _device.AudioClient;
            var mix = _client.MixFormat;
            Format = DeviceFormat.From(mix);
            long period = _client.DefaultDevicePeriod;
            // Buffer of 2 periods (min 20 ms) gives headroom if the engine thread is briefly late.
            long buffer = Math.Max(period * 2, 200_000);
            _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, buffer, 0, mix, Guid.Empty);
            Event = new AutoResetEvent(false);
            _client.SetEventHandle(Event.SafeWaitHandle.DangerousGetHandle());
            _capture = _client.AudioCaptureClient;
            PeriodMs = period / 10_000.0;
            StreamLatencyMs = _client.StreamLatency / 10_000.0;
        }
        catch
        {
            _client?.Dispose();
            _device.Dispose();
            throw;
        }
    }

    public string Name { get; } = "";

    public DeviceFormat Format { get; }

    public AutoResetEvent Event { get; } = null!;

    public double PeriodMs { get; }

    public double StreamLatencyMs { get; }

    public long Discontinuities { get; private set; }

    public void Start() => _client.Start();

    /// <summary>Reads every pending packet, converting each to mono and handing it to <paramref name="sink"/>.</summary>
    public int Drain(MonoSink sink)
    {
        int total = 0;
        int packet;
        while ((packet = _capture.GetNextPacketSize()) > 0)
        {
            IntPtr ptr = _capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
            int bytes = frames * Format.BlockAlign;
            if (_bytes.Length < bytes) _bytes = new byte[bytes * 2];
            if (_mono.Length < frames) _mono = new float[frames * 2];
            bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
            if (!silent && bytes > 0) Marshal.Copy(ptr, _bytes, 0, bytes);
            _capture.ReleaseBuffer(frames);
            if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Discontinuities++;

            var mono = _mono.AsSpan(0, frames);
            if (silent) mono.Clear();
            else PcmConvert.ToMono(_bytes.AsSpan(0, bytes), Format.Encoding, Format.Channels, mono);
            sink(mono);
            total += frames;
        }
        return total;
    }

    public void Dispose()
    {
        try { _client.Stop(); } catch (Exception) { }
        _capture?.Dispose();
        _client?.Dispose();
        _device.Dispose();
        Event?.Dispose();
    }
}

internal delegate void MonoSink(Span<float> mono);

/// <summary>
/// Shared-mode, event-driven WASAPI render fed from a FIFO of mono samples at the device rate.
/// Keeps only a small amount of audio queued in the device buffer to minimise latency.
/// </summary>
internal sealed class RenderStream : IDisposable
{
    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioRenderClient _render;
    private readonly int _bufferFrames;
    private float[] _mono = new float[2048];
    private float[] _resampled = new float[4096];
    private byte[] _bytes = new byte[16384];
    private bool _gotData;
    private double _avgPadding = -1;
    private readonly Queue<long> _underrunTimes = new();

    public RenderStream(MMDeviceEnumerator enumerator, string deviceId, int captureRate, double marginMs)
    {
        _device = enumerator.GetDevice(deviceId);
        try
        {
            if (_device.State != DeviceState.Active)
                throw new COMException("Device not active", AudioErrors.E_NOTFOUND);
            Name = _device.FriendlyName;
            _client = _device.AudioClient;
            var mix = _client.MixFormat;
            Format = DeviceFormat.From(mix);
            long period = _client.DefaultDevicePeriod;
            _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                Math.Max(period * 4, 500_000), 0, mix, Guid.Empty);
            Event = new AutoResetEvent(false);
            _client.SetEventHandle(Event.SafeWaitHandle.DangerousGetHandle());
            _render = _client.AudioRenderClient;
            _bufferFrames = _client.BufferSize;
            PeriodFrames = (int)(period * Format.SampleRate / 10_000_000);
            StreamLatencyMs = _client.StreamLatency / 10_000.0;
            MarginMs = marginMs;

            Resampler = new Resampler(captureRate, Format.SampleRate);
            Fifo = new SampleFifo(Format.SampleRate); // 1 s max
            Drift = new DriftController(MarginFrames, Format.SampleRate / 2);

            // Prime with silence so the device starts cleanly.
            WriteFrames(Math.Min(TargetPadding, _bufferFrames), silent: true);
        }
        catch
        {
            _client?.Dispose();
            _device.Dispose();
            throw;
        }
    }

    public string Name { get; } = "";

    public DeviceFormat Format { get; }

    public AutoResetEvent Event { get; } = null!;

    public Resampler Resampler { get; } = null!;

    public SampleFifo Fifo { get; } = null!;

    public DriftController Drift { get; } = null!;

    public int PeriodFrames { get; }

    public double StreamLatencyMs { get; }

    public double MarginMs { get; private set; }

    public long Underruns { get; private set; }

    public long Overruns { get; private set; }

    public long Splices => Drift.Splices;

    private int MarginFrames => (int)(MarginMs * Format.SampleRate / 1000);

    private int TargetPadding => Math.Min(_bufferFrames, PeriodFrames + MarginFrames);

    /// <summary>Average number of frames queued in the device buffer.</summary>
    public double AveragePaddingFrames => Math.Max(0, _avgPadding);

    public void Start() => _client.Start();

    /// <summary>Resamples one block of processed capture audio into this output's FIFO.</summary>
    public void Push(ReadOnlySpan<float> mono)
    {
        int max = Resampler.MaxOutputLength(mono.Length);
        if (_resampled.Length < max) _resampled = new float[max * 2];
        int n = Resampler.Process(mono, _resampled);
        Overruns += Fifo.Write(_resampled.AsSpan(0, n)) > 0 ? 1 : 0;
        _gotData = true;
        Resampler.SetCorrection(Drift.Correction);
    }

    /// <summary>Called when the device signals it needs data.</summary>
    public void Service()
    {
        int padding = _client.CurrentPadding;
        _avgPadding = _avgPadding < 0 ? padding : _avgPadding + 0.05 * (padding - _avgPadding);
        int want = Math.Min(TargetPadding - padding, _bufferFrames - padding);
        if (want <= 0) return;
        if (_gotData)
        {
            Drift.TargetSlack = MarginFrames;
            Drift.ObserveRead(Fifo.Count, want);
            int skip = Drift.TakePendingSkip();
            if (skip > 0) Fifo.SpliceSkip(skip, Format.SampleRate / 200); // 5 ms cross-fade
        }
        WriteFrames(want, silent: false);
    }

    private void WriteFrames(int frames, bool silent)
    {
        if (frames <= 0) return;
        if (_mono.Length < frames) _mono = new float[frames * 2];
        var mono = _mono.AsSpan(0, frames);
        int got = silent ? 0 : Fifo.Read(mono);
        if (got < frames)
        {
            mono[got..].Clear();
            if (_gotData && !silent) RegisterUnderrun();
        }

        IntPtr ptr = _render.GetBuffer(frames);
        if (got == 0)
        {
            _render.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
            return;
        }
        int bytes = frames * Format.BlockAlign;
        if (_bytes.Length < bytes) _bytes = new byte[bytes * 2];
        PcmConvert.FromMono(mono, Format.Encoding, Format.Channels, _bytes.AsSpan(0, bytes));
        Marshal.Copy(_bytes, 0, ptr, bytes);
        _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    private void RegisterUnderrun()
    {
        Underruns++;
        long now = Environment.TickCount64;
        _underrunTimes.Enqueue(now);
        while (_underrunTimes.Count > 0 && now - _underrunTimes.Peek() > 3000) _underrunTimes.Dequeue();
        // Repeated dropouts: grow the safety margin a little (self-tuning, capped at 30 ms).
        if (_underrunTimes.Count >= 3 && MarginMs < 30)
        {
            MarginMs = Math.Min(30, MarginMs + 2);
            _underrunTimes.Clear();
        }
    }

    public void Dispose()
    {
        try { _client.Stop(); } catch (Exception) { }
        _render?.Dispose();
        _client?.Dispose();
        _device.Dispose();
        Event?.Dispose();
    }
}
