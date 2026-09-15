using System;

namespace Trdo.Services.Audio;

/// <summary>
/// Tunable constants and envelope math for the FM radio static played while a stream buffers.
/// </summary>
/// <remarks>
/// Deliberately free of NAudio and WinRT types so it can be linked into Trdo.Tests the same way
/// the playback policies are. Anything that needs a sample provider or an audio device belongs in
/// <see cref="RadioStaticService"/> or the providers, not here.
/// </remarks>
internal static class RadioStaticProfile
{
    /// <summary>
    /// How long the static takes to reach full level. Short enough to feel immediate, but long
    /// enough that the noise swells in rather than snapping on at full volume.
    /// </summary>
    internal const double RampUpMs = 150;

    /// <summary>
    /// Matches <c>RadioPlayerService.FadeInDuration</c> so the static ducks out over exactly the
    /// window in which the stream fades in, giving a cross-fade rather than a gap or an overlap.
    /// </summary>
    internal const double FadeOutMs = 350;

    /// <summary>
    /// How long the static takes to go silent when the user explicitly pauses. Much shorter than
    /// <see cref="FadeOutMs"/>: a pause is "stop now," not a graceful hand-off to an incoming
    /// stream, so a burst already fading out from an unrelated buffering event must not be allowed
    /// to keep ringing for the rest of its normal fade after that. Still nonzero so it does not
    /// click.
    /// </summary>
    internal const double PauseFadeOutMs = 80;

    /// <summary>How long the Settings "Test" button holds the static at full level.</summary>
    internal const double TestBurstMs = 3000;

    /// <summary>
    /// Render buffer size to ask WASAPI for. NAudio defaults to 200ms, which would put the static
    /// on the speakers a fifth of a second after buffering began - long after the outgoing station
    /// had finished fading, so the two never overlapped. Measured cost of starting a stream at this
    /// size is under 10ms, which is inside the cross-fade rather than after it. Lower is possible
    /// but buys nothing audible and risks underruns glitching the noise.
    /// </summary>
    internal const int OutputLatencyMs = 50;

    /// <summary>
    /// How long to keep the render device open after the static goes quiet. Rebuilding it costs
    /// device activation every time; holding it means a follow-up buffer - a mid-stream rebuffer,
    /// or the user hopping stations - starts fading in within a single buffer instead.
    /// </summary>
    internal const double PlayerLingerMs = 10_000;

    /// <summary>
    /// Static level as a fraction of the user's stream volume. Static is background texture, not
    /// programme material, so it sits well below the stream it stands in for.
    /// </summary>
    internal const float StaticLevel = 0.18f;

    // Band limits for the noise. Rolling off below 300 Hz keeps it from sounding like wind, and
    // above 8 kHz from sounding like digital hiss; what is left is the "shhh" of a detuned radio.
    internal const float NoiseHighPassHz = 300f;
    internal const float NoiseLowPassHz = 8000f;
    internal const float NoiseFilterQ = 0.7f;

    // The heterodyne whine that wanders behind the noise. It sits above the band where the pink
    // noise keeps most of its energy: a tone buried in the middle of the noise is masked almost
    // perfectly, so pitching it high is as much of what makes it audible as the gain is. The wander
    // is proportional to the band - wide enough to sound like it is searching, not vibrato.
    internal const float CarrierMinHz = 4200f;
    internal const float CarrierMaxHz = 5600f;
    internal const float CarrierGain = 0.14f;
    internal const float CarrierDriftHz = 80f;

    // A slow sweep climbing across the band, the drifting heterodyne you get when a neighbouring
    // carrier wanders past the one you are tuned to. Long and quiet: it should register as the
    // static having character rather than as a tone anyone could hum.
    internal const float SweepStartHz = 600f;
    internal const float SweepEndHz = 5000f;
    internal const float SweepGain = 0.09f;
    internal const double SweepMinLengthSeconds = 8.0;
    internal const double SweepMaxLengthSeconds = 16.0;

    // The slow amplitude swell that keeps the noise breathing instead of sitting flat.
    internal const float SwellMin = 0.55f;
    internal const float SwellMax = 1.0f;
    internal const float SwellStep = 0.06f;

    /// <summary>
    /// Gain the static should play at for a given user stream volume.
    /// </summary>
    /// <param name="userVolume">
    /// The player's volume, on the same 0-2 scale <c>RadioPlayerService.Volume</c> uses (LibVLC
    /// allows amplification above 1).
    /// </param>
    internal static float EffectiveGain(double userVolume) =>
        (float)(Math.Clamp(userVolume, 0d, 2d) * StaticLevel);

    /// <summary>
    /// Advances the swell envelope by one step of a bounded random walk.
    /// </summary>
    /// <param name="current">Current envelope value.</param>
    /// <param name="random">A value in [0,1), typically from <see cref="Random.NextDouble"/>.</param>
    internal static float NextSwell(float current, double random) =>
        Math.Clamp(current + (float)((random - 0.5) * 2.0 * SwellStep), SwellMin, SwellMax);

    /// <summary>
    /// Picks how long one full up-and-down pass of the sweep should take.
    /// </summary>
    /// <remarks>
    /// Randomised per burst so repeated buffering does not replay an identical gesture, and long
    /// enough that a typical buffer only ever hears part of a pass - which is what keeps it
    /// sounding like drift rather than like a repeating riser.
    /// </remarks>
    /// <param name="random">A value in [0,1), typically from <see cref="Random.NextDouble"/>.</param>
    internal static double NextSweepLengthSeconds(double random) =>
        SweepMinLengthSeconds + (Math.Clamp(random, 0d, 1d) * (SweepMaxLengthSeconds - SweepMinLengthSeconds));

    /// <summary>
    /// Picks the pitch the whine starts on, anywhere across its band.
    /// </summary>
    /// <remarks>
    /// Chosen fresh for every burst. Starting from a fixed pitch made each buffer open on the same
    /// note, which is the sort of repetition that turns a texture into a jingle - a real radio is
    /// never sitting at the same point in its drift when you tune in.
    /// </remarks>
    /// <param name="random">A value in [0,1), typically from <see cref="Random.NextDouble"/>.</param>
    internal static float StartingCarrierFrequency(double random) =>
        CarrierMinHz + (float)(Math.Clamp(random, 0d, 1d) * (CarrierMaxHz - CarrierMinHz));

    /// <summary>
    /// Advances the carrier whine's frequency by one step of a bounded random walk.
    /// </summary>
    /// <param name="current">Current frequency in Hz.</param>
    /// <param name="random">A value in [0,1), typically from <see cref="Random.NextDouble"/>.</param>
    internal static float NextCarrierFrequency(float current, double random) =>
        Math.Clamp(current + (float)((random - 0.5) * 2.0 * CarrierDriftHz), CarrierMinHz, CarrierMaxHz);
}
