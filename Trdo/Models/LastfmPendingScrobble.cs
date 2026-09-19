using System;

namespace Trdo.Models;

/// <summary>
/// A scrobble that has been decided as eligible but not yet confirmed sent to Last.fm.
/// Persisted by <see cref="Services.Lastfm.LastfmScrobbleQueueService"/> so a listen already
/// counted as "worth scrobbling" survives an app restart or a network failure rather than being
/// silently lost.
/// </summary>
public sealed class LastfmPendingScrobble
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string Artist { get; set; }
    public required string Track { get; set; }
    public required long TimestampUnix { get; set; }
    public required bool ChosenByUser { get; set; }

    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Mbid { get; set; }
    public int? DurationSeconds { get; set; }
    public int? TrackNumber { get; set; }

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastErrorMessage { get; set; }
}
