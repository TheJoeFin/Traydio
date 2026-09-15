using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Controls;
using Trdo.Services;
using Trdo.Services.Audio;
using Trdo.Services.Playback;
using Trdo.ViewModels;
using Windows.UI.ViewManagement;
using WinUIEx;

namespace Trdo;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private TrayPopupWindow? _trayPopupWindow;
    private MiniPlayerWindow? _miniPlayerWindow;
    private SongChangePopupWindow? _songChangePopupWindow;
    private string? _lastKnownNowPlayingDisplayText;

    /// <summary>
    /// When the current station started playing, or null once a metadata observation has
    /// consumed it. A track observed inside
    /// <see cref="SongChangeAnnouncementPolicy.StationStartGrace"/> of it is the one the
    /// station opened on rather than a mid-stream change.
    /// </summary>
    private DateTimeOffset? _stationStartedAtUtc;

    /// <summary>
    /// The last <see cref="PlayerViewModel.IsPlaying"/> value acted on. The view model re-raises
    /// the property on every playback-state event rather than only on a change, so a station
    /// that stutters on connect reports "playing" repeatedly; without this, each report would
    /// re-open the station-start window and hand a later track the startup delay instead of the
    /// station's own.
    /// </summary>
    private bool _wasPlaying;

    /// <summary>
    /// The text the popup has most recently been asked to show for the current station, as
    /// opposed to <see cref="_lastKnownNowPlayingDisplayText"/>, which is only the baseline for
    /// spotting a change. The two differ whenever metadata is observed without being announced —
    /// which is the normal case at startup, because a station's first metadata usually lands
    /// while it is still buffering, before playback begins. Announcing at the start of playback
    /// therefore has to ask "has this track been shown?", not "is this track new?".
    /// </summary>
    private string? _lastAnnouncedDisplayText;
    private readonly PlayerViewModel _playerVm = PlayerViewModel.Shared;
    private readonly UISettings _uiSettings = new();
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _trayIconRestoreEvent;
    private DispatcherQueue? _uiDispatcherQueue;
#if DEBUG
    private DispatcherQueueTimer? _songChangePopupPreviewTimer;
