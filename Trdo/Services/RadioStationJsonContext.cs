using System.Collections.Generic;
using System.Text.Json.Serialization;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// JSON source generation context for RadioStation local storage.
/// This ensures JSON serialization works correctly even when the app is trimmed in Release mode.
/// </summary>
[JsonSerializable(typeof(List<RadioStation>))]
[JsonSerializable(typeof(RadioStation))]
[JsonSerializable(typeof(WhiteNoiseColor))]
[JsonSerializable(typeof(AudioSourceKind))]
[JsonSerializable(typeof(StationLayoutDocument))]
[JsonSerializable(typeof(StationLayoutRow))]
[JsonSerializable(typeof(List<StationLayoutRow>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class RadioStationJsonContext : JsonSerializerContext
{
}
