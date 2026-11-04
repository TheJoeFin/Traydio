using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Trdo.Models;
using Windows.Storage;

namespace Trdo.Services;

/// <summary>
/// Service for managing favorite tracks persistence.
/// </summary>
public class FavoritesService
{
    private const string FavoritesKey = "FavoriteTracks";

    private static readonly string _favoritesFilePath =
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "favorites.json");

    private static readonly Lazy<FavoritesService> _instance = new(() => new FavoritesService());
    public static FavoritesService Instance => _instance.Value;

    private readonly List<FavoriteTrack> _cachedFavorites = [];

    public event EventHandler? FavoritesChanged;

    private FavoritesService()
    {
        // Load favorites on initialization
        _cachedFavorites = LoadFavoritesInternal();
    }

    /// <summary>
    /// Gets all favorite tracks.
    /// </summary>
    public List<FavoriteTrack> GetFavorites()
    {
        return [.. _cachedFavorites];
    }

    /// <summary>
    /// Gets the favorite tracks that have not yet been exported and archived.
    /// </summary>
    public List<FavoriteTrack> GetUnarchivedFavorites()
    {
        return [.. _cachedFavorites.Where(f => !f.IsArchived)];
    }

    /// <summary>
    /// Marks the given favorites as exported/archived. Returns the number of tracks changed.
    /// </summary>
    public int MarkArchived(IEnumerable<string> ids, DateTime? exportedAt = null)
    {
        if (ids is null)
            return 0;

        HashSet<string> idSet = [.. ids];
        if (idSet.Count == 0)
            return 0;

        DateTime stamp = exportedAt ?? DateTime.Now;
        int changed = 0;

        foreach (FavoriteTrack track in _cachedFavorites)
        {
            if (idSet.Contains(track.Id) && !track.IsArchived)
            {
                track.ExportedAt = stamp;
                changed++;
            }
        }

        if (changed == 0)
            return 0;

        SaveFavorites();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>
    /// Clears the archived (exported) flag for a single favorite so it is exported again.
    /// </summary>
    public bool ClearArchived(string id)
    {
        FavoriteTrack? track = _cachedFavorites.FirstOrDefault(f => f.Id == id);
        if (track is null || !track.IsArchived)
            return false;

        track.ExportedAt = null;
        SaveFavorites();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Clears the archived (exported) flag for every favorite. Returns the number of tracks changed.
    /// </summary>
    public int ClearAllArchived()
    {
        int changed = 0;
        foreach (FavoriteTrack track in _cachedFavorites)
        {
            if (track.IsArchived)
            {
                track.ExportedAt = null;
                changed++;
            }
        }

        if (changed == 0)
            return 0;

        SaveFavorites();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>
    /// Adds a track to favorites.
    /// </summary>
    public bool AddFavorite(FavoriteTrack track)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.DisplayText))
            return false;

        // Check if already favorited (by unique key)
        if (_cachedFavorites.Any(f => f.UniqueKey == track.UniqueKey))
            return false;

        _cachedFavorites.Insert(0, track); // Add to beginning (most recent first)
        SaveFavorites();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Adds a track to favorites from metadata.
    /// </summary>
    public bool AddFavorite(StreamMetadata metadata, string stationName)
    {
        if (metadata == null || !metadata.HasMetadata)
            return false;

        FavoriteTrack track = FavoriteTrack.FromMetadata(metadata, stationName);
        return AddFavorite(track);
    }

    /// <summary>
    /// Removes a track from favorites by ID.
    /// </summary>
    public bool RemoveFavorite(string id)
    {
        FavoriteTrack? track = _cachedFavorites.FirstOrDefault(f => f.Id == id);
        if (track == null)
            return false;

        _cachedFavorites.Remove(track);
        SaveFavorites();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Removes a favorite by matching metadata.
    /// </summary>
    public bool RemoveFavoriteByMetadata(StreamMetadata metadata)
    {
        if (metadata == null)
            return false;

        string uniqueKey = $"{metadata.Artist?.ToLowerInvariant()}|{metadata.Title?.ToLowerInvariant()}|{metadata.StreamTitle?.ToLowerInvariant()}".Trim();
        FavoriteTrack? track = _cachedFavorites.FirstOrDefault(f => f.UniqueKey == uniqueKey);

        if (track == null)
            return false;

        _cachedFavorites.Remove(track);
        SaveFavorites();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Toggles favorite status for a track.
    /// </summary>
    public bool ToggleFavorite(StreamMetadata metadata, string stationName)
    {
        if (IsFavorited(metadata))
        {
            RemoveFavoriteByMetadata(metadata);
            return false; // Now unfavorited
        }
        else
        {
            AddFavorite(metadata, stationName);
            return true; // Now favorited
        }
    }

    /// <summary>
    /// Checks if a track is favorited.
    /// </summary>
    public bool IsFavorited(StreamMetadata? metadata)
    {
        if (metadata == null || !metadata.HasMetadata)
            return false;

        string uniqueKey = $"{metadata.Artist?.ToLowerInvariant()}|{metadata.Title?.ToLowerInvariant()}|{metadata.StreamTitle?.ToLowerInvariant()}".Trim();
        return _cachedFavorites.Any(f => f.UniqueKey == uniqueKey);
    }

    /// <summary>
    /// Checks if a track (by unique key) is favorited.
    /// </summary>
    public bool IsFavorited(string uniqueKey)
    {
        return _cachedFavorites.Any(f => f.UniqueKey == uniqueKey);
    }

    private void SaveFavorites()
    {
        try
        {
            string json = JsonSerializer.Serialize(_cachedFavorites, FavoritesJsonContext.Default.ListFavoriteTrack);
            File.WriteAllText(_favoritesFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FavoritesService] Error saving favorites: {ex.Message}");
        }
    }

    private List<FavoriteTrack> LoadFavoritesInternal()
    {
        try
        {
            if (File.Exists(_favoritesFilePath))
            {
                string json = File.ReadAllText(_favoritesFilePath);
                return JsonSerializer.Deserialize(json, FavoritesJsonContext.Default.ListFavoriteTrack) ?? [];
            }

            // One-time migration from LocalSettings
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(FavoritesKey, out object? value) &&
                value is string legacyJson)
            {
                List<FavoriteTrack> migrated =
                    JsonSerializer.Deserialize(legacyJson, FavoritesJsonContext.Default.ListFavoriteTrack) ?? [];
                File.WriteAllText(_favoritesFilePath, JsonSerializer.Serialize(migrated, FavoritesJsonContext.Default.ListFavoriteTrack));
                ApplicationData.Current.LocalSettings.Values.Remove(FavoritesKey);
                System.Diagnostics.Debug.WriteLine($"[FavoritesService] Migrated {migrated.Count} favorites from LocalSettings to file");
                return migrated;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FavoritesService] Error loading favorites: {ex.Message}");
        }

        return [];
    }
}
