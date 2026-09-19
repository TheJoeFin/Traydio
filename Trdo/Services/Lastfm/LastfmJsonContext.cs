using System.Text.Json.Serialization;

namespace Trdo.Services.Lastfm;

/// <summary>
/// JSON source generation context for Last.fm API responses. Required (not just preferred) since
/// the app publishes trimmed (see Trdo.csproj PublishTrimmed) and reflection-based
/// serialization is not trim-safe - see Trdo/Services/RadioBrowserJsonContext.cs for the same
/// pattern applied to the Radio Browser client.
/// </summary>
[JsonSerializable(typeof(LastfmErrorEnvelope))]
[JsonSerializable(typeof(LastfmTokenResponse))]
[JsonSerializable(typeof(LastfmSessionEnvelope))]
[JsonSerializable(typeof(LastfmSessionResponse))]
[JsonSerializable(typeof(LastfmNowPlayingEnvelope))]
[JsonSerializable(typeof(LastfmNowPlayingResult))]
[JsonSerializable(typeof(LastfmIgnoredMessage))]
[JsonSerializable(typeof(LastfmScrobbleEnvelope))]
[JsonSerializable(typeof(LastfmScrobblesResult))]
[JsonSerializable(typeof(LastfmScrobbleAttr))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class LastfmJsonContext : JsonSerializerContext
{
}