#endif

    /// <summary>
    /// Maximum length for the now playing text in the tooltip before truncation.
    /// </summary>
    private const int MaxTooltipNowPlayingLength = 60;

    /// <summary>
    /// Stand-in title for the Settings demo when nothing is playing. Long
    /// enough that the preview shows how a real artist/title pair sits in the
    /// pill rather than a token that fits with room to spare.
    /// </summary>
    private const string DemoSongText = "Fleetwood Mac - Dreams";

    /// <summary>
    /// Maximum length for the full tray icon tooltip (NOTIFYICONDATA limit).
    /// </summary>
    private const int MaxTrayTooltipLength = 128;

    public App()
    {
        LocalizationService.ApplyLanguage(SettingsService.AppLanguage);
        InitializeComponent();
        _playerVm.PropertyChanged += PlayerVmOnPropertyChanged;

        // Initialize PlaylistHistoryService early so it captures metadata from the start
        PlaylistHistoryService.EnsureInitialized();

        // Same for playback errors: the service has to be listening before the first
        // failure, and it needs this (UI) thread's dispatcher for its review timer.
        PlaybackErrorService.EnsureInitialized();

        // Radio static listens for buffering, which can start before any window is shown.
        RadioStaticService.Instance.Initialize();

        // Subscribe to theme change events
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;

        SettingsService.SongChangePopupEnabledChanged += OnSongChangePopupEnabledChanged;
    }

    public void ShowMiniPlayerWindow()
    {
        WindowPlacementService.CapturePointerAnchor();

        if (_miniPlayerWindow is null)
        {
            _miniPlayerWindow = new MiniPlayerWindow();
            WindowHelper.Track(_miniPlayerWindow);
            _miniPlayerWindow.Closed += (_, _) => _miniPlayerWindow = null;
        }

        // Position before activating so the window never flashes at a stale location.
        WindowPlacementService.PositionWindowNearAnchor(_miniPlayerWindow, 320, 220);
        _miniPlayerWindow.Activate();
    }

    /// <summary>
    /// Reacts to a stream metadata change by (maybe) showing the song-change
    /// popup. Meaningful display text updates the remembered "last known"
    /// value so the dedupe/baseline logic in
    /// <see cref="SongChangeAnnouncementPolicy"/> works whether or not the
    /// popup is currently enabled. Blank metadata is ignored so a transient
    /// clear cannot make the same song appear new.
    /// </summary>
    /// <remarks>
    /// The track-info delay is not applied here. It is applied upstream, in
    /// <see cref="Services.Metadata.MetadataPublishGate"/>, so that the popup, the window, the
    /// mini player and the media controls all name the same track at the same moment; by the
    /// time the metadata reaches this method it is already the track the listener can hear.
    /// </remarks>
    private void HandleSongChangePopup()
    {
        string displayText = _playerVm.CurrentMetadata.DisplayText.Trim();
        if (displayText.Length == 0)
        {
            LogService.Info("SongChangePopup", "Metadata observed but blank; ignoring");
            return;
        }

        // Whatever this observation was — an announcement or just a new baseline — it was the
        // track already playing when the station started. Anything after it is a real
        // mid-stream change, which the publish gate has already held back for us.
        bool isFirstSinceStart = SongChangeAnnouncementPolicy.IsWithinStationStartGrace(
            _stationStartedAtUtc, DateTimeOffset.UtcNow);

        string? previous = _lastKnownNowPlayingDisplayText;
        bool isEnabled = SettingsService.IsSongChangePopupEnabled;
        bool shouldAnnounce = SongChangeAnnouncementPolicy.ShouldAnnounce(
            previous,
            displayText,
            isEnabled,
            isFirstSinceStart);

        LogService.Info("SongChangePopup",
            $"Metadata observed: '{displayText}' (previous='{previous ?? "<none>"}', " +
            $"enabled={isEnabled}, isPlaying={_wasPlaying}, firstSinceStart={isFirstSinceStart}, " +
            $"stationStartedAt={_stationStartedAtUtc?.ToString("HH:mm:ss.fff") ?? "<not started>"}) " +
            $"-> announce={shouldAnnounce}");

        _lastKnownNowPlayingDisplayText = displayText;
        _stationStartedAtUtc = null;

        if (!shouldAnnounce)
            return;

        _lastAnnouncedDisplayText = displayText;
        ShowSongChangePopup(displayText);
    }

    /// <summary>
    /// Shows the track a station opens with, at the moment playback actually begins.
    /// </summary>
    /// <remarks>
    /// Metadata providers are started alongside the play call, but the backend only reports
    /// itself as playing once the stream has opened — a second or more later on a slow connect.
    /// The opening track therefore usually arrives while the app is still buffering, when there
    /// is no station-start window open and no baseline to differ from, so it can only be
    /// recorded rather than shown. Because the metadata orchestrator suppresses repeats, it
    /// would never be re-offered, and the popup would sit out the whole first track. This
    /// re-offers it once audio is running, guarded by what has actually been shown so a
    /// stuttering connect (which reports "playing" more than once) cannot show it twice.
    /// </remarks>
    private void AnnounceCurrentTrackAtStationStart()
    {
        string displayText = _playerVm.CurrentMetadata.DisplayText.Trim();

        if (displayText.Length == 0)
        {
            // Nothing to show yet. The window stays open, so whichever track the stream
            // reports first will announce through the normal path.
            LogService.Info("SongChangePopup", "No metadata yet at station start; awaiting the stream's first track");
            return;
        }

        if (string.Equals(displayText, _lastAnnouncedDisplayText, StringComparison.Ordinal))
        {
            // Already handled, so close the window this re-report opened. A stuttering connect
            // reports "playing" several times; leaving it open would hand the next genuine
            // track change the startup delay instead of the station's own.
            _stationStartedAtUtc = null;
            LogService.Info("SongChangePopup", $"'{displayText}' already shown for this station; not repeating");
            return;
        }

        LogService.Info("SongChangePopup", $"Station opened on '{displayText}'; showing it now");

        // Drop the baseline so the shared path reads this as the station's opening track
        // rather than an unchanged repeat of what was observed while buffering.
        _lastKnownNowPlayingDisplayText = null;
        HandleSongChangePopup();
    }

    private void ShowSongChangePopup(string displayText)
    {
        EnsureSongChangePopupWindow();

        if (_songChangePopupWindow is null)
        {
            LogService.Warn("SongChangePopup", $"No popup window available; '{displayText}' not shown");
            return;
        }

        LogService.Info("SongChangePopup", $"Showing popup for '{displayText}'");
        _songChangePopupWindow.ShowSongChange(displayText);
    }

    /// <summary>
    /// Shows the song change popup on demand so Settings can demonstrate what
    /// it looks like. Uses whatever is playing when there is something, since
    /// seeing a real title is the most honest preview, and a sample otherwise.
    /// </summary>
    /// <remarks>
    /// Deliberately bypasses the enabled setting: the preview is most useful precisely when
    /// popups are still off and the user is deciding whether to turn them on. It shows the
    /// track currently published rather than whatever the station has most recently
    /// announced, so the preview names the song the user can hear.
    /// </remarks>
    public void ShowSongChangePopupDemo()
    {
        string displayText = _playerVm.NowPlaying.Trim();

        if (displayText.Length == 0)
            displayText = DemoSongText;

        ShowSongChangePopup(displayText);
    }

    private void EnsureSongChangePopupWindow()
    {
        if (_songChangePopupWindow is not null)
            return;

        _songChangePopupWindow = new SongChangePopupWindow();
        WindowHelper.Track(_songChangePopupWindow);
        _songChangePopupWindow.Closed += (_, _) => _songChangePopupWindow = null;
    }

    /// <summary>
    /// Dismisses a popup that is still on screen when the user turns the
    /// feature off, so the setting takes effect immediately rather than after
    /// the current auto-hide delay.
    /// </summary>
    private void OnSongChangePopupEnabledChanged(object? sender, EventArgs e)
    {
        if (!SettingsService.IsSongChangePopupEnabled)
        {
            _songChangePopupWindow?.HidePopup();
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Check for single instance using a named mutex
        // These keep the legacy "Trdo" names on purpose: an old-version instance
        // still running across an update must share the same mutex and event.
        const string mutexName = "Global\\Trdo_SingleInstance_Mutex";
        const string restoreEventName = "Global\\Trdo_RestoreTrayIcon_Event";

        try
        {
            _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running
                // Signal it to restore the tray icon before exiting
                try
                {
                    using EventWaitHandle restoreEvent = EventWaitHandle.OpenExisting(restoreEventName);
                    restoreEvent.Set();
                }
                catch
                {
                    // Event handle doesn't exist or couldn't be opened
                    // This is acceptable - the watchdog timer will eventually restore the icon
                }

                // Exit this instance gracefully
                ShutdownAndExit();
                return;
            }
        }
        catch (Exception)
        {
            // If mutex creation fails, allow the app to continue
            // This could happen in restricted environments
        }

        // Create the event handle for other instances to signal us
        try
        {
            _trayIconRestoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, restoreEventName);
        }
        catch
        {
            // If we can't create the event handle, continue without it
            // The watchdog timer will still provide periodic restoration
        }

        _uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        InitializeTrayIcon();
        await UpdateTrayIconAsync();
        UpdatePlayPauseCommandText(forceTooltip: true);

