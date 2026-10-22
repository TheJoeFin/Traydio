using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trdo.Models;
using Trdo.Services;

namespace Trdo.ViewModels;

public partial class AddStationViewModel : INotifyPropertyChanged
{
    private string _stationName = string.Empty;
    private string _streamUrl = string.Empty;
    private string? _homepage;
    private string? _faviconUrl;
    private bool _hasValidationError;
    private string _validationMessage = string.Empty;
    private string _pageTitle = LocalizationService.GetString("AddStation_AddPageTitle", "Add Radio Station");
    private PlayerViewModel? _playerViewModel;
    private RadioStation? _editingStation;
    /// <summary>
    /// The directory result this station is being added from, when the user arrived here by
    /// choosing "edit before adding" on a search result. Kept so <see cref="Save"/> can start
    /// from the full projection and keep the genre, country and language the search already
    /// knew, rather than saving only the four fields shown on this page.
    /// </summary>
    private RadioBrowserStation? _searchSource;
    private double _volumePercent = 100;
    private bool _hasBufferOverride;
    private double _bufferLevel;
    private bool _hasSongPopupDelayOverride;
    private double _songPopupDelaySeconds;
    private bool _heroImageFailed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<RadioStation>? StationAdded;

    public void SetPlayerViewModel(PlayerViewModel playerViewModel)
    {
        _playerViewModel = playerViewModel;

        // Seed the override slider from the app-wide level so switching the
        // toggle on starts where the station already sits rather than jumping to
        // Default. LoadStationForEdit replaces this for stations that have their
        // own value.
        BufferLevel = GlobalBufferLevel;
        SongPopupDelaySeconds = GlobalSongPopupDelaySeconds;
        OnPropertyChanged(nameof(BufferSummary));
        OnPropertyChanged(nameof(SongPopupDelaySummary));
    }

    public void LoadStationForEdit(RadioStation station)
    {
        _editingStation = station;
        StationName = station.Name;
        StreamUrl = station.StreamUrl;
        Homepage = station.Homepage;
        FaviconUrl = station.FaviconUrl;
        VolumePercent = station.Volume * 100;

        // A station with no override follows the global setting; seed the slider
        // with the global level anyway so switching the toggle on starts from
        // what the station is actually using rather than snapping to zero.
        HasBufferOverride = station.BufferLevel is not null;
        BufferLevel = station.BufferLevel ?? GlobalBufferLevel;

        HasSongPopupDelayOverride = station.SongPopupDelaySeconds is not null;
        SongPopupDelaySeconds = station.SongPopupDelaySeconds ?? GlobalSongPopupDelaySeconds;

        PageTitle = LocalizationService.GetString("AddStation_EditPageTitle", "Edit Radio Station");
    }

    public void LoadFromSearchResult(RadioBrowserStation searchStation)
    {
        _searchSource = searchStation;
        StationName = searchStation.Name;
        StreamUrl = searchStation.GetStreamUrl();
        Homepage = !string.IsNullOrWhiteSpace(searchStation.Homepage) ? searchStation.Homepage : null;
        FaviconUrl = !string.IsNullOrWhiteSpace(searchStation.Favicon) ? searchStation.Favicon : null;
        PageTitle = LocalizationService.GetString("AddStation_AddPageTitle", "Add Radio Station");
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set
        {
            if (value == _pageTitle) return;
            _pageTitle = value;
            OnPropertyChanged();
        }
    }

    public string StationName
    {
        get => _stationName;
        set
        {
            if (value == _stationName) return;
            _stationName = value;
            OnPropertyChanged();
            ValidateInput();
        }
    }

    public string StreamUrl
    {
        get => _streamUrl;
        set
        {
            if (value == _streamUrl) return;
            _streamUrl = value;
            OnPropertyChanged();
            ValidateInput();
        }
    }

    public string? Homepage
    {
        get => _homepage;
        set
        {
            if (value == _homepage) return;
            _homepage = value;
            OnPropertyChanged();
        }
    }

    public string? FaviconUrl
    {
        get => _faviconUrl;
        set
        {
            if (value == _faviconUrl) return;
            _faviconUrl = value;
            OnPropertyChanged();

            // A new URL deserves a fresh attempt even if the previous one 404'd.
            _heroImageFailed = false;
            OnPropertyChanged(nameof(HeroImageSource));
            OnPropertyChanged(nameof(HasHeroImage));
        }
    }

