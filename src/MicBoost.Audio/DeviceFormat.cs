using MicBoost.Core.Dsp;
using NAudio.Wave;

namespace MicBoost.Audio;

/// <summary>The parts of a WASAPI mix format the engine cares about.</summary>
public readonly record struct DeviceFormat(SampleEncoding Encoding, int Channels, int SampleRate, int BlockAlign)
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public override string ToString()
    {
        string enc = Encoding switch
        {
            SampleEncoding.Float32 => "32-bit float",
            SampleEncoding.Int16 => "16-bit",
            SampleEncoding.Int24 => "24-bit",
            _ => "32-bit",
        };
        return $"{SampleRate / 1000.0:0.#} kHz, {enc}, {Channels} ch";
    }

    public static DeviceFormat From(WaveFormat wf)
    {
        int channels = wf.Channels;
        int containerBits = channels > 0 ? wf.BlockAlign * 8 / channels : 0;
        bool isFloat;
        if (wf is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == SubtypeFloat) isFloat = true;
            else if (ext.SubFormat == SubtypePcm) isFloat = false;
            else throw new NotSupportedException($"Unsupported device format subtype {ext.SubFormat}.");
        }
        else if (wf.Encoding == WaveFormatEncoding.IeeeFloat) isFloat = true;
        else if (wf.Encoding == WaveFormatEncoding.Pcm) isFloat = false;
        else throw new NotSupportedException($"Unsupported device format {wf.Encoding}.");

        SampleEncoding enc = (isFloat, containerBits) switch
        {
            (true, 32) => SampleEncoding.Float32,
            (false, 16) => SampleEncoding.Int16,
            (false, 24) => SampleEncoding.Int24,
            (false, 32) => SampleEncoding.Int32,
            _ => throw new NotSupportedException($"Unsupported sample size: {containerBits} bits{(isFloat ? " float" : "")}."),
        };
        return new DeviceFormat(enc, channels, wf.SampleRate, wf.BlockAlign);
    }
}
