using System.Buffers.Binary;

namespace MicBoost.Core.Dsp;

public enum SampleEncoding
{
    Float32,
    Int16,
    Int24,
    Int32,
}

/// <summary>Conversion between interleaved device buffers and mono float samples.</summary>
public static class PcmConvert
{
    public static int BytesPerSample(SampleEncoding e) => e switch
    {
        SampleEncoding.Int16 => 2,
        SampleEncoding.Int24 => 3,
        _ => 4,
    };

    /// <summary>Converts interleaved frames to mono by averaging channels. Non-finite values become 0.</summary>
    public static void ToMono(ReadOnlySpan<byte> source, SampleEncoding encoding, int channels, Span<float> mono)
    {
        int bps = BytesPerSample(encoding);
        int frameBytes = bps * channels;
        int frames = Math.Min(mono.Length, source.Length / frameBytes);
        float scale = 1f / channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            int offset = f * frameBytes;
            for (int c = 0; c < channels; c++)
                sum += Read(source.Slice(offset + c * bps, bps), encoding);
            float v = sum * scale;
            mono[f] = float.IsFinite(v) ? v : 0f;
        }
    }

    /// <summary>Writes mono samples to every channel of an interleaved buffer.</summary>
    public static void FromMono(ReadOnlySpan<float> mono, SampleEncoding encoding, int channels, Span<byte> destination)
    {
        int bps = BytesPerSample(encoding);
        int frameBytes = bps * channels;
        int frames = Math.Min(mono.Length, destination.Length / frameBytes);
        for (int f = 0; f < frames; f++)
        {
            float v = mono[f];
            int offset = f * frameBytes;
            for (int c = 0; c < channels; c++)
                Write(destination.Slice(offset + c * bps, bps), encoding, v);
        }
    }

    private static float Read(ReadOnlySpan<byte> b, SampleEncoding e)
    {
        switch (e)
        {
            case SampleEncoding.Float32:
                return BinaryPrimitives.ReadSingleLittleEndian(b);
            case SampleEncoding.Int16:
                return BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f;
            case SampleEncoding.Int24:
                int v = b[0] | (b[1] << 8) | ((sbyte)b[2] << 16);
                return v / 8388608f;
            default:
                return BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f;
        }
    }

    private static void Write(Span<byte> b, SampleEncoding e, float v)
    {
        if (!float.IsFinite(v)) v = 0f;
        v = Math.Clamp(v, -1f, 1f);
        switch (e)
        {
            case SampleEncoding.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(b, v);
                break;
            case SampleEncoding.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(b, (short)Math.Clamp(MathF.Round(v * 32768f), short.MinValue, short.MaxValue));
                break;
            case SampleEncoding.Int24:
                int i24 = (int)Math.Clamp(MathF.Round(v * 8388608f), -8388608f, 8388607f);
                b[0] = (byte)i24;
                b[1] = (byte)(i24 >> 8);
                b[2] = (byte)(i24 >> 16);
                break;
            default:
                long i32 = (long)Math.Clamp(Math.Round(v * 2147483648.0), int.MinValue, int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(b, (int)i32);
                break;
        }
    }
}
