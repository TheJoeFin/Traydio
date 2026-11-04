using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Trdo.Services;

namespace Trdo.Models;

/// <summary>
/// Represents an item in the playlist history, wrapping stream metadata with additional context.
/// </summary>
public partial class PlaylistHistoryItem : INotifyPropertyChanged
{
    private readonly FavoritesService _favoritesService = FavoritesService.Instance;

    private string _artist = string.Empty;
    private string _title = string.Empty;
    private string _streamTitle = string.Empty;
    private string _stationName = string.Empty;
    private DateTime _playedAt = DateTime.Now;
    private bool _isFavorited;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Artist
    {
        get => _artist;
        set
        {
            if (_artist == value) return;
            _artist = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(HasArtist));
            OnPropertyChanged(nameof(UniqueKey));
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(HasTitle));
            OnPropertyChanged(nameof(UniqueKey));
        }
    }

    public string StreamTitle
    {
        get => _streamTitle;
        set
        {
            if (_streamTitle == value) return;
            _streamTitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(UniqueKey));
        }
    }

    public string StationName
    {
        get => _stationName;
        set
        {
            if (_stationName == value) return;
            _stationName = value;
            OnPropertyChanged();
        }
    }

    public DateTime PlayedAt
    {
        get => _playedAt;
        set
        {
            if (_playedAt == value) return;
            _playedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedTime));
            OnPropertyChanged(nameof(ShowDate));
        }
    }

    public bool IsFavorited
    {
        get => _isFavorited;
        set
        {
            if (_isFavorited == value) return;
            _isFavorited = value;
            OnPropertyChanged();
        }
    }

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
    /// Gets a formatted time string for display.
    /// Shows time only if today, otherwise shows date and time.
    /// </summary>
    public string FormattedTime
    {
        get
        {
            // Use the user's regional short time pattern so 24-hour locales (e.g. pl-PL)
            // and the Windows "24-hour clock" setting are respected.
            string time = PlayedAt.ToString("t", CultureInfo.CurrentCulture);

            if (PlayedAt.Date == DateTime.Today)
            {
                return time;
            }

            return $"{FormatShortDateWithoutYear(PlayedAt)} {time}";
        }
    }

    // Culture short date (e.g. "10/5/2026" or "05.10.2026") with the year dropped
    // (-> "10/5" or "05.10").
    private static string FormatShortDateWithoutYear(DateTime value)
    {
        string pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        string trimmed = YearPatternRegex().Replace(pattern, string.Empty).Trim('/', '.', '-', ' ', ',');
        return value.ToString(string.IsNullOrWhiteSpace(trimmed) ? "M/d" : trimmed, CultureInfo.CurrentCulture);
    }

    [GeneratedRegex(@"[^dMm]*y+[^dMm]*")]
    private static partial Regex YearPatternRegex();

    /// <summary>
    /// Indicates whether to show the date (true if not today).
    /// </summary>
    public bool ShowDate => PlayedAt.Date != DateTime.Today;

    /// <summary>
    /// Indicates whether this item has artist info.
    /// </summary>
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    /// <summary>
    /// Indicates whether this item has title info.
    /// </summary>
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    /// <summary>
    /// Gets a unique key for comparison purposes.
    /// </summary>
    public string UniqueKey => $"{Artist?.ToLowerInvariant()}|{Title?.ToLowerInvariant()}|{StreamTitle?.ToLowerInvariant()}".Trim();

    /// <summary>
    /// Creates a PlaylistHistoryItem from stream metadata.
    /// </summary>
    public static PlaylistHistoryItem FromMetadata(StreamMetadata metadata, string stationName)
    {
        FavoritesService favoritesService = FavoritesService.Instance;

        return new PlaylistHistoryItem
        {
            Artist = metadata.Artist,
            Title = metadata.Title,
            StreamTitle = metadata.StreamTitle,
            StationName = stationName,
            PlayedAt = DateTime.Now,
            IsFavorited = favoritesService.IsFavorited(metadata)
        };
    }

    /// <summary>
    /// Converts this history item to StreamMetadata for favorites operations.
    /// </summary>
    public StreamMetadata ToStreamMetadata()
    {
        return new StreamMetadata
        {
            Artist = Artist,
            Title = Title,
            StreamTitle = StreamTitle
        };
    }

    /// <summary>
    /// Toggles the favorite status of this track.
    /// </summary>
    public void ToggleFavorite()
    {
        StreamMetadata metadata = ToStreamMetadata();
        IsFavorited = _favoritesService.ToggleFavorite(metadata, StationName);
    }

    /// <summary>
    /// Updates the favorited status from the service.
    /// </summary>
    public void RefreshFavoriteStatus()
    {
        StreamMetadata metadata = ToStreamMetadata();
        IsFavorited = _favoritesService.IsFavorited(metadata);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
