namespace Trdo.Models;

/// <summary>
/// The noise spectrum generated for a white noise "station". See
/// <see cref="RadioStation.SourceKind"/> is <see cref="AudioSourceKind.WhiteNoise"/>.
/// </summary>
/// <remarks>
/// Ordering matters: values are persisted as their numeric ordinal (see
/// <see cref="Services.RadioStationJsonContext"/>), so existing members must never be
/// reordered or renumbered. New colors are appended at the end.
/// </remarks>
public enum WhiteNoiseColor
{
    /// <summary>Flat power spectral density across the whole audible band - a steady hiss.</summary>
    White,

    /// <summary>Power falls off at -3 dB/octave, reading as softer and less hissy than white.</summary>
    Pink,

    /// <summary>
    /// Also called red noise. Power falls off at -6 dB/octave - twice the roll-off of pink -
    /// producing a deep, low rumble with almost no high-frequency hiss.
    /// </summary>
    Brown,

    /// <summary>
    /// Power rises at +3 dB/octave - the inverse of pink - producing a bright, hissy tone
    /// weighted toward the high end. Used in audio dithering.
    /// </summary>
    Blue,

    /// <summary>
    /// Also called purple noise. Power rises at +6 dB/octave - the inverse of brown -
    /// producing a very bright, sharp hiss dominated by high frequencies.
    /// </summary>
    Violet,

    /// <summary>
    /// White noise reshaped to sound equally loud across the audible band by cutting the
    /// frequencies human hearing is most sensitive to (roughly 2-5 kHz) and boosting the
    /// bass and treble extremes it is least sensitive to.
    /// </summary>
    Grey
}