#if DEBUG
        StartSongChangePopupPreviewIfRequested();
#endif
    }

#if DEBUG
    /// <summary>
    /// Debug-only preview of the song-change popup so its appearance, placement
    /// and animation can be checked without waiting for a live stream to change
    /// tracks. Set TRDO_PREVIEW_SONG_POPUP=1 in the environment before launching.
    /// Re-shows faster than the auto-hide delay so the popup stays on screen.
    /// </summary>
    private void StartSongChangePopupPreviewIfRequested()
    {
        if (Environment.GetEnvironmentVariable("TRDO_PREVIEW_SONG_POPUP") != "1")
            return;

        string[] samples =
        [
            "Fleetwood Mac - Dreams",
            "The Blue Nile - A Walk Across the Rooftops",
            "Khruangbin - August 10",
        ];
        int index = 0;

        _songChangePopupPreviewTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _songChangePopupPreviewTimer.Interval = TimeSpan.FromSeconds(2);
        _songChangePopupPreviewTimer.IsRepeating = true;
        _songChangePopupPreviewTimer.Tick += (_, _) =>
        {
            EnsureSongChangePopupWindow();
            _songChangePopupWindow?.ShowSongChange(samples[index % samples.Length]);
            index++;
        };
        _songChangePopupPreviewTimer.Start();
    }
