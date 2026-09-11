using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Helpers;
using Trdo.Models;
using Trdo.Services;
using Windows.System;

namespace Trdo.ViewModels;

public partial class NowPlayingViewModel : INotifyPropertyChanged
{
    private readonly RadioPlayerService _player = RadioPlayerService.Instance;
    private readonly FavoritesService _favoritesService = FavoritesService.Instance;
    private readonly PlaylistHistoryService _historyService = PlaylistHistoryService.Instance;

    private bool _isCurrentTrackFavorited;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCurrentTrackFavorited
    {
        get => _isCurrentTrackFavorited;
        set
        {
            if (_isCurrentTrackFavorited == value) return;
            _isCurrentTrackFavorited = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The playlist history showing recent tracks (from singleton service).
    /// </summary>
    public ObservableCollection<PlaylistHistoryItem> PlaylistHistory => _historyService.History;

    public NowPlayingViewModel()
    {
        PlayerViewModel.Shared.PropertyChanged += OnPlayerViewModelPropertyChanged;

        // Subscribe to metadata changes for UI updates
        _player.StreamMetadataChanged += OnStreamMetadataChanged;
        SettingsService.MusicSearchServicesChanged += OnMusicSearchServicesChanged;

        // Subscribe to favorites changes to update UI
        _favoritesService.FavoritesChanged += (_, _) =>
        {
            UpdateCurrentTrackFavoriteStatus();
        };

        // Initialize current track favorite status
        UpdateCurrentTrackFavoriteStatus();

        Debug.WriteLine($"[NowPlayingViewModel] Initialized with {PlaylistHistory.Count} history items from service");
    }

    private void OnPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.LocalTrackList) or nameof(PlayerViewModel.CurrentLocalTrackIndex))
        {
            Debug.WriteLine($"[NowPlayingViewModel] PlayerViewModel.{e.PropertyName} changed");
        }

        if (e.PropertyName is nameof(PlayerViewModel.IsPlaybackActive)
            or nameof(PlayerViewModel.IsRefreshingMetadata)
            or nameof(PlayerViewModel.CanRefreshMetadata))
        {
            OnPropertyChanged(nameof(IsPlaybackActive));
            OnPropertyChanged(nameof(IsRefreshingMetadata));
            OnPropertyChanged(nameof(CanRefreshMetadata));
        }

        if (e.PropertyName is nameof(PlayerViewModel.IsLocalMusicActive)
            or nameof(PlayerViewModel.LocalTrackList)
            or nameof(PlayerViewModel.CurrentLocalTrackIndex))
        {
            OnPropertyChanged(nameof(IsLocalMusicActive));
            OnPropertyChanged(nameof(HasEnabledMusicServices));
        }

        // Kept separate from the block above: LocalTrackList rebuilds the whole displayed
        // collection, which would reset the ListView's selection if raised on every track
        // change - it only needs to fire when the folder's track list itself changes.
        if (e.PropertyName is nameof(PlayerViewModel.IsLocalMusicActive)
            or nameof(PlayerViewModel.LocalTrackList))
        {
            OnPropertyChanged(nameof(LocalTrackList));
        }

