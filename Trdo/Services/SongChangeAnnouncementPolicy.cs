using System;

namespace Trdo.Services;

/// <summary>
/// Pure decision logic for whether a stream metadata change should trigger the
/// song-change popup. Kept free of WinRT/WinUI dependencies so it can be unit
/// tested directly (see Trdo.Tests).
/// </summary>
public static class SongChangeAnnouncementPolicy
{
    /// <summary>
    /// Decides whether the current metadata change is "meaningful" enough to
    /// announce, given the caller-tracked baseline from the previous call.
    /// </summary>
    /// <param name="previousDisplayText">
    /// The meaningful display text observed on the previous call, or
    /// <see langword="null"/> if this is the first observation since launch
    /// (or since the tracker was reset). A null or blank baseline never
    /// announces: it only establishes the starting point so that already-current
    /// metadata does not immediately pop up when the feature is enabled or the
    /// app starts.
    /// </param>
    /// <param name="currentDisplayText">The current <c>StreamMetadata.DisplayText</c>.</param>
    /// <param name="isEnabled">Whether the user has opted into the popup.</param>
    /// <returns><see langword="true"/> if the popup should be shown for this change.</returns>
    public static bool ShouldAnnounce(string? previousDisplayText, string currentDisplayText, bool isEnabled)
    {
        return ShouldAnnounce(previousDisplayText, currentDisplayText, isEnabled, isFirstObservationSinceStationStart: false);
    }

