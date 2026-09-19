using System.Collections.Generic;
using System.Text.Json.Serialization;
using Trdo.Models;

namespace Trdo.Services.Lastfm;

/// <summary>
/// JSON source generation context for the persisted scrobble queue. Same reasoning as
/// <see cref="LastfmJsonContext"/>: required for trim-safe serialization in Release builds.
/// </summary>
[JsonSerializable(typeof(List<LastfmPendingScrobble>))]
[JsonSerializable(typeof(LastfmPendingScrobble))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class LastfmScrobbleQueueJsonContext : JsonSerializerContext
{
}