#endif

    private void PlayerVmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
        {
            bool isPlaying = _playerVm.IsPlaying;

            if (isPlaying && !_wasPlaying)
            {
                // The track playing when a station starts is already audible, so its
                // announcement must not be held back by the metadata-lead delay.
                _stationStartedAtUtc = DateTimeOffset.UtcNow;
                _wasPlaying = true;

                LogService.Info("SongChangePopup", "Playback started; station-start window open");

                // Metadata providers start with the play call, but playback only reports itself
                // as started once the stream has actually opened — well over a second later on a
                // slow connect. The station's current track therefore usually arrives before
                // this point, where it could only establish the baseline. Show it now.
                AnnounceCurrentTrackAtStationStart();
            }

            _wasPlaying = isPlaying;

            UpdatePlayPauseCommandText();
            // Update tray icon to reflect play/pause state
            _ = UpdateTrayIconAsync();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.IsBuffering))
        {
            // Update tray icon to show loading state
            _ = UpdateTrayIconAsync();
        }
        else if (e.PropertyName is nameof(PlayerViewModel.CanPlay) or nameof(PlayerViewModel.IsMuted))
        {
            UpdatePlayPauseCommandText();
        }
        else if (e.PropertyName is (nameof(PlayerViewModel.NowPlaying)) or
                 (nameof(PlayerViewModel.HasNowPlaying)))
        {
            // Update tooltip when now playing info changes
            UpdatePlayPauseCommandText();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.CurrentMetadata))
        {
            HandleSongChangePopup();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.SelectedStation))
        {
            // Reset the baseline so the incoming station's first metadata establishes it
            // rather than announcing immediately. Anything the previous stream was still
            // holding is dropped upstream, by the publish gate.
            _lastKnownNowPlayingDisplayText = null;
            _lastAnnouncedDisplayText = null;
            _stationStartedAtUtc = DateTimeOffset.UtcNow;

            LogService.Info("SongChangePopup",
                $"Station changed to '{_playerVm.SelectedStation?.Name ?? "<none>"}'; " +
                "baseline cleared and station-start window open");
        }
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        // Theme has changed, update the tray icon
        _ = UpdateTrayIconAsync();
    }

    private void InitializeTrayIcon()
    {
        if (_trayIcon is not null)
            return;

        _trayIcon = new(0, "Assets/Radio.ico", "Traydio");
        _trayIcon.Selected += TrayIcon_Selected;
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIconSource(_trayIcon);

        // Only show tutorial window on first run
        if (SettingsService.IsFirstRun)
        {
            TutorialWindow tutorialWindow = new();
            tutorialWindow.Show();
        }
    }

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        if (SettingsService.TrayClickBehavior == 1)
        {
            // Swapped: right click plays/pauses (fall back to flyout if no station selected)
            if (_playerVm.CanPlay)
            {
                TogglePlaybackFromTray();
                return;
            }
        }

        // Default: right click opens flyout; also fallback when no station is available
        ShowFlyout(args);
    }

    private void TrayIcon_Selected(TrayIcon sender, TrayIconEventArgs args)
    {
        if (SettingsService.TrayClickBehavior == 1)
        {
            // Swapped: left click opens flyout
            ShowFlyout(args);
            return;
        }

        // Default: left click plays/pauses
        // Check if we can play (have stations available and one selected)
        if (!_playerVm.CanPlay)
        {
            // No stations available, show the flyout to encourage user to add a station
            ShowFlyout(args);
            return;
        }

        // We have stations, toggle play/pause
        TogglePlaybackFromTray();
    }

    /// <summary>
    /// Toggles playback from a tray click and, when that *starts* playback,
    /// announces what is now playing.
    /// </summary>
    /// <remarks>
    /// This is the only tray path that opens no window, so on its own it leaves
    /// the user with nothing on screen telling them what they just started —
    /// which is exactly the gap the popup fills. Deliberately silent when the
    /// click pauses instead: the pill is headed "Now playing", and saying that
    /// about a stream the user just stopped would be a lie. The previous state
    /// is captured before the toggle because playback starts asynchronously,
    /// so <see cref="PlayerViewModel.IsPlaying"/> has not necessarily flipped
    /// by the time <c>Toggle</c> returns.
    /// </remarks>
    private void TogglePlaybackFromTray()
    {
        bool wasPlaying = _playerVm.IsPlaying;

        _playerVm.Toggle();
        _ = UpdateTrayIconAsync();

        if (!wasPlaying)
            ShowNowPlayingFromTray();
    }

    /// <summary>
    /// Shows the song change popup for whatever is playing right now, on demand
    /// rather than in response to a metadata change.
    /// </summary>
    /// <remarks>
    /// Shows the currently published track, which is the one the listener can
    /// hear, rather than anything the station has announced ahead of it.
    /// Honours the on/off setting, so it stays a single master switch
    /// for "this pill never appears" — unlike the Settings demo button, where
    /// the whole point is to preview the pill before turning it on.
    /// </remarks>
    private void ShowNowPlayingFromTray()
    {
        if (!SettingsService.IsSongChangePopupEnabled)
            return;

        string displayText = _playerVm.NowPlaying.Trim();

        if (displayText.Length == 0)
            return;

        ShowSongChangePopup(displayText);
    }

    public void ShowFlyout(TrayIconEventArgs? args = null)
    {
        // Unlike TryShowFlyout/ShowMiniPlayerWindow (invoked from a button
        // inside the app, where the pointer position is meaningful), this is
        // always a click/tap/pen activation of the tray icon itself. Touch
        // and pen taps don't move the hardware cursor, so capturing it here
        // can anchor the popup to a stale, unrelated position instead of the
        // icon — clear it so placement always derives from the icon's rect.

        WindowPlacementService.ClearPointerAnchor();
        ShowTrayPopup();
    }

    private void ShowTrayPopup()
    {
        if (_trayPopupWindow is null)
        {
            _trayPopupWindow = new TrayPopupWindow();
            WindowHelper.Track(_trayPopupWindow);
            _trayPopupWindow.Closed += (_, _) => _trayPopupWindow = null;
        }

        _trayPopupWindow.ToggleNearAnchor();
    }

    private async Task UpdateTrayIconAsync()
    {
        if (_trayIcon is null)
            return;

        // Detect system theme (true = dark theme, false = light theme)
        bool isDarkTheme = IsSystemInDarkMode();

        // Choose icon based on buffering, theme, and play state
        string iconUri;

        if (_playerVm.IsBuffering)
        {
            // When buffering/loading, use the hourglass icon
            iconUri = "Assets/Hourglass.ico";
        }
        else if (_playerVm.IsPlaying)
        {
            // When playing, use the regular Radio icon
            iconUri = "Assets/Radio.ico";
        }
        else
        {
            // When not playing, use theme-aware icons
            iconUri = isDarkTheme ? "Assets/Radio-White.ico" : "Assets/Radio-Black.ico";
        }

        try
        {
            _trayIcon.SetIcon(iconUri);
        }
        catch
        {
            // If the theme-specific icon doesn't exist, fallback to default Radio.ico
            _trayIcon.SetIcon("Assets/Radio.ico");
        }

        // SetIcon can clear the native tooltip even when the text is unchanged.
        UpdatePlayPauseCommandText(forceTooltip: true);

        await Task.CompletedTask;
    }

    private static bool IsSystemInDarkMode()
    {
        try
        {
            // Read the system (taskbar) theme, not the app theme.
            // SystemUsesLightTheme = 0 means dark taskbar, 1 means light taskbar.
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("SystemUsesLightTheme");
            if (value is int intVal)
                return intVal == 0;

            return true;
        }
        catch
        {
            // Default to dark theme if detection fails
            return true;
        }
    }

    private void UpdatePlayPauseCommandText(bool forceTooltip = false)
    {
        if (_trayIcon is null)
            return;

        string station = _playerVm.SelectedStation?.Name ?? string.Empty;
        station = station.Split(" ", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        string tooltip;
        if (!_playerVm.CanPlay)
        {
            tooltip = LocalizationService.GetString(
                "TrayIcon_AddStation",
                "Traydio - Add a station to start listening");
        }
        else if (_playerVm.IsPlaying)
        {
            string playPauseClickHint = SettingsService.TrayClickBehavior == 1
                ? LocalizationService.GetString("TrayIcon_RightClickToPause", "Right-click to pause")
                : LocalizationService.GetString("TrayIcon_LeftClickToPause", "Left-click to pause");

            if (_playerVm.HasNowPlaying)
            {
                string nowPlaying = _playerVm.NowPlaying;
                if (nowPlaying.Length > MaxTooltipNowPlayingLength)
                {
                    nowPlaying = string.Concat(nowPlaying.AsSpan(0, MaxTooltipNowPlayingLength - 3), "...");
                }

                string playingFormat = LocalizationService.GetString(
                    "TrayIcon_PlayingWithNowPlaying",
                    "Traydio {0} (Playing)\n{1}\n{2}");
                tooltip = string.Format(playingFormat, station, nowPlaying, playPauseClickHint);
            }
            else
            {
                string playingFormat = LocalizationService.GetString(
                    "TrayIcon_Playing",
                    "Traydio {0} (Playing)\n{1}");
                tooltip = string.Format(playingFormat, station, playPauseClickHint);
            }
        }
        else
        {
            string playPauseClickHint = SettingsService.TrayClickBehavior == 1
                ? LocalizationService.GetString("TrayIcon_RightClickToPlay", "Right-click to play")
                : LocalizationService.GetString("TrayIcon_LeftClickToPlay", "Left-click to play");

            string pausedFormat = LocalizationService.GetString(
                "TrayIcon_Paused",
                "Traydio {0} (Paused)\n{1}");
            tooltip = string.Format(pausedFormat, station, playPauseClickHint);
        }

        if (_playerVm.CanPlay && _playerVm.IsMuted)
        {
            // Mute leaves no other trace on the icon, and silence with "(Playing)" above it
            // would otherwise read as a broken stream.
            tooltip = string.Concat(tooltip.TrimEnd(), "\n", LocalizationService.GetString("TrayIcon_Muted", "Muted"));
        }

        SetTrayTooltip(tooltip, forceTooltip);
    }

    private void SetTrayTooltip(string? text, bool force = false)
    {
        if (_trayIcon is null)
            return;

        string tooltip = string.IsNullOrWhiteSpace(text) ? "Traydio" : text.Trim();
        if (tooltip.Length > MaxTrayTooltipLength)
        {
            tooltip = string.Concat(tooltip.AsSpan(0, MaxTrayTooltipLength - 3), "...");
        }

        void Apply()
        {
            if (force)
            {
                // WinUIEx only sends a native tooltip update when the value changes.
                _trayIcon.Tooltip = "\u200B";
            }

            _trayIcon.Tooltip = tooltip;
        }

        if (_uiDispatcherQueue is not null && !_uiDispatcherQueue.HasThreadAccess)
        {
            _uiDispatcherQueue.TryEnqueue(Apply);
            return;
        }

        Apply();
    }

    private async void OnTaskbarCreated(object? sender, EventArgs e)
    {
        await EnsureTrayIconVisibleAsync();
    }

    private async Task EnsureTrayIconVisibleAsync()
    {
        try
        {
            if (_trayIcon is null)
            {
                InitializeTrayIcon();
            }
            else
            {
                _trayPopupWindow?.HidePopup();
                _trayIcon.IsVisible = false;
                _trayIcon.IsVisible = true;
            }

            await UpdateTrayIconAsync();
            UpdatePlayPauseCommandText(forceTooltip: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Failed to recreate tray icon: {ex}");
        }
    }

    private bool _isShuttingDown;

    /// <summary>
    /// Tears down every long-lived service and native resource this app owns, then exits.
    /// </summary>
    /// <remarks>
    /// This is the app's only real exit path (the Quit button and the duplicate-instance
    /// early-out both route here) and must be called instead of <see cref="Application.Exit"/>
    /// directly. A finalizer used to do this cleanup, but it never actually ran: <c>Exit()</c>
    /// terminates the process without running finalizers, and <c>Application.Current</c> stays
    /// GC-reachable for the app's whole life anyway, so it was never eligible for finalization
    /// in the first place. That silently meant the radio player - its MediaPlayer, the LibVLC
    /// engine, the watchdog's background monitor - was never disposed on quit.
    /// </remarks>
    internal void ShutdownAndExit()
    {
        if (_isShuttingDown)
            return;
        _isShuttingDown = true;

        Debug.WriteLine("[App] Shutting down");

        try
        {
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
            SettingsService.SongChangePopupEnabledChanged -= OnSongChangePopupEnabledChanged;

            _trayPopupWindow?.Close();
            _miniPlayerWindow?.Close();
            _songChangePopupWindow?.Close();

            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            // Order matters twice over: RadioStaticService unsubscribes from the player's events
            // first, so nothing reacts to the source teardown below by starting static that would
            // never fade out; then the player's Dispose() tears down the LibVlcPlaybackBackend
            // (which owns a VlcMediaPlayer built on the shared native instance) before LibVlcHost
            // frees that instance, since freeing it first would pull the native library out from
            // under a still-live player.
            RadioStaticService.Instance.Dispose();
            RadioPlayerService.Instance.Dispose();
            LibVlcHost.Dispose();

            // Only ever owned when this instance created it (createdNew was true in
            // OnLaunched) - the duplicate-instance path opens someone else's mutex and must not
            // release it, but ReleaseMutex() on an unowned mutex just throws, which the catch
            // below swallows.
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _trayIconRestoreEvent?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Error during shutdown: {ex.Message}");
        }

        Exit();
    }
}
