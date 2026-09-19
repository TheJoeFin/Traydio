using System;
using System.Text.Json.Serialization;

namespace Trdo.Models;

/// <summary>
/// Represents a radio station from the Radio Browser API
/// </summary>
public class RadioBrowserStation
{
    [JsonPropertyName("changeuuid")]
    public string ChangeUuid { get; set; } = string.Empty;

    [JsonPropertyName("stationuuid")]
    public string StationUuid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("url_resolved")]
    public string UrlResolved { get; set; } = string.Empty;

    [JsonPropertyName("homepage")]
    public string Homepage { get; set; } = string.Empty;

    [JsonPropertyName("favicon")]
    public string Favicon { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public string Tags { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("countrycode")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = string.Empty;

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }

    [JsonPropertyName("votes")]
    public int Votes { get; set; }

    /// <summary>
    /// Gets the best available stream URL (resolved URL if available, otherwise regular URL)
    /// </summary>
    public string GetStreamUrl()
    {
        return !string.IsNullOrWhiteSpace(UrlResolved) ? UrlResolved : Url;
    }

    /// <summary>
    /// Codec and bitrate as shown in search results, e.g. "AAC 128k". The directory
    /// sometimes reports only one of the two, so this shows whichever is known rather than
    /// printing a stray "0k". Empty when neither is known - a placeholder like "Unknown
    /// quality" would just get clipped by the row's layout anyway, so there is no value in
    /// showing it.
    /// </summary>
    public string QualityLabel
    {
        get
        {
            bool hasCodec = !string.IsNullOrWhiteSpace(Codec);
            bool hasBitrate = Bitrate > 0;

            if (hasCodec && hasBitrate) return $"{Codec} {Bitrate}k";
            if (hasCodec) return Codec;
            if (hasBitrate) return $"{Bitrate}k";
            return string.Empty;
        }
    }

    /// <summary>
    /// Converts this RadioBrowserStation to a RadioStation for local storage.
    /// <para>
    /// This is the single projection from a directory result to a saved station - every add
    /// path goes through it. Callers layer the user's own choices (name, volume, buffer
    /// overrides) on top of the result; those always win over what the directory says.
    /// </para>
    /// </summary>
    public RadioStation ToRadioStation()
    {
        return new RadioStation
        {
            Id = Services.StationIdentityPolicy.NewId(),
            Name = Name,
            StreamUrl = GetStreamUrl(),
            Homepage = NullIfBlank(Homepage),
            FaviconUrl = NullIfBlank(Favicon),
            StationUuid = NullIfBlank(StationUuid),
            Tags = NullIfBlank(Tags),
            Country = NullIfBlank(Country),
            CountryCode = NullIfBlank(CountryCode),
            Language = NullIfBlank(Language),
            Codec = NullIfBlank(Codec),
            Bitrate = Bitrate > 0 ? Bitrate : null,
            // Stamped locally: the directory's timestamps describe their record, not when
            // this user added the station.
            DateAdded = DateTimeOffset.UtcNow,
            MetadataRefreshedUtc = DateTimeOffset.UtcNow
        };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
