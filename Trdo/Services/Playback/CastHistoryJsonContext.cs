using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trdo.Services.Playback;

[JsonSerializable(typeof(CastHistoryDocument))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class CastHistoryJsonContext : JsonSerializerContext
{
}

public static class CastHistoryStorageFormat
{
    public static CastHistoryDocument Parse(string json)
    {
        CastHistoryDocument document =
            JsonSerializer.Deserialize(json, CastHistoryJsonContext.Default.CastHistoryDocument)
            ?? throw new FormatException("Casting history is empty.");
        CastHistoryPolicy.Validate(document);
        return document;
    }

    public static string Serialize(CastHistoryDocument document)
    {
        CastHistoryPolicy.Validate(document);
        return JsonSerializer.Serialize(document, CastHistoryJsonContext.Default.CastHistoryDocument);
    }
}
