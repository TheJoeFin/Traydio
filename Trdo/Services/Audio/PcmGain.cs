using System;

namespace Trdo.Services.Audio;

/// <summary>
/// The player volume applied to decoded samples inside Traydio's own process.
/// </summary>
/// <remarks>
/// Windows applies stream and session volume after per-stream effects (for example a headset
/// EQ from G HUB), so a volume set there leaves those effects working on a full-scale signal
/// and a loud station clips inside them at any slider position. Scaling the samples before
/// they reach Windows gives the effects the headroom the slider implies.
/// </remarks>
public static class PcmGain
{
    /// <summary>Linear gain for a 0-2 player volume, matching the native engine's scale.</summary>
    public static float FromVolume(double volume) =>
        double.IsNaN(volume) ? 0f : (float)Math.Clamp(volume, 0, 2);

    public static void Apply(Span<float> samples, float gain)
    {
        if (gain == 1f)
            return;

        for (int i = 0; i < samples.Length; i++)
            samples[i] *= gain;
    }

    /// <summary>Converts signed 16-bit PCM to float and applies the gain in the same pass.</summary>
    public static void ToFloat(ReadOnlySpan<short> source, Span<float> target, float gain)
    {
        for (int i = 0; i < source.Length; i++)
            target[i] = source[i] / 32768f * gain;
    }
}
