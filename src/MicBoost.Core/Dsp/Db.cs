namespace MicBoost.Core.Dsp;

/// <summary>Decibel helpers. Levels are relative to digital full scale (dBFS).</summary>
public static class Db
{
    /// <summary>Floor used for silence so we never return -Infinity.</summary>
    public const float Floor = -120f;

    private const float FloorLinear = 1e-6f; // = -120 dB

    public static float ToLinear(float db) => db <= Floor ? 0f : MathF.Pow(10f, db / 20f);

    public static double ToLinear(double db) => db <= Floor ? 0d : Math.Pow(10d, db / 20d);

    public static float FromLinear(float linear)
    {
        linear = MathF.Abs(linear);
        return linear <= FloorLinear ? Floor : MathF.Max(Floor, 20f * MathF.Log10(linear));
    }

    public static double FromLinear(double linear)
    {
        linear = Math.Abs(linear);
        return linear <= FloorLinear ? Floor : Math.Max(Floor, 20d * Math.Log10(linear));
    }

    /// <summary>One-pole smoothing coefficient for a time constant in milliseconds.</summary>
    public static float TimeCoefficient(float milliseconds, int sampleRate)
    {
        if (milliseconds <= 0f) return 0f;
        return MathF.Exp(-1f / (milliseconds * 0.001f * sampleRate));
    }
}