        if (e.PropertyName is nameof(PlayerViewModel.CurrentLocalTrackIndex))
        {
            OnPropertyChanged(nameof(CurrentLocalTrackIndex));
        }
    }

    private void OnMusicSearchServicesChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsSpotifyEnabled));
        OnPropertyChanged(nameof(IsDiscogsEnabled));
        OnPropertyChanged(nameof(IsAppleMusicEnabled));
        OnPropertyChanged(nameof(IsYouTubeMusicEnabled));
        OnPropertyChanged(nameof(IsBandcampEnabled));
        OnPropertyChanged(nameof(HasEnabledMusicServices));
    }

    private void OnStreamMetadataChanged(object? sender, StreamMetadata metadata)
    {
        OnPropertyChanged(nameof(CurrentMetadata));
        OnPropertyChanged(nameof(StreamTitle));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ArtistDisplay));
        OnPropertyChanged(nameof(TitleDisplay));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(HasArtist));
        OnPropertyChanged(nameof(HasTitle));
        OnPropertyChanged(nameof(ShowStreamTitleOnly));
        OnPropertyChanged(nameof(ShowRawStreamTitle));
        OnPropertyChanged(nameof(DiscogsSearchQuery));
        OnPropertyChanged(nameof(SpotifySearchQuery));
        OnPropertyChanged(nameof(IsSpotifyEnabled));
        OnPropertyChanged(nameof(IsDiscogsEnabled));
        OnPropertyChanged(nameof(IsAppleMusicEnabled));
        OnPropertyChanged(nameof(IsYouTubeMusicEnabled));
        OnPropertyChanged(nameof(IsBandcampEnabled));
        OnPropertyChanged(nameof(HasEnabledMusicServices));

        // History is now managed by PlaylistHistoryService singleton
        UpdateCurrentTrackFavoriteStatus();
    }

    private void UpdateCurrentTrackFavoriteStatus()
    {
        IsCurrentTrackFavorited = _favoritesService.IsFavorited(CurrentMetadata);
    }

    public void ToggleCurrentTrackFavorite()
    {
        if (CurrentMetadata?.HasMetadata != true)
            return;

        string stationName = PlayerViewModel.Shared.SelectedStation?.Name ?? "Unknown Station";
        IsCurrentTrackFavorited = _favoritesService.ToggleFavorite(CurrentMetadata, stationName);
        Debug.WriteLine($"[NowPlayingViewModel] Toggled favorite for current track. IsFavorited: {IsCurrentTrackFavorited}");
    }

    public void ToggleHistoryItemFavorite(PlaylistHistoryItem? item)
    {
        if (item == null)
            return;

        item.ToggleFavorite();
        Debug.WriteLine($"[NowPlayingViewModel] Toggled favorite for history item: {item.DisplayText}. IsFavorited: {item.IsFavorited}");

        // If this is the current track, update that status too
        if (CurrentMetadata != null && item.UniqueKey == $"{CurrentMetadata.Artist?.ToLowerInvariant()}|{CurrentMetadata.Title?.ToLowerInvariant()}|{CurrentMetadata.StreamTitle?.ToLowerInvariant()}".Trim())
        {
            UpdateCurrentTrackFavoriteStatus();
        }
    }

    /// <summary>
    /// Gets the current stream metadata.
    /// </summary>
    public StreamMetadata CurrentMetadata => _player.CurrentMetadata;

    /// <summary>
    /// Gets the full stream title string.
    /// </summary>
    public string StreamTitle => CurrentMetadata?.StreamTitle ?? string.Empty;

    /// <summary>
    /// Gets the artist name if available.
    /// </summary>
    public string Artist => CurrentMetadata?.Artist ?? string.Empty;

    /// <summary>
    /// Gets the song/track title if available.
    /// </summary>
    public string Title => CurrentMetadata?.Title ?? string.Empty;

    public string ArtistDisplay => StreamMetadataFormatting.FormatArtist(CurrentMetadata);

    public string TitleDisplay => StreamMetadataFormatting.FormatTitle(CurrentMetadata);

    public bool IsPlaybackActive => PlayerViewModel.Shared.IsPlaybackActive;

    public bool IsRefreshingMetadata => PlayerViewModel.Shared.IsRefreshingMetadata;

    public bool CanRefreshMetadata => PlayerViewModel.Shared.CanRefreshMetadata;

    public Task RefreshMetadataAsync(CancellationToken cancellationToken = default) =>
        PlayerViewModel.Shared.RefreshMetadataAsync(cancellationToken);

    /// <summary>
    /// Gets the display-friendly now playing text.
    /// </summary>
    public string DisplayText => CurrentMetadata?.DisplayText ?? string.Empty;

    /// <summary>
    /// Indicates whether any meaningful metadata is available.
    /// </summary>
    public bool HasMetadata => CurrentMetadata?.HasMetadata ?? false;

    /// <summary>
    /// Indicates whether artist information is available.
    /// </summary>
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    /// <summary>
    /// Indicates whether title information is available.
    /// </summary>
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    /// <summary>
    /// Indicates whether to show only the raw stream title (when we couldn't parse artist/title).
    /// </summary>
    public bool ShowStreamTitleOnly => HasMetadata && !HasArtist && !HasTitle && !string.IsNullOrWhiteSpace(StreamTitle);

    /// <summary>
    /// Indicates whether to show the raw stream title section (only when we have parsed data to compare).
    /// </summary>
    public bool ShowRawStreamTitle => HasMetadata && (HasArtist || HasTitle) && !string.IsNullOrWhiteSpace(StreamTitle);

    /// <summary>
    /// Gets the search query for Discogs, URL-encoded.
    /// </summary>
    public string DiscogsSearchQuery
    {
        get
        {
            string searchText = DisplayText;
            if (string.IsNullOrWhiteSpace(searchText))
                searchText = StreamTitle;

            return Uri.EscapeDataString(searchText);
        }
    }

    /// <summary>
    /// Gets the search query for Spotify, URL-encoded.
    /// </summary>
    public string SpotifySearchQuery
    {
        get
        {
            string searchText = DisplayText;
            if (string.IsNullOrWhiteSpace(searchText))
                searchText = StreamTitle;

            return Uri.EscapeDataString(searchText);
        }
    }

    /// <summary>
    /// Opens Discogs search with the current track information.
    /// </summary>
    public async Task SearchOnDiscogs()
    {
        if (!HasMetadata)
            return;

        string url = $"https://www.discogs.com/search?q={DiscogsSearchQuery}";
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    /// <summary>
    /// Opens Spotify search with the current track information.
    /// Tries to open the local Spotify app first, falls back to web.
    /// </summary>
    public async Task SearchOnSpotify()
    {
        if (!HasMetadata)
            return;

        // Try to open the Spotify app first using the spotify: URI scheme
        string spotifyAppUri = $"spotify:search:{SpotifySearchQuery}";

        try
        {
            bool success = await Launcher.LaunchUriAsync(new Uri(spotifyAppUri));

            if (!success)
            {
                // Spotify app not installed or couldn't launch, fall back to web
                Debug.WriteLine("[NowPlayingViewModel] Spotify app not available, falling back to web");
                await OpenSpotifyWeb();
            }
        }
        catch (Exception ex)
        {
            // URI scheme not recognized or other error, fall back to web
            Debug.WriteLine($"[NowPlayingViewModel] Error launching Spotify app: {ex.Message}");
            await OpenSpotifyWeb();
        }
    }

    /// <summary>
    /// Opens Spotify web search as a fallback.
    /// </summary>
    private async Task OpenSpotifyWeb()
    {
        string webUrl = $"https://open.spotify.com/search/{SpotifySearchQuery}";
        await Launcher.LaunchUriAsync(new Uri(webUrl));
    }

    /// <summary>
    /// Opens Apple Music search with the current track information.
    /// </summary>
    public async Task SearchOnAppleMusic()
    {
        if (!HasMetadata)
            return;

        string searchText = DisplayText.Length > 0 ? DisplayText : StreamTitle;
        await MusicSearchLinkService.LaunchAppleMusicWebSearchAsync(searchText);
    }

    /// <summary>
    /// Opens YouTube Music search with the current track information.
    /// </summary>
    public async Task SearchOnYouTubeMusic()
    {
        if (!HasMetadata)
            return;

        string query = Uri.EscapeDataString(DisplayText.Length > 0 ? DisplayText : StreamTitle);
        string url = $"https://music.youtube.com/search?q={query}";
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    /// <summary>
    /// Opens Bandcamp search with the current track information.
    /// </summary>
    public async Task SearchOnBandcamp()
    {
        if (!HasMetadata)
            return;

        string query = Uri.EscapeDataString(DisplayText.Length > 0 ? DisplayText : StreamTitle);
        string url = $"https://bandcamp.com/search?q={query}";
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    /// <summary>
    /// Gets whether Spotify search links should be shown.
    /// </summary>
    public bool IsSpotifyEnabled => SettingsService.IsSpotifyEnabled;

    /// <summary>
    /// Gets whether Discogs search links should be shown.
    /// </summary>
    public bool IsDiscogsEnabled => SettingsService.IsDiscogsEnabled;

    /// <summary>
    /// Gets whether Apple Music search links should be shown.
    /// </summary>
    public bool IsAppleMusicEnabled => SettingsService.IsAppleMusicEnabled;

    /// <summary>
    /// Gets whether YouTube Music search links should be shown.
    /// </summary>
    public bool IsYouTubeMusicEnabled => SettingsService.IsYouTubeMusicEnabled;

    /// <summary>
    /// Gets whether Bandcamp search links should be shown.
    /// </summary>
    public bool IsBandcampEnabled => SettingsService.IsBandcampEnabled;

    /// <summary>
    /// Gets whether at least one music service search link should be shown. Never shown for
    /// local music - a locally-tagged filename has no guaranteed metadata match on any of
    /// these services.
    /// </summary>
    public bool HasEnabledMusicServices =>
        !IsLocalMusicActive &&
        (IsSpotifyEnabled ||
        IsDiscogsEnabled ||
        IsAppleMusicEnabled ||
        IsYouTubeMusicEnabled ||
        IsBandcampEnabled);

    /// <summary>True when the selected station is a local music folder rather than radio/white noise.</summary>
    public bool IsLocalMusicActive => PlayerViewModel.Shared.IsLocalMusicActive;

    /// <summary>Gets the index of the local track that is currently playing.</summary>
    public int CurrentLocalTrackIndex => PlayerViewModel.Shared.CurrentLocalTrackIndex;

    /// <summary>
    /// The current folder's tracks, in order.
    /// </summary>
    public IReadOnlyList<LocalTrackDisplayItem> LocalTrackList
    {
        get
        {
            IReadOnlyList<string> tracks = PlayerViewModel.Shared.LocalTrackList;
            Debug.WriteLine($"[NowPlayingViewModel] LocalTrackList rebuilt ({tracks.Count} track(s)) - this swaps the ListView's ItemsSource");

            List<LocalTrackDisplayItem> items = new(tracks.Count);
            for (int i = 0; i < tracks.Count; i++)
            {
                TagLib.File tfile = TagLib.File.Create(tracks[i]);

                string trackTitle = tfile.Tag.Title;

                // If no title tag, use the filename without extension
                if (string.IsNullOrWhiteSpace(trackTitle))
                    trackTitle = Path.GetFileNameWithoutExtension(tracks[i]);

                items.Add(new LocalTrackDisplayItem
                {
                    Index = i,
                    Path = tracks[i],
                    DisplayTitle = trackTitle,
                });
            }

            return items;
        }
    }

    /// <summary>Plays a track tapped from <see cref="LocalTrackList"/>.</summary>
    public async void PlayLocalTrackAtIndex(int index)
    {
        await PlayerViewModel.Shared.PlayLocalTrackAtIndexAsync(index);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
