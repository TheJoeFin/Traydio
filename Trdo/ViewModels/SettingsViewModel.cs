using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services;
using Trdo.Services.Playback;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Trdo.ViewModels;

public partial class SettingsViewModel : INotifyPropertyChanged
{
    private readonly PlayerViewModel _playerViewModel;
    private bool _isStartupEnabled;
    private bool _isStartupToggleEnabled = true;
    private string _startupToggleText = LocalizationService.GetString("Toggle_Off", "Off");
    private string _watchdogToggleText = LocalizationService.GetString("Toggle_Off", "Off");
    private string _autoBufferToggleText = LocalizationService.GetString("Toggle_Off", "Off");
    private string _autoPlayOnStartupToggleText = LocalizationService.GetString("Toggle_Off", "Off");
    private string _spotifyToggleText = LocalizationService.GetString("Toggle_On", "On");
    private string _discogsToggleText = LocalizationService.GetString("Toggle_On", "On");
    private string _appleMusicToggleText = LocalizationService.GetString("Toggle_On", "On");
    private string _youtubeMusicToggleText = LocalizationService.GetString("Toggle_On", "On");
    private string _bandcampToggleText = LocalizationService.GetString("Toggle_Off", "Off");
    private string _songChangePopupToggleText = LocalizationService.GetString("Toggle_Off", "Off");
    private string _radioStaticToggleText = LocalizationService.GetString("Toggle_Off", "Off");
    private StartupTask? _startupTask;
    private bool _initDone;
    private bool _isLanguageRestartPending;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel()
    {
        _playerViewModel = PlayerViewModel.Shared;

        // Subscribe to PlayerViewModel property changes
        _playerViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlayerViewModel.WatchdogEnabled))
            {
                OnPropertyChanged(nameof(IsWatchdogEnabled));
                WatchdogToggleText = GetToggleText(_playerViewModel.WatchdogEnabled);
            }
            else if (args.PropertyName == nameof(PlayerViewModel.AutoBufferIncreaseEnabled))
            {
                OnPropertyChanged(nameof(IsAutoBufferIncreaseEnabled));
                AutoBufferToggleText = GetToggleText(_playerViewModel.AutoBufferIncreaseEnabled);
            }
            else if (args.PropertyName == nameof(PlayerViewModel.BufferLevel))
            {
                OnPropertyChanged(nameof(BufferLevel));
                OnPropertyChanged(nameof(BufferLevelDescription));
            }
            else if (args.PropertyName == nameof(PlayerViewModel.SilenceTimeoutSeconds))
            {
                OnPropertyChanged(nameof(SilenceTimeoutSeconds));
                OnPropertyChanged(nameof(SilenceTimeoutDisplay));
            }
        };

        // Initialize toggle text
        WatchdogToggleText = GetToggleText(_playerViewModel.WatchdogEnabled);
        AutoBufferToggleText = GetToggleText(_playerViewModel.AutoBufferIncreaseEnabled);
        AutoPlayOnStartupToggleText = GetToggleText(SettingsService.AutoPlayOnStartup);
        SpotifyToggleText = GetToggleText(SettingsService.IsSpotifyEnabled);
        DiscogsToggleText = GetToggleText(SettingsService.IsDiscogsEnabled);
        AppleMusicToggleText = GetToggleText(SettingsService.IsAppleMusicEnabled);
        YouTubeMusicToggleText = GetToggleText(SettingsService.IsYouTubeMusicEnabled);
        BandcampToggleText = GetToggleText(SettingsService.IsBandcampEnabled);
        SongChangePopupToggleText = GetToggleText(SettingsService.IsSongChangePopupEnabled);
        RadioStaticToggleText = GetToggleText(SettingsService.IsRadioStaticEnabled);

        // Initialize startup task
        _ = InitializeStartupTaskAsync();
    }

    private static string GetToggleText(bool enabled) => enabled
        ? LocalizationService.GetString("Toggle_On", "On")
        : LocalizationService.GetString("Toggle_Off", "Off");

    public int LanguageSelectionIndex
    {
        get
        {
            int index = Array.IndexOf(LocalizationService.LanguagePickerOptions, SettingsService.AppLanguage);
            return index < 0 ? 0 : index;
        }
        set
        {
            string[] languages = LocalizationService.LanguagePickerOptions;
            string languageTag = value >= 0 && value < languages.Length
                ? languages[value]
                : LocalizationService.SystemLanguage;

            if (languageTag == SettingsService.AppLanguage) return;

            LogService.Info("Localization", $"User changed language: '{SettingsService.AppLanguage}' -> '{languageTag}'");
            SettingsService.AppLanguage = languageTag;
            OnPropertyChanged();

            // x:Uid resources are resolved while each XAML tree loads, so already-created UI keeps
            // the old language until the app starts again.
            IsLanguageRestartPending = true;
        }
    }

    /// <summary>
    /// True once the language has been changed in this session, so the UI can ask for a restart.
    /// </summary>
    public bool IsLanguageRestartPending
    {
        get => _isLanguageRestartPending;
        private set
        {
            if (value == _isLanguageRestartPending) return;
            _isLanguageRestartPending = value;
            OnPropertyChanged();
        }
    }

    public bool IsStartupEnabled
    {
        get => _isStartupEnabled;
        set
        {
            if (value == _isStartupEnabled) return;
            _isStartupEnabled = value;
            OnPropertyChanged();
            StartupToggleText = GetToggleText(value);

            // Apply the change
            _ = ApplyStartupStateAsync(value);
        }
    }

    public bool IsStartupToggleEnabled
    {
        get => _isStartupToggleEnabled;
        set
        {
            if (value == _isStartupToggleEnabled) return;
            _isStartupToggleEnabled = value;
            OnPropertyChanged();
        }
    }

    public string StartupToggleText
    {
        get => _startupToggleText;
        set
        {
            if (value == _startupToggleText) return;
            _startupToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether the app should automatically start playing the last selected station on startup.
    /// </summary>
    public bool IsAutoPlayOnStartupEnabled
    {
        get => SettingsService.AutoPlayOnStartup;
        set
        {
            if (value == SettingsService.AutoPlayOnStartup) return;
            SettingsService.AutoPlayOnStartup = value;
            OnPropertyChanged();
            AutoPlayOnStartupToggleText = GetToggleText(value);
        }
    }

    public string AutoPlayOnStartupToggleText
    {
        get => _autoPlayOnStartupToggleText;
        set
        {
            if (value == _autoPlayOnStartupToggleText) return;
            _autoPlayOnStartupToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether Spotify search links are shown in Now Playing and Favorites.
    /// </summary>
    public bool IsSpotifyEnabled
    {
        get => SettingsService.IsSpotifyEnabled;
        set
        {
            if (value == SettingsService.IsSpotifyEnabled) return;
            SettingsService.IsSpotifyEnabled = value;
            OnPropertyChanged();
            SpotifyToggleText = GetToggleText(value);
        }
    }

    public string SpotifyToggleText
    {
        get => _spotifyToggleText;
        set
        {
            if (value == _spotifyToggleText) return;
            _spotifyToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether Discogs search links are shown in Now Playing and Favorites.
    /// </summary>
    public bool IsDiscogsEnabled
    {
        get => SettingsService.IsDiscogsEnabled;
        set
        {
            if (value == SettingsService.IsDiscogsEnabled) return;
            SettingsService.IsDiscogsEnabled = value;
            OnPropertyChanged();
            DiscogsToggleText = GetToggleText(value);
        }
    }

    public string DiscogsToggleText
    {
        get => _discogsToggleText;
        set
        {
            if (value == _discogsToggleText) return;
            _discogsToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether Apple Music search links are shown in Now Playing and Favorites.
    /// </summary>
    public bool IsAppleMusicEnabled
    {
        get => SettingsService.IsAppleMusicEnabled;
        set
        {
            if (value == SettingsService.IsAppleMusicEnabled) return;
            SettingsService.IsAppleMusicEnabled = value;
            OnPropertyChanged();
            AppleMusicToggleText = GetToggleText(value);
        }
    }

    public string AppleMusicToggleText
    {
        get => _appleMusicToggleText;
        set
        {
            if (value == _appleMusicToggleText) return;
            _appleMusicToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether YouTube Music search links are shown in Now Playing and Favorites.
    /// </summary>
    public bool IsYouTubeMusicEnabled
    {
        get => SettingsService.IsYouTubeMusicEnabled;
        set
        {
            if (value == SettingsService.IsYouTubeMusicEnabled) return;
            SettingsService.IsYouTubeMusicEnabled = value;
            OnPropertyChanged();
            YouTubeMusicToggleText = GetToggleText(value);
        }
    }

    public string YouTubeMusicToggleText
    {
        get => _youtubeMusicToggleText;
        set
        {
            if (value == _youtubeMusicToggleText) return;
            _youtubeMusicToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether Bandcamp search links are shown in Now Playing and Favorites.
    /// </summary>
    public bool IsBandcampEnabled
    {
        get => SettingsService.IsBandcampEnabled;
        set
        {
            if (value == SettingsService.IsBandcampEnabled) return;
            SettingsService.IsBandcampEnabled = value;
            OnPropertyChanged();
            BandcampToggleText = GetToggleText(value);
        }
    }

    public string BandcampToggleText
    {
        get => _bandcampToggleText;
        set
        {
            if (value == _bandcampToggleText) return;
            _bandcampToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the tray icon click behavior.
    /// 0 = left click plays/pauses, right click opens flyout (default).
    /// 1 = left click opens flyout, right click plays/pauses.
    /// </summary>
    public int TrayClickBehavior
    {
        get => SettingsService.TrayClickBehavior;
        set
        {
            if (value == SettingsService.TrayClickBehavior) return;
            SettingsService.TrayClickBehavior = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether a brief on-screen popup appears near the taskbar
    /// whenever the playing song changes.
    /// </summary>
    public bool IsSongChangePopupEnabled
    {
        get => SettingsService.IsSongChangePopupEnabled;
        set
        {
            if (value == SettingsService.IsSongChangePopupEnabled) return;
            SettingsService.IsSongChangePopupEnabled = value;
            OnPropertyChanged();
            SongChangePopupToggleText = GetToggleText(value);
        }
    }

    /// <summary>
    /// App-wide delay between a song change and the app showing it, in seconds. Applies to
    /// every surface, not just the popup. Stations that run further ahead can override this
    /// individually.
    /// </summary>
    public double TrackInfoDelaySeconds
    {
        get => SettingsService.TrackInfoDelaySeconds;
        set
        {
            double clamped = SongChangeAnnouncementPolicy.ClampDelay(value);
            if (Math.Abs(clamped - SettingsService.TrackInfoDelaySeconds) < 0.0001) return;
            SettingsService.TrackInfoDelaySeconds = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrackInfoDelayDescription));
        }
    }

    public string TrackInfoDelayDescription =>
        SongChangeAnnouncementPolicy.DescribeDelay(SettingsService.TrackInfoDelaySeconds);

    /// <summary>Upper bound offered by the delay slider.</summary>
    public double MaxTrackInfoDelaySeconds => SongChangeAnnouncementPolicy.MaxDelaySeconds;

    /// <summary>
    /// How long the popup stays on screen once it appears, in seconds. App-wide: this is
    /// about reading speed rather than anything a station does, so there is no override.
    /// </summary>
    public double SongChangePopupDwellSeconds
    {
        get => SettingsService.SongChangePopupDwellSeconds;
        set
        {
            double clamped = SongChangeAnnouncementPolicy.ClampDwell(value);
            if (Math.Abs(clamped - SettingsService.SongChangePopupDwellSeconds) < 0.0001) return;
            SettingsService.SongChangePopupDwellSeconds = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SongChangePopupDwellDescription));
        }
    }

    public string SongChangePopupDwellDescription =>
        SongChangeAnnouncementPolicy.DescribeDwell(SettingsService.SongChangePopupDwellSeconds);

    /// <summary>Bounds offered by the dwell slider.</summary>
    public double MinSongChangePopupDwellSeconds => SongChangeAnnouncementPolicy.MinDwellSeconds;

    public double MaxSongChangePopupDwellSeconds => SongChangeAnnouncementPolicy.MaxDwellSeconds;

    public string SongChangePopupToggleText
    {
        get => _songChangePopupToggleText;
        set
        {
            if (value == _songChangePopupToggleText) return;
            _songChangePopupToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether generated FM radio static plays while a stream buffers.
    /// </summary>
    public bool IsRadioStaticEnabled
    {
        get => SettingsService.IsRadioStaticEnabled;
        set
        {
            if (value == SettingsService.IsRadioStaticEnabled) return;
            SettingsService.IsRadioStaticEnabled = value;
            OnPropertyChanged();
            RadioStaticToggleText = GetToggleText(value);
        }
    }

    public string RadioStaticToggleText
    {
        get => _radioStaticToggleText;
        set
        {
            if (value == _radioStaticToggleText) return;
            _radioStaticToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// ComboBox index for the playback engine mode. UI order (Auto, Native only,
    /// Native preferred) no longer matches the stored enum values, so the index
    /// is mapped rather than cast.
    /// </summary>
    public int PlaybackEngineModeIndex
    {
        get => SettingsService.PlaybackEngineMode switch
        {
            PlaybackEngineMode.NativeOnly => 1,
            PlaybackEngineMode.NativePreferred => 2,
            _ => 0
        };
        set
        {
            PlaybackEngineMode mode = value switch
            {
                1 => PlaybackEngineMode.NativeOnly,
                2 => PlaybackEngineMode.NativePreferred,
                _ => PlaybackEngineMode.Auto
            };

            if (mode == SettingsService.PlaybackEngineMode)
            {
                return;
            }

            SettingsService.PlaybackEngineMode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackEngineModeDescription));
        }
    }

    public string PlaybackEngineModeDescription =>
        SettingsService.PlaybackEngineMode switch
        {
            PlaybackEngineMode.NativeOnly => "Windows Media Foundation only",
            PlaybackEngineMode.NativePreferred => "Windows first, LibVLC fallback",
            _ => "LibVLC first, Windows fallback"
        };

    public bool AllowSleepWhilePlaying
    {
        get => SettingsService.AllowSleepWhilePlaying;
        set
        {
            if (value == SettingsService.AllowSleepWhilePlaying) return;
            SettingsService.AllowSleepWhilePlaying = value;
            PowerManagementService.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AllowSleepToggleText));
        }
    }

    public string AllowSleepToggleText => AllowSleepWhilePlaying
        ? "PC can sleep while playing"
        : "PC stays awake while playing";

    public bool IsWatchdogEnabled
    {
        get => _playerViewModel.WatchdogEnabled;
        set
        {
            if (value == _playerViewModel.WatchdogEnabled) return;
            _playerViewModel.WatchdogEnabled = value;
            OnPropertyChanged();
            WatchdogToggleText = GetToggleText(value);
        }
    }

    public string WatchdogToggleText
    {
        get => _watchdogToggleText;
        set
        {
            if (value == _watchdogToggleText) return;
            _watchdogToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether auto-buffer increase is enabled.
    /// When enabled, the buffer level automatically increases when stutter is detected.
    /// </summary>
    public bool IsAutoBufferIncreaseEnabled
    {
        get => _playerViewModel.AutoBufferIncreaseEnabled;
        set
        {
            if (value == _playerViewModel.AutoBufferIncreaseEnabled) return;
            _playerViewModel.AutoBufferIncreaseEnabled = value;
            OnPropertyChanged();
            AutoBufferToggleText = GetToggleText(value);
        }
    }

    public string AutoBufferToggleText
    {
        get => _autoBufferToggleText;
        set
        {
            if (value == _autoBufferToggleText) return;
            _autoBufferToggleText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the current buffer level (0-3).
    /// 0 = Default, 1 = Medium, 2 = Large, 3 = Extra Large
    /// </summary>
    public double BufferLevel
    {
        get => _playerViewModel.BufferLevel;
        set
        {
            if (Math.Abs(value - _playerViewModel.BufferLevel) < 0.0001) return;
            _playerViewModel.BufferLevel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BufferLevelDescription));
        }
    }

    /// <summary>
    /// Gets a human-readable description of the current buffer level.
    /// </summary>
    public string BufferLevelDescription => _playerViewModel.BufferLevelDescription;

    /// <summary>
    /// Gets or sets the silence detection timeout in seconds.
    /// If audio is silent for longer than this, the stream will be restarted.
    /// </summary>
    public double SilenceTimeoutSeconds
    {
        get => _playerViewModel.SilenceTimeoutSeconds;
        set
        {
            if (Math.Abs(value - _playerViewModel.SilenceTimeoutSeconds) < 0.01) return;
            _playerViewModel.SilenceTimeoutSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SilenceTimeoutDisplay));
        }
    }

    /// <summary>
    /// Gets a formatted display string for the current silence timeout value.
    /// </summary>
    public string SilenceTimeoutDisplay => $"{SilenceTimeoutSeconds:0}s";

    private async Task InitializeStartupTaskAsync()
    {
        try
        {
            _startupTask = await StartupTask.GetAsync("TrdoStartup").AsTask();
            _initDone = true;
            UpdateStartupStateFromTask();
        }
        catch
        {
            // Could not get StartupTask (likely unpackaged). Disable toggle.
            IsStartupToggleEnabled = false;
            IsStartupEnabled = false;
        }
    }

    private void UpdateStartupStateFromTask()
    {
        if (_startupTask is null) return;

        switch (_startupTask.State)
        {
            case StartupTaskState.Enabled:
                IsStartupToggleEnabled = true;
                _isStartupEnabled = true;
                OnPropertyChanged(nameof(IsStartupEnabled));
                StartupToggleText = GetToggleText(true);
                break;
            case StartupTaskState.Disabled:
                IsStartupToggleEnabled = true;
                _isStartupEnabled = false;
                OnPropertyChanged(nameof(IsStartupEnabled));
                StartupToggleText = GetToggleText(false);
                break;
            case StartupTaskState.DisabledByUser:
            case StartupTaskState.DisabledByPolicy:
            default:
                IsStartupToggleEnabled = false;
                _isStartupEnabled = false;
                OnPropertyChanged(nameof(IsStartupEnabled));
                StartupToggleText = GetToggleText(false);
                break;
        }
    }

    private async Task ApplyStartupStateAsync(bool enable)
    {
        if (!_initDone || _startupTask is null) return;

        try
        {
            if (enable)
            {
                if (_startupTask.State == StartupTaskState.Disabled)
                {
                    await _startupTask.RequestEnableAsync().AsTask();
                }
            }
            else
            {
                if (_startupTask.State == StartupTaskState.Enabled)
                {
                    _startupTask.Disable();
                }
            }
        }
        catch
        {
            // ignore errors
        }

        // Reflect actual state after operation
        UpdateStartupStateFromTask();
    }

    /// <summary>
    /// Imports radio stations from a playlist file (M3U, M3U8, or PLS).
    /// </summary>
    public async Task<int> ImportStationsAsync(nint windowHandle)
    {
        FileOpenPicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.FileTypeFilter.Add(".m3u");
        picker.FileTypeFilter.Add(".m3u8");
        picker.FileTypeFilter.Add(".pls");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
            return 0;

        string content = await FileIO.ReadTextAsync(file);
        List<RadioStation> imported = PlaylistImportExportService.ImportFromFile(file.Path, content);

        if (imported.Count == 0)
            return 0;

        PlayerViewModel player = PlayerViewModel.Shared;
        int addedCount = 0;
        foreach (RadioStation station in imported)
        {
            bool alreadyExists = false;
            foreach (RadioStation existing in player.Stations)
            {
                if (string.Equals(existing.Name, station.Name, StringComparison.Ordinal) &&
                    string.Equals(existing.StreamUrl, station.StreamUrl, StringComparison.Ordinal) &&
                    string.Equals(existing.Homepage, station.Homepage, StringComparison.Ordinal) &&
                    string.Equals(existing.FaviconUrl, station.FaviconUrl, StringComparison.Ordinal))
                {
                    alreadyExists = true;
                    break;
                }
            }

            if (!alreadyExists)
            {
                // This adds to the collection directly rather than going through
                // PlayerViewModel.AddStation, so stamp what that path would have stamped.
                // Imported stations carry no directory details, which makes them exactly the
                // candidates the "refresh station info" command exists for.
                station.Id = StationIdentityPolicy.NewId();
                station.DateAdded ??= DateTimeOffset.UtcNow;

                player.Stations.Add(station);
                addedCount++;
            }
        }

        if (addedCount > 0)
            RadioStationService.Instance.SaveStations(player.Stations);

        return addedCount;
    }

    /// <summary>
    /// Exports all radio stations to a playlist file (M3U, M3U8, or PLS).
    /// </summary>
    public async Task<bool> ExportStationsAsync(nint windowHandle)
    {
        PlayerViewModel player = PlayerViewModel.Shared;
        if (player.Stations.Count == 0)
            return false;

        FileSavePicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.SuggestedFileName = "Traydio Stations";
        picker.FileTypeChoices.Add("M3U Playlist", [".m3u"]);
        picker.FileTypeChoices.Add("PLS Playlist", [".pls"]);

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
            return false;

        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        string content = extension == ".pls"
            ? PlaylistImportExportService.ExportToPls(player.Stations)
            : PlaylistImportExportService.ExportToM3u(player.Stations);

        await FileIO.WriteTextAsync(file, content);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
