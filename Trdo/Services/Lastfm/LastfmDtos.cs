using System.Text.Json.Serialization;

namespace Trdo.Services.Lastfm;

// ---------------------------------------------------------------------------------------------
// Response DTOs. Shapes follow Last.fm's format=json responses; irregular JSON names (#text,
// @attr) need an explicit JsonPropertyName since they are not valid C# identifiers.
// ---------------------------------------------------------------------------------------------

/// <summary>Common shape of a failed call: <c>{"error": N, "message": "..."}</c>.</summary>
internal sealed class LastfmErrorEnvelope
{
    public int Error { get; set; }
    public string? Message { get; set; }
}

/// <summary>Response of <c>auth.getToken</c>.</summary>
internal sealed class LastfmTokenResponse
{
    public string? Token { get; set; }
}

/// <summary>Response of <c>auth.getSession</c>.</summary>
internal sealed class LastfmSessionEnvelope
{
    public LastfmSessionResponse? Session { get; set; }
}

internal sealed class LastfmSessionResponse
{
    public string? Name { get; set; }
    public string? Key { get; set; }
    public int Subscriber { get; set; }
}

/// <summary>Response of <c>track.updateNowPlaying</c>. Only the ignored-message is read - the
/// call is best-effort and its echoed track/artist/album fields are not acted on.</summary>
internal sealed class LastfmNowPlayingEnvelope
{
    [JsonPropertyName("nowplaying")]
    public LastfmNowPlayingResult? NowPlaying { get; set; }
}

internal sealed class LastfmNowPlayingResult
{
    [JsonPropertyName("ignoredMessage")]
    public LastfmIgnoredMessage? IgnoredMessage { get; set; }
}

internal sealed class LastfmIgnoredMessage
{
    public string? Code { get; set; }

    [JsonPropertyName("#text")]
    public string? Text { get; set; }
}

/// <summary>
/// Response of <c>track.scrobble</c>. Only the accepted/ignored counts are read - Last.fm's own
/// per-scrobble ignore reasons are about metadata quality a resubmission would not fix, so a
/// batch that was accepted by the transport is treated as done rather than parsed item by item.
/// </summary>
internal sealed class LastfmScrobbleEnvelope
{
    public LastfmScrobblesResult? Scrobbles { get; set; }
}

internal sealed class LastfmScrobblesResult
{
    [JsonPropertyName("@attr")]
    public LastfmScrobbleAttr? Attr { get; set; }
}

internal sealed class LastfmScrobbleAttr
{
    public int Accepted { get; set; }
    public int Ignored { get; set; }
}

// ---------------------------------------------------------------------------------------------
// Request DTOs. Plain input shapes for LastfmApiClient - not serialized directly, but keeping
// them as typed records avoids the call sites passing long, error-prone parameter lists.
// ---------------------------------------------------------------------------------------------

/// <summary>Everything needed for one <c>track.updateNowPlaying</c> call.</summary>
internal sealed class LastfmNowPlayingRequest
{
    public required string Artist { get; init; }
    public required string Track { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Mbid { get; init; }
    public int? DurationSeconds { get; init; }
    public int? TrackNumber { get; init; }
}

/// <summary>Everything needed for one scrobble entry in a <c>track.scrobble</c> batch.</summary>
internal sealed class LastfmScrobbleRequest
{
    public required string Artist { get; init; }
    public required string Track { get; init; }
    public required long TimestampUnix { get; init; }
    public required bool ChosenByUser { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Mbid { get; init; }
    public int? DurationSeconds { get; init; }
    public int? TrackNumber { get; init; }
}
