using System;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Decides when a track being listened to becomes worth scrobbling, per Last.fm's Scrobbling
/// 2.0 rule: a track qualifies once playback reaches half its duration or four minutes,
/// whichever comes first, and only if it is longer than 30 seconds to begin with.
/// </summary>
/// <remarks>
/// Radio streams rarely expose a track's real duration, so there is a second, proxy rule for
/// that case: treat the track as provisionally eligible once 4 minutes of continuous play have
/// elapsed (guaranteed eligible under the real rule for any duration), and if the track changes
/// before that - the more common case - fall back to a lower floor (30 seconds of continuous
/// play) as the best available stand-in for "this was a real listen, not a station a listener
/// briefly stopped on while scanning." Kept free of timers/schedulers so it can be unit tested
/// directly (see Trdo.Tests); the stateful side of this - accumulating actual played time - is
/// <see cref="ScrobbleTimingTracker"/>.
/// </remarks>
internal static class LastfmScrobbleEligibilityPolicy
{
    /// <summary>Last.fm's floor: a track shorter than this can never be scrobbled.</summary>
    public const int MinScrobbleTrackSeconds = 30;

    /// <summary>Last.fm's ceiling: half of any longer track is capped at 4 minutes of play.</summary>
    public const int MaxScrobbleWindowSeconds = 240;

    /// <summary>
    /// How long a candidate track must play, continuously, before it is eligible to scrobble -
    /// or <see langword="null"/> if a known duration means it can never qualify (30 seconds or
    /// shorter).
    /// </summary>
    /// <param name="knownDuration">
    /// The track's real duration (local files), or <see langword="null"/> when it is not known
    /// (radio streams), in which case the proactive proxy ceiling is used instead.
    /// </param>
    public static TimeSpan? ComputeEligibilityDelay(TimeSpan? knownDuration)
    {
        if (knownDuration is not { } duration)
            return TimeSpan.FromSeconds(MaxScrobbleWindowSeconds);

        if (duration.TotalSeconds <= MinScrobbleTrackSeconds)
            return null;

        double seconds = Math.Min(duration.TotalSeconds / 2, MaxScrobbleWindowSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Whether a track that changed (or ended) before its eligibility timer fired should still
    /// be scrobbled, judged from how long it was actually heard.
    /// </summary>
    /// <param name="elapsedContinuousPlayTime">Time actually spent playing this candidate, excluding pauses/buffering.</param>
    /// <param name="knownDuration">The track's real duration, or <see langword="null"/> for radio.</param>
    public static bool IsRetroactivelyEligible(TimeSpan elapsedContinuousPlayTime, TimeSpan? knownDuration)
    {
        TimeSpan? delay = ComputeEligibilityDelay(knownDuration);
        if (delay is null)
            return false;

        if (knownDuration.HasValue)
            return elapsedContinuousPlayTime >= delay.Value;

        // Unknown duration: the proactive ceiling did not fire yet (that is why this is being
        // asked at all), so fall back to the lower floor as the proxy for "a real listen."
        return elapsedContinuousPlayTime.TotalSeconds >= MinScrobbleTrackSeconds;
    }
}
