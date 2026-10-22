using Trdo.Services;
using System;
using System.Text.Json.Serialization;

namespace Trdo.Models;

/// <summary>
/// Represents a track that has been favorited by the user.
/// </summary>
public class FavoriteTrack
{
    /// <summary>
    /// Unique identifier for this favorite track.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The name of the radio station this track was playing on.
    /// </summary>
    public string StationName { get; set; } = string.Empty;

    /// <summary>
    /// The artist name, if available.
    /// </summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>
    /// The song/track title, if available.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The full stream title string from the metadata.
    /// </summary>
    public string StreamTitle { get; set; } = string.Empty;

    /// <summary>
    /// When this track was favorited.
    /// </summary>
    public DateTime FavoritedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// When this track was last included in an export that archived it.
    /// Null means the track has never been exported.
    /// </summary>
    public DateTime? ExportedAt { get; set; }

    /// <summary>
    /// Whether this track has already been exported and archived.
    /// </summary>
    [JsonIgnore]
    public bool IsArchived => ExportedAt.HasValue;

    /// <summary>
    /// A human-friendly description of when this track was exported.
    /// </summary>
    [JsonIgnore]
    public string ExportedOnDisplay => ExportedAt is DateTime exportedAt
        ? LocalizedText.Format("Ui_FavoriteTrack_73edcbce18", "Exported on {0:MMM d, yyyy h:mm tt}", exportedAt)
        : string.Empty;

    /// <summary>
    /// Gets a display-friendly string for the track.
    /// </summary>
    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Title))
                return $"{Artist} - {Title}";

            if (!string.IsNullOrWhiteSpace(StreamTitle))
                return StreamTitle;

            return string.Empty;
        }
    }

    /// <summary>
    /// Creates a unique key for comparison purposes (to avoid duplicate favorites).
    /// </summary>
    public string UniqueKey => $"{Artist?.ToLowerInvariant()}|{Title?.ToLowerInvariant()}|{StreamTitle?.ToLowerInvariant()}".Trim();

    /// <summary>
    /// Creates a FavoriteTrack from stream metadata.
    /// </summary>
    public static FavoriteTrack FromMetadata(StreamMetadata metadata, string stationName)
    {
        return new FavoriteTrack
        {
            StationName = stationName,
            Artist = metadata.Artist,
            Title = metadata.Title,
            StreamTitle = metadata.StreamTitle,
            FavoritedAt = DateTime.Now
        };
    }
}