    /// <summary>
    /// Decides whether the current metadata change is meaningful enough to announce.
    /// When a station has just started, its first metadata observation is the current track the
    /// listener can already hear, so it must bypass the usual baseline guard and surface
    /// immediately even when there is no prior display text to compare against.
    /// </summary>
    /// <remarks>
    /// The station-start bypass lifts the baseline guard <em>only</em>. The dedupe against
    /// the previous text still applies, because a start is rarely a single clean event: the
    /// same track can be reported twice as sources converge on it (an ICY title first, then
    /// the same title carrying album art), and a stuttering connection re-opens the start
    /// window under a track that has already been announced. Announcing unconditionally here
    /// would show the same song twice in a row in both cases.
    /// </remarks>
    public static bool ShouldAnnounce(
        string? previousDisplayText,
        string currentDisplayText,
        bool isEnabled,
        bool isFirstObservationSinceStationStart)
    {
        if (!isEnabled)
            return false;

        if (string.IsNullOrWhiteSpace(currentDisplayText))
            return false;

        if (string.IsNullOrWhiteSpace(previousDisplayText))
        {
            // With no baseline, the decision is entirely about why we got here. Just after a
            // station starts this is the track already playing and the listener wants to see
            // it; otherwise (launch, or the setting being switched on mid-song) establishing
            // the baseline is all this observation does.
            return isFirstObservationSinceStationStart;
        }

        return !string.Equals(
            currentDisplayText.Trim(),
            previousDisplayText.Trim(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// How long after a station starts an announcement still counts as "the track that was
    /// already playing". Metadata for a stream that has just opened arrives within a few
    /// seconds, so half a minute is generous; the bound matters because the window has to
    /// close on its own. Resuming mid-track produces no metadata change at all — the
    /// orchestrator dedupes it — so a one-shot flag would survive until the next real track
    /// change and rob it of the delay it needs.
    /// </summary>
    public static readonly TimeSpan StationStartGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether an announcement made at <paramref name="nowUtc"/> is still close enough to the
    /// station starting at <paramref name="startedAtUtc"/> to describe a track the listener can
    /// already hear. A null start means no station start is pending.
    /// </summary>
    public static bool IsWithinStationStartGrace(DateTimeOffset? startedAtUtc, DateTimeOffset nowUtc)
    {
        if (startedAtUtc is not { } startedAt)
            return false;

        TimeSpan elapsed = nowUtc - startedAt;
        return elapsed >= TimeSpan.Zero && elapsed <= StationStartGrace;
    }

    /// <summary>Minimum supported delay: announce as soon as the metadata arrives.</summary>
    public const double MinDelaySeconds = 0;

    /// <summary>
    /// Upper bound on the announcement delay. A minute covers even the worst offenders,
    /// and stopping there keeps an unbounded value from letting a popup outlive the song
    /// it describes.
    /// </summary>
    public const double MaxDelaySeconds = 60;

    /// <summary>Constrains a delay to the supported range, mapping NaN to no delay.</summary>
    public static double ClampDelay(double seconds)
    {
        if (double.IsNaN(seconds))
            return MinDelaySeconds;

        return Math.Clamp(seconds, MinDelaySeconds, MaxDelaySeconds);
    }

    /// <summary>
    /// Shortest time the popup stays on screen. Below about a second the pill is gone before
    /// a long title can be read, so the animations would be all the user saw.
    /// </summary>
    public const double MinDwellSeconds = 1;

    /// <summary>
    /// Longest time the popup stays on screen. A quarter of a minute is already long enough
    /// to read anything a station sends; past that the pill starts to feel stuck rather than
    /// generous, and it sits over whatever is behind it the whole time.
    /// </summary>
    public const double MaxDwellSeconds = 15;

    /// <summary>
    /// How long the popup stays up out of the box: long enough to read a track and artist,
    /// short enough to stay out of the way.
    /// </summary>
    public const double DefaultDwellSeconds = 2.5;

    /// <summary>Constrains a dwell time to the supported range, mapping NaN to the default.</summary>
    public static double ClampDwell(double seconds)
    {
        if (double.IsNaN(seconds))
            return DefaultDwellSeconds;

        return Math.Clamp(seconds, MinDwellSeconds, MaxDwellSeconds);
    }

    /// <summary>
    /// Formats a dwell time for display. Unlike a delay, this is never zero, so it always
    /// reads as a duration.
    /// </summary>
    public static string DescribeDwell(double seconds)
    {
        double clamped = ClampDwell(seconds);

        return DescribeSeconds(clamped);
    }

    /// <summary>
    /// Works out how long to wait before announcing, given the station's own override and
    /// the app-wide setting.
    /// <para>
    /// Many stations push metadata a few seconds before the audio actually reaches the
    /// listener, and the size of that lead is a property of the station's encoder — so the
    /// per-station value wins outright rather than adding to the global one. A station with
    /// no override follows the app setting.
    /// </para>
    /// </summary>
    /// <param name="stationDelaySeconds">The station's override, or null to follow the app setting.</param>
    /// <param name="globalDelaySeconds">The app-wide delay.</param>
    public static double ResolveDelaySeconds(double? stationDelaySeconds, double globalDelaySeconds)
    {
        return ClampDelay(stationDelaySeconds ?? globalDelaySeconds);
    }

    /// <summary>
    /// Formats a delay for display, so the settings page, the station editor and the
    /// popup's own menu all describe the same value identically.
    /// </summary>
    public static string DescribeDelay(double seconds)
    {
        double clamped = ClampDelay(seconds);

        if (clamped <= 0)
            return LocalizedText.Get("Ui_SongChangeAnnouncementPolicy_1eef43b1b6", "No delay");

        // The top of the range reads better as a minute than as 60 seconds.
        if (clamped >= MaxDelaySeconds)
            return LocalizedText.Get("Ui_SongChangeAnnouncementPolicy_e67b6f61f5", "1 minute");

        // Whole seconds read better than "5.0s" for the common preset values.
        return DescribeSeconds(clamped);
    }

    private static string DescribeSeconds(double seconds)
    {
        if (Math.Abs(seconds - Math.Round(seconds)) < 0.05)
            return Math.Round(seconds) == 1 ? LocalizedText.Get("Ui_SongChangeAnnouncementPolicy_db3b946b8d", "1 second") : LocalizedText.Format("Ui_SongChangeAnnouncementPolicy_8c48eeb893", "{0:0} seconds", Math.Round(seconds));
        return LocalizedText.Format("Ui_SongChangeAnnouncementPolicy_c432fca10b", "{0:0.0} seconds", seconds);
    }
}