    /// <summary>
    /// The logo to preview, or <c>null</c> when there is nothing usable to show —
    /// blank, malformed, or a URL that already failed to load. Built here rather
    /// than through a converter because this window's XAML root is a Window, and
    /// x:Bind cannot resolve a StaticResource converter without a
    /// FrameworkElement root (same reason PlayerViewModel exposes ImageSource).
    /// </summary>
    public ImageSource? HeroImageSource
    {
        get
        {
            if (_heroImageFailed || string.IsNullOrWhiteSpace(FaviconUrl))
                return null;

            string trimmed = FaviconUrl.Trim();
            return IsValidUrl(trimmed) ? new BitmapImage(new Uri(trimmed, UriKind.Absolute)) : null;
        }
    }

    public bool HasHeroImage => HeroImageSource is not null;

    /// <summary>
    /// Called by the view when the preview image fails to download or decode, so
    /// the hero collapses instead of showing a broken-image box.
    /// </summary>
    public void NotifyHeroImageFailed()
    {
        if (_heroImageFailed) return;
        _heroImageFailed = true;
        OnPropertyChanged(nameof(HeroImageSource));
        OnPropertyChanged(nameof(HasHeroImage));
    }

    /// <summary>
    /// Per-station volume as a percentage (100 = full stream volume, up to 200
    /// for amplification), matching the range of the player's own volume control.
    /// </summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            value = Math.Clamp(value, 0, 200);
            if (value == _volumePercent) return;
            _volumePercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeDescription));
        }
    }

    public string VolumeDescription => $"{_volumePercent:0}%";

    /// <summary>
    /// Whether this station overrides the app-wide buffer level. When false the
    /// station follows the global setting and <see cref="BufferLevel"/> is only
    /// the seed value for the slider.
    /// </summary>
    public bool HasBufferOverride
    {
        get => _hasBufferOverride;
        set
        {
            if (value == _hasBufferOverride) return;
            _hasBufferOverride = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BufferSummary));
        }
    }

    public double BufferLevel
    {
        get => _bufferLevel;
        set
        {
            value = Math.Clamp(value, 0, 3);
            if (value == _bufferLevel) return;
            _bufferLevel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BufferLevelDescription));
            OnPropertyChanged(nameof(BufferSummary));
        }
    }

    public string BufferLevelDescription => StreamWatchdogService.DescribeBufferLevel(_bufferLevel);

    /// <summary>
    /// The app-wide buffer level this station falls back to when it has no override.
    /// </summary>
    public double GlobalBufferLevel => _playerViewModel?.BufferLevel ?? 0;

    /// <summary>
    /// One-line summary of what the station will actually buffer at, shown in the
    /// collapsed Advanced header so the setting is discoverable without expanding.
    /// </summary>
    public string BufferSummary => _hasBufferOverride
        ? LocalizedText.Format("Ui_AddStationViewModel_6782fbe204", "Buffer: {0} (this station)", BufferLevelDescription)
        : LocalizedText.Format("Ui_AddStationViewModel_68753a79d1", "Buffer: {0} (app setting)", StreamWatchdogService.DescribeBufferLevel(GlobalBufferLevel));

    /// <summary>
    /// Whether this station overrides the app-wide track info delay. When false the
    /// station follows the global setting and <see cref="SongPopupDelaySeconds"/> is only
    /// the seed value for the slider.
    /// </summary>
    public bool HasSongPopupDelayOverride
    {
        get => _hasSongPopupDelayOverride;
        set
        {
            if (value == _hasSongPopupDelayOverride) return;
            _hasSongPopupDelayOverride = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SongPopupDelaySummary));
        }
    }

    /// <summary>
    /// How long this station holds a metadata change back before the app shows it.
    /// Only applied when <see cref="HasSongPopupDelayOverride"/> is set.
    /// </summary>
    public double SongPopupDelaySeconds
    {
        get => _songPopupDelaySeconds;
        set
        {
            value = SongChangeAnnouncementPolicy.ClampDelay(value);
            if (value == _songPopupDelaySeconds) return;
            _songPopupDelaySeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SongPopupDelayDescription));
            OnPropertyChanged(nameof(SongPopupDelaySummary));
        }
    }

    public string SongPopupDelayDescription =>
        SongChangeAnnouncementPolicy.DescribeDelay(_songPopupDelaySeconds);

    /// <summary>The app-wide delay this station falls back to when it has no override.</summary>
    public double GlobalSongPopupDelaySeconds => SettingsService.TrackInfoDelaySeconds;

    /// <summary>The largest delay the slider offers, bounded by the policy.</summary>
    public double MaxSongPopupDelaySeconds => SongChangeAnnouncementPolicy.MaxDelaySeconds;

    /// <summary>
    /// One-line summary of the delay actually in force, shown in the collapsed Advanced
    /// header so the setting is discoverable without expanding.
    /// </summary>
    public string SongPopupDelaySummary => _hasSongPopupDelayOverride
        ? LocalizedText.Format("Ui_AddStationViewModel_a3d6447529", "Track info delay: {0} (this station)", SongPopupDelayDescription)
        : LocalizedText.Format("Ui_AddStationViewModel_758d997b51", "Track info delay: {0} (app setting)", SongChangeAnnouncementPolicy.DescribeDelay(GlobalSongPopupDelaySeconds));

    public bool HasValidationError
    {
        get => _hasValidationError;
        private set
        {
            if (value == _hasValidationError) return;
            _hasValidationError = value;
            OnPropertyChanged();
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (value == _validationMessage) return;
            _validationMessage = value;
            OnPropertyChanged();
        }
    }

    public bool CanSave => !string.IsNullOrWhiteSpace(StationName) &&
                           !string.IsNullOrWhiteSpace(StreamUrl) &&
                           !HasValidationError;

    private void ValidateInput()
    {
        HasValidationError = false;
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(StationName) && string.IsNullOrWhiteSpace(StreamUrl))
        {
            // Don't show error if both are empty (initial state)
            OnPropertyChanged(nameof(CanSave));
            return;
        }

        if (string.IsNullOrWhiteSpace(StationName))
        {
            HasValidationError = true;
            ValidationMessage = LocalizationService.GetString("AddStation_NameRequired", "Station name is required.");
        }
        else if (string.IsNullOrWhiteSpace(StreamUrl))
        {
            HasValidationError = true;
            ValidationMessage = LocalizationService.GetString("AddStation_UrlRequired", "Stream URL is required.");
        }
        else if (!IsValidUrl(StreamUrl))
        {
            HasValidationError = true;
            ValidationMessage = LocalizationService.GetString("AddStation_InvalidUrl", "Please enter a valid HTTP or HTTPS URL.");
        }

        OnPropertyChanged(nameof(CanSave));
    }

    private static bool IsValidUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    public bool Save()
    {
        ValidateInput();

        if (!CanSave)
        {
            return false;
        }

        if (_editingStation != null)
        {
            // Edit mode - update existing station
            _editingStation.Name = StationName.Trim();
            _editingStation.StreamUrl = StreamUrl.Trim();
            _editingStation.Homepage = !string.IsNullOrWhiteSpace(Homepage) ? Homepage.Trim() : null;
            _editingStation.FaviconUrl = !string.IsNullOrWhiteSpace(FaviconUrl) ? FaviconUrl.Trim() : null;
            _editingStation.Volume = VolumePercent / 100;
            _editingStation.BufferLevel = HasBufferOverride ? BufferLevel : null;
            _editingStation.SongPopupDelaySeconds = HasSongPopupDelayOverride ? SongPopupDelaySeconds : null;

            // Save the updated stations list - this will automatically reinitialize if it's the selected station
            _playerViewModel?.SaveStations();
        }
        else
        {
            // Add mode - create new station. Start from the directory result when there is
            // one so its genre, country and language ride along, then layer the user's edits
            // on top; what they typed always wins over what the directory said.
            RadioStation newStation = _searchSource?.ToRadioStation()
                ?? new RadioStation { Name = string.Empty, StreamUrl = string.Empty };

            newStation.Name = StationName.Trim();
            newStation.StreamUrl = StreamUrl.Trim();
            newStation.Homepage = !string.IsNullOrWhiteSpace(Homepage) ? Homepage.Trim() : null;
            newStation.FaviconUrl = !string.IsNullOrWhiteSpace(FaviconUrl) ? FaviconUrl.Trim() : null;
            newStation.Volume = VolumePercent / 100;
            newStation.BufferLevel = HasBufferOverride ? BufferLevel : null;
            newStation.SongPopupDelaySeconds = HasSongPopupDelayOverride ? SongPopupDelaySeconds : null;

            // Add to PlayerViewModel if available
            _playerViewModel?.AddStation(newStation);

            // Raise event for listeners
            StationAdded?.Invoke(this, newStation);
        }

        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
