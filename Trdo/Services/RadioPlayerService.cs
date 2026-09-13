using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Helpers;
using Trdo.Models;
using Trdo.Services.Audio;
using Trdo.Services.Playback;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Trdo.Services;

public sealed partial class RadioPlayerService : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly DispatcherQueue? _uiQueue;
    private readonly StreamWatchdogService _watchdog;
    private readonly SystemMediaTransportControls? _systemMediaControls;
    private readonly HttpClient _httpClient;
    private double _volume = 1.0;
    private const string VolumeKey = "RadioVolume";
    private const string WatchdogEnabledKey = "WatchdogEnabled";
    private string? _streamUrl;
    private string? _currentStationName;
    private string? _currentStationFaviconUrl;
    private string? _currentAlbumArtUrl;
    private bool _isInternalStateChange;
    private bool _wasExternalPause;
    private Timer? _smtcUpdateTimer;
    private bool _smtcUpdatePending;
    private readonly Lock _smtcUpdateLock = new();
    private readonly SemaphoreSlim _volumeFadeLock = new(1, 1);
    private Timer? _internalStateChangeTimer;
    private readonly DateTime _lastExternalPauseRecovery = DateTime.MinValue;
    private bool _hasPlayedOnce;
    private bool _isManuallyBuffering;
    private bool _isVolumeFading;
    private double _activeBackendVolume = 1.0;
    private bool _isStationCyclingEnabled;
    private readonly WhiteNoisePlaybackEngine _whiteNoiseEngine = new();
    private WhiteNoiseColor _whiteNoiseColor = WhiteNoiseColor.White;
    private IReadOnlyList<string> _localTrackList = [];
    private int _localTrackIndex = -1;
    private int _localTrackCompletionGate;

    // What the currently prepared source actually is. Stays Radio - the default - until a
    // transition to a different kind actually lands, which is what keeps IsPlaying/IsBuffering
    // and Play()/Pause() reading the OUTGOING source's kind for as long as it is still the one
    // on screen; see the ordering comment in TransitionToStationAsync.
    private AudioSourceKind _activeSourceKind = AudioSourceKind.Radio;
    private CancellationTokenSource? _playAttemptCts;
    private int _consecutivePlaybackFailures;
    private bool _hasReportedPlaybackFailure;
    private const int MaxConsecutivePlaybackFailures = 3;
    private static readonly TimeSpan MinPlaybackConfirmationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FadeStepInterval = TimeSpan.FromMilliseconds(20);

    public static RadioPlayerService Instance { get; } = new();

    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<bool>? BufferingStateChanged;
    public event EventHandler<StreamMetadata>? StreamMetadataChanged;
    public event EventHandler? LocalTrackChanged;
    public event EventHandler? LocalTrackListChanged;
    public event EventHandler? NextStationRequested;
    public event EventHandler? PreviousStationRequested;

    /// <summary>
    /// Raised when a play attempt cannot proceed or fails. The string payload is a
    /// user-facing message describing the failure (e.g. no network connection).
    /// </summary>
    public event EventHandler<string>? PlaybackFailed;

    internal static string NoNetworkMessage =>
        LocalizationService.GetString(
            "PlaybackFailure_NoNetwork",
            "No internet connection. Connect to a network and try again.");

    public bool IsPlaying
    {
        get
        {
            switch (_activeSourceKind)
            {
                case AudioSourceKind.WhiteNoise:
                    return _whiteNoiseEngine.IsPlaying;

                // Files isn't implemented yet - nothing can create a station of that kind, so it
                // shares Radio's backend-based check rather than needing its own branch. Getters
                // like this one are on hot, frequently-polled paths (bindings, the watchdog), so
                // an unimplemented kind falls back to today's behaviour instead of throwing.
                case AudioSourceKind.Radio:
                case AudioSourceKind.Files:
                default:
                    bool isPlaying = ActiveBackend.IsPlaying;
                    Debug.WriteLine($"[RadioPlayerService] IsPlaying getter: {isPlaying}, Backend: {ActivePlaybackBackend}");
                    return isPlaying;
            }
        }
    }

    public bool IsBuffering
    {
        get
        {
            switch (_activeSourceKind)
            {
                case AudioSourceKind.WhiteNoise:
                    // Generated locally - there is nothing to wait on.
                    return false;

                case AudioSourceKind.Radio:
                case AudioSourceKind.Files:
                default:
                    try
                    {
                        if (ActivePlaybackBackend == PlaybackBackendKind.LibVlc)
                        {
                            return ActiveBackend.IsBuffering || _isManuallyBuffering;
                        }

                        MediaPlaybackState state = _player.PlaybackSession.PlaybackState;
                        bool isPlayerBuffering = state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
                        bool isBuffering = isPlayerBuffering || _isManuallyBuffering;
                        Debug.WriteLine($"[RadioPlayerService] IsBuffering getter: {isBuffering} (Player: {isPlayerBuffering}, Manual: {_isManuallyBuffering}), PlaybackState: {state}");
                        return isBuffering;
                    }
                    catch
                    {
                        return _isManuallyBuffering;
                    }
            }
        }
    }

    public string? StreamUrl
    {
        get
        {
            Debug.WriteLine($"[RadioPlayerService] StreamUrl getter: {_streamUrl}");
            return _streamUrl;
        }
    }

    /// <summary>
    /// Gets the buffering progress as a value between 0 and 1.
    /// For live streams, this can help detect if the stream is actually delivering data.
    /// </summary>
    public double BufferingProgress
    {
        get
        {
            try
            {
                return ActiveBackend.BufferingProgress;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Gets the current playback position.
    /// For live streams, this can help detect if audio is actually progressing.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            try
            {
                return ActiveBackend.Position;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// The current item's total duration, or <c>null</c> for a live radio stream (or nothing
    /// prepared yet). Meaningful only for <see cref="AudioSourceKind.Files"/>.
    /// </summary>
    public TimeSpan? Duration
    {
        get
        {
            try
            {
                return ActiveBackend.Duration;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Seeks the active backend to <paramref name="position"/>. Meaningful only for <see cref="AudioSourceKind.Files"/>.</summary>
    public void Seek(TimeSpan position)
    {
        try
        {
            ActiveBackend.Seek(position);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Seek failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the total buffered duration using MediaPlaybackSession.GetBufferedRanges.
    /// Returns the sum of all buffered time ranges.
    /// </summary>
    public TimeSpan TotalBufferedDuration
    {
        get
        {
            try
            {
                IReadOnlyList<MediaTimeRange> bufferedRanges = ActiveBackend.GetBufferedRanges();
                TimeSpan totalBuffered = TimeSpan.Zero;
                foreach (MediaTimeRange range in bufferedRanges)
                {
                    totalBuffered += range.End - range.Start;
                }
                return totalBuffered;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Gets the buffered ranges from the playback session.
    /// Each MediaTimeRange contains Start and End times representing buffered content.
    /// </summary>
    public IReadOnlyList<MediaTimeRange> GetBufferedRanges()
    {
        try
        {
            return ActiveBackend.GetBufferedRanges();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Gets the minimum buffer duration required based on current buffer level setting.
    /// </summary>
    public TimeSpan RequiredBufferDuration => TimeSpan.FromMilliseconds(_watchdog.BufferDelayMs);

    /// <summary>
    /// Checks if sufficient buffer is available for smooth playback based on current buffer level.
    /// </summary>
    public bool HasSufficientBuffer
    {
        get
        {
            TimeSpan required = RequiredBufferDuration;
            if (required == TimeSpan.Zero)
            {
                // Default level - no minimum buffer required
                return true;
            }
            return TotalBufferedDuration >= required;
        }
    }

    public StreamWatchdogService Watchdog => _watchdog;

    /// <summary>
    /// Gets the current stream metadata (now playing information) as published to the app -
    /// which during a track-info delay is still the previous track, because that is the one
    /// the listener can hear. The orchestrator's own value runs ahead of the audio and is
    /// deliberately not exposed.
    /// </summary>
    public StreamMetadata CurrentMetadata => _publishGate.Current;

    /// <summary>
    /// How long a mid-stream track change is held back before the app shows it, in seconds.
    /// Set by <see cref="ViewModels.PlayerViewModel"/>, which is the only thing that knows both
    /// the app setting and the selected station's override.
    /// </summary>
    public double TrackInfoDelaySeconds
    {
        get => _publishGate.DelaySeconds;
        set => _publishGate.DelaySeconds = value;
    }

    /// <summary>
    /// Drops a track that is still being held and treats the next one as a fresh start.
    /// </summary>
    /// <remarks>
    /// The single place anything says "whatever was waiting to be shown no longer applies".
    /// Every route out of audio funnels here - pausing, a hardware button, a backend
    /// reporting Stopped or EndReached, a stream failing, and the user picking a different
    /// station, which resets eagerly because its transition runs asynchronously and would
    /// otherwise leave a window for the outgoing stream's track to surface. Null-tolerant
    /// because playback state can be reported before the engine has finished initialising.
    /// </remarks>
    public void ResetTrackInfoHold() => _publishGate?.Reset();

    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 2);
            if (Math.Abs(_volume - value) < 0.0001) return;
            Debug.WriteLine($"[RadioPlayerService] Setting Volume from {_volume} to {value}");
            _volume = value;
            if (!_isVolumeFading)
            {
                SyncActiveBackendVolume();
                _whiteNoiseEngine.SetVolume(_volume);
            }
            try
            {
                ApplicationData.Current.LocalSettings.Values[VolumeKey] = _volume;
            }
            catch { }
            VolumeChanged?.Invoke(this, _volume);
        }
    }

    public bool WatchdogEnabled
    {
        get => _watchdog.IsEnabled;
        set
        {
            Debug.WriteLine($"[RadioPlayerService] Setting WatchdogEnabled to {value}");
            _watchdog.IsEnabled = value;
            try
            {
                ApplicationData.Current.LocalSettings.Values[WatchdogEnabledKey] = value;
            }
            catch { }
        }
    }

    public void SetStationCyclingEnabled(bool isEnabled)
    {
        if (_isStationCyclingEnabled == isEnabled)
        {
            return;
        }

        _isStationCyclingEnabled = isEnabled;
        Debug.WriteLine($"[RadioPlayerService] Station cycling enabled: {_isStationCyclingEnabled}");
        ScheduleSystemMediaTransportControlsUpdate();
    }

    private RadioPlayerService()
    {
        Debug.WriteLine("=== RadioPlayerService Constructor START ===");

        _uiQueue = DispatcherQueue.GetForCurrentThread();
        Debug.WriteLine($"[RadioPlayerService] DispatcherQueue obtained: {_uiQueue != null}");

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        Debug.WriteLine("[RadioPlayerService] HttpClient created for album art downloads");

        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = false,
            IsLoopingEnabled = false,
            // Windows MediaPlayer only supports 0.0-1.0; higher amplification is
            // applied by the LibVLC backend. Clamp so the native player never throws.
            Volume = Math.Clamp(_volume, 0, 1)
        };
        Debug.WriteLine($"[RadioPlayerService] MediaPlayer created with Volume={_volume}, AutoPlay=false");

        _player.PlaybackSession.PlaybackStateChanged += (_, _) =>
        {
            bool isPlaying;
            bool isBuffering;
            MediaPlaybackState currentState;
            try
            {
                currentState = _player.PlaybackSession.PlaybackState;
                isPlaying = currentState == MediaPlaybackState.Playing;
                isBuffering = currentState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
                LogService.Info("RadioPlayerService", $"Native state -> {currentState} (isPlaying={isPlaying}, isBuffering={isBuffering})");
                Debug.WriteLine($"[RadioPlayerService] PlaybackStateChanged event: IsPlaying={isPlaying}, IsBuffering={isBuffering}, State={currentState}, IsInternalChange={_isInternalStateChange}");

                // Reaching Playing means the current attempt succeeded - reset failure tracking.
                if (isPlaying)
                {
                    ResetPlaybackFailureTracking();
                    ConfirmActiveBackendHealthy();
                }

                // If state change was not initiated internally (e.g., from hardware buttons),
                // notify the watchdog of user intention
                if (!_isInternalStateChange)
                {
                    Debug.WriteLine("[RadioPlayerService] External state change detected (likely hardware button)");
                    if (currentState == MediaPlaybackState.Playing)
                    {
                        _watchdog?.NotifyUserIntentionToPlay();
                        Debug.WriteLine("[RadioPlayerService] Notified watchdog of user intention to play (hardware button)");

                        // Mark that an external play was triggered
                        // The Play() method will handle MediaSource recreation if needed
                        if (_wasExternalPause)
                        {
                            Debug.WriteLine("[RadioPlayerService] External play detected after external pause - will be handled by Play() method");
                        }
                    }
                    else if (currentState == MediaPlaybackState.Paused)
                    {
                        if (_isManuallyBuffering)
                        {
                            Debug.WriteLine("[RadioPlayerService] Ignoring pause during manual buffering");
                        }
                        else
                        {
                            // Only notify pause intent if explicitly paused (not buffering, opening, or other states)
                            _watchdog?.NotifyUserIntentionToPause();
                            Debug.WriteLine("[RadioPlayerService] Notified watchdog of user intention to pause (hardware button)");

                            // Mark that this was an external pause
                            _wasExternalPause = true;
                            Debug.WriteLine("[RadioPlayerService] Marked as external pause - will refresh stream on next play");

                            // An external pause is the user asking for silence just as much as
                            // Pause() is, so drop any in-flight play attempt with it. Otherwise
                            // it stays live as evidence of intent and a later recovery restarts
                            // audio the user had already stopped.
                            CancelPendingPlayAttempt();

                            // Stop metadata polling when paused
                            StopMetadata();
                            Debug.WriteLine("[RadioPlayerService] Stopped metadata after external pause");
                        }
                    }
                    // For other states (Buffering, Opening, None), don't change watchdog intent
                    // This allows the watchdog to recover if a stream stops unexpectedly
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioPlayerService] EXCEPTION in PlaybackStateChanged: {ex.Message}");
                return;
            }
            // Covers the states Pause() does not run through - a stream that ends or is
            // stopped outright. Buffering and Opening are excluded: a stream stuttering
            // mid-track is still playing that track, and dropping the held info there would
            // mean the track never appeared at all.
            if (!isPlaying && !isBuffering)
            {
                ResetTrackInfoHold();
            }

            TryEnqueueOnUi(() =>
            {
                PlaybackStateChanged?.Invoke(this, isPlaying);
                BufferingStateChanged?.Invoke(this, isBuffering);
                ScheduleSystemMediaTransportControlsUpdate();
            });
        };

        _watchdog = new StreamWatchdogService(this);
        Debug.WriteLine("[RadioPlayerService] StreamWatchdogService created");

        // Keep the PC awake while playing (unless the user allows sleep).
        PlaybackStateChanged += (_, isPlaying) => PowerManagementService.SetPlaybackActive(isPlaying);

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Failed to subscribe to PowerModeChanged: {ex.Message}");
        }

        InitializePlaybackEngine();

        // Initialize SystemMediaTransportControls
        try
        {
            // Manual SMTC control: without this the auto command manager ignores
            // the button configuration whenever the MediaPlayer has no source,
            // which is always the case when LibVLC is the active backend.
            _player.CommandManager.IsEnabled = false;

            _systemMediaControls = _player.SystemMediaTransportControls;
            if (_systemMediaControls != null)
            {
                _systemMediaControls.IsEnabled = true;
                _systemMediaControls.IsPlayEnabled = true;
                _systemMediaControls.IsPauseEnabled = true;
                _systemMediaControls.IsStopEnabled = false;
                _systemMediaControls.IsNextEnabled = false;
                _systemMediaControls.IsPreviousEnabled = false;

                _systemMediaControls.ButtonPressed += OnSystemMediaButtonPressed;
                Debug.WriteLine("[RadioPlayerService] SystemMediaTransportControls initialized");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Failed to initialize SystemMediaTransportControls: {ex.Message}");
        }

        LoadSettings();

        Debug.WriteLine("=== RadioPlayerService Constructor END ===");
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Suspend || !IsPlaying)
            return;

        // A live stream position is meaningless after resume; mark it so the
        // next play (user or watchdog recovery) recreates the source at live.
        Debug.WriteLine("[RadioPlayerService] System suspending during playback - marking stream for recreation");
        _wasExternalPause = true;
    }

    private void LoadSettings()
    {
        Debug.WriteLine("[RadioPlayerService] LoadSettings START");
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(VolumeKey, out object? v))
            {
                double parsed = v switch
                {
                    double d => d,
                    string s when double.TryParse(s, out double d2) => d2,
                    _ => _volume
                };
                _volume = Math.Clamp(parsed, 0, 2);
                SyncActiveBackendVolume();
                Debug.WriteLine($"[RadioPlayerService] Loaded volume from settings: {_volume}");
            }

            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(WatchdogEnabledKey, out object? w))
            {
                bool watchdogEnabled = w switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out bool b2) => b2,
                    _ => true
                };
                _watchdog.IsEnabled = watchdogEnabled;
                Debug.WriteLine($"[RadioPlayerService] Loaded watchdog enabled from settings: {watchdogEnabled}");
            }
            else
            {
                _watchdog.IsEnabled = true;
                Debug.WriteLine("[RadioPlayerService] No saved watchdog setting, defaulting to enabled");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] EXCEPTION in LoadSettings: {ex.Message}");
        }
        Debug.WriteLine("[RadioPlayerService] LoadSettings END");
    }

    /// <summary>
    /// Initialize the player with a stream URL (first-time setup only)
    /// </summary>
    public void Initialize(string streamUrl)
    {
        Debug.WriteLine($"=== Initialize START ===");
        Debug.WriteLine($"[RadioPlayerService] Initialize called with URL: {streamUrl}");
        Debug.WriteLine($"[RadioPlayerService] Current _streamUrl: {_streamUrl}");

        if (!string.IsNullOrWhiteSpace(_streamUrl))
        {
            // Already initialized, use SetStreamUrl instead
            Debug.WriteLine("[RadioPlayerService] Already initialized, skipping Initialize");
            Debug.WriteLine($"=== Initialize END (already initialized) ===");
            return;
        }

        Debug.WriteLine("[RadioPlayerService] Calling SetStreamUrl...");

        // Mark this as internal so we don't trigger external pause detection
        SetInternalStateChange(true);
        SetStreamUrl(streamUrl);

        // Allow time for the MediaSource to fully initialize before first play
        // This prevents the initial play from causing cascading state changes
        Debug.WriteLine("[RadioPlayerService] MediaSource created and ready for playback");

        Debug.WriteLine($"=== Initialize END ===");
    }

    /// <summary>
    /// Set or change the stream URL. This will prepare the stream for playback.
    /// </summary>
    public void SetStreamUrl(string streamUrl)
    {
        Debug.WriteLine($"=== SetStreamUrl START ===");
        Debug.WriteLine($"[RadioPlayerService] SetStreamUrl called with: {streamUrl}");
        Debug.WriteLine($"[RadioPlayerService] Previous URL: {_streamUrl}");

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            Debug.WriteLine("[RadioPlayerService] ERROR: Stream URL is empty");
            throw new ArgumentException("Stream URL cannot be empty", nameof(streamUrl));
        }

        Uri uri = new(streamUrl); // Will throw if invalid URL
        Debug.WriteLine($"[RadioPlayerService] URI created successfully: {uri}");

        // If changing stations, reset the first play flag
        if (_streamUrl != streamUrl)
        {
            _hasPlayedOnce = false;
            ResetPlaybackFailureTracking();

            // A new stream must not inherit the previous one's recovery escalation or
            // auto-buffer bump - one bad station shouldn't degrade every station after it.
            _watchdog.ResetForStation();
            Debug.WriteLine("[RadioPlayerService] Station changed - reset first play flag");
        }

        // Update the stream URL
        _streamUrl = streamUrl;
        Debug.WriteLine($"[RadioPlayerService] _streamUrl updated to: {_streamUrl}");

        // Configure player for live streaming
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.RealTimePlayback = true;
        Debug.WriteLine("[RadioPlayerService] Player configured for live streaming");

        ClearActiveBackendSource();
        StopMetadata();

        // Update SMTC with new station
        ScheduleSystemMediaTransportControlsUpdate();

        Debug.WriteLine($"=== SetStreamUrl END ===");
    }

    /// <summary>
    /// Set the current station name for display in system media controls.
    /// </summary>
    public void SetStationName(string stationName)
    {
        Debug.WriteLine($"[RadioPlayerService] Setting station name to: {stationName}");
        _currentStationName = stationName;
        ScheduleSystemMediaTransportControlsUpdate();
    }

    /// <summary>
    /// Set the current station favicon URL for display in system media controls.
    /// </summary>
    public void SetStationFavicon(string? faviconUrl)
    {
        Debug.WriteLine($"[RadioPlayerService] Setting station favicon to: {faviconUrl}");
        _currentStationFaviconUrl = faviconUrl;
        ScheduleSystemMediaTransportControlsUpdate();
    }

    /// <summary>
    /// Sets which noise spectrum a white noise station plays. Safe to call any time - it only
    /// affects what <see cref="Play"/> picks up the next time it runs under
    /// <see cref="AudioSourceKind.WhiteNoise"/>, not what is currently active.
    /// </summary>
    public void SetWhiteNoiseColor(WhiteNoiseColor color)
    {
        _whiteNoiseColor = color;
        _whiteNoiseEngine.SetColor(color);
    }

    /// <summary>
    /// Sets which kind of source is active outside of a transition - the app-startup path,
    /// which calls <see cref="Initialize"/> and <see cref="Play"/> directly rather than going
    /// through <see cref="TransitionToStationAsync"/>. Must be called before either of those,
    /// since there is no outgoing source at startup for the ordering in
    /// <see cref="TransitionToStationAsync"/> to protect.
    /// </summary>
    public void SetActiveSourceKind(AudioSourceKind kind)
    {
        _activeSourceKind = kind;
    }

    /// <summary>
    /// What kind of source is currently active. <see cref="StreamWatchdogService"/> reads this
    /// to skip its network/buffer-shaped recovery entirely for anything that isn't
    /// <see cref="AudioSourceKind.Radio"/> - there is no stream to stall or reconnect.
    /// </summary>
    public AudioSourceKind ActiveSourceKind => _activeSourceKind;

    public async Task TransitionToStationAsync(
        string streamUrl,
        string stationName,
        string? faviconUrl,
        double volume,
        bool playAfterSwitch,
        AudioSourceKind sourceKind = AudioSourceKind.Radio,
        WhiteNoiseColor whiteNoiseColor = WhiteNoiseColor.White,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // IsPlaying/IsBuffering and the Pause() inside FadeOutAndPauseAsync must still see
            // the OUTGOING source's kind here - switching to the new one first would make a
            // playing radio stream read as "not playing" the moment a transition to white noise
            // starts (IsPlaying would check the noise engine instead), so it would never be
            // faded out or torn down.
            if (IsPlaying || IsBuffering)
            {
                await FadeOutAndPauseAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Only now does the new source become "active" - everything above this line still
            // reasoned about the old one, everything below reasons about the new one.
            _activeSourceKind = sourceKind;
            _whiteNoiseColor = whiteNoiseColor;
            _whiteNoiseEngine.SetColor(whiteNoiseColor);

            SetStreamUrl(streamUrl);
            Volume = volume;
            SetStationName(stationName);
            SetStationFavicon(faviconUrl);

            if (playAfterSwitch)
            {
                Play();
            }
        }
        catch (OperationCanceledException)
        {
            SyncActiveBackendVolume();
            throw;
        }
    }

    public async Task FadeOutAndPauseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CancelPendingPlayAttempt();

            if (ActiveBackend.IsPlaying)
            {
                await FadeActiveBackendVolumeAsync(
                    targetVolume: 0,
                    FadeOutDuration,
                    followUserVolume: false,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Pause();
        }
        catch (OperationCanceledException)
        {
            SyncActiveBackendVolume();
            throw;
        }
    }

    /// <summary>The folder's tracks currently loaded for a <see cref="AudioSourceKind.Files"/> station.</summary>
    public IReadOnlyList<string> CurrentLocalTrackList => _localTrackList;

    /// <summary>Index into <see cref="CurrentLocalTrackList"/> of the track currently playing.</summary>
    public int CurrentLocalTrackIndex => _localTrackIndex;

    public bool CanGoToNextLocalTrack =>
        _activeSourceKind == AudioSourceKind.Files && _localTrackIndex + 1 < _localTrackList.Count;

    public bool CanGoToPreviousLocalTrack =>
        _activeSourceKind == AudioSourceKind.Files && _localTrackIndex > 0;

    /// <summary>
    /// Scans <see cref="RadioStation.LocalFolderPath"/> fresh and starts playing its first
    /// track. The folder is rescanned live rather than trusting a cached list, so files added,
    /// removed, or renamed since the station was last played are reflected immediately.
    /// </summary>
    public async Task PlayLocalMusicStationAsync(
        RadioStation station,
        bool playAfterSwitch,
        CancellationToken cancellationToken = default)
    {
        _localTrackList = LocalMusicFolderScanner.ScanTracks(station.LocalFolderPath);
        LogService.Info("RadioPlayerService", $"Local track list (re)scanned: {_localTrackList.Count} track(s) in {LogService.Redact(station.LocalFolderPath)}");
        TryEnqueueOnUi(() => LocalTrackListChanged?.Invoke(this, EventArgs.Empty));
        SetLocalTrackIndex(0);

        if (_localTrackList.Count == 0)
        {
            LogService.Warn("RadioPlayerService",
                $"Local music folder has no playable tracks: {LogService.Redact(station.LocalFolderPath)}");
            ReportPlaybackFailure("This folder has no playable audio files.");
            return;
        }

        await TransitionToStationAsync(
            ToFileUri(_localTrackList[_localTrackIndex]),
            station.Name,
            station.FaviconUrl,
            station.Volume,
            playAfterSwitch,
            sourceKind: AudioSourceKind.Files,
            cancellationToken: cancellationToken);
    }

    /// <summary>Jumps to an arbitrary track in the folder currently loaded, e.g. from the details page's track list.</summary>
    public async Task<bool> PlayLocalTrackAtIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (_activeSourceKind != AudioSourceKind.Files || index < 0 || index >= _localTrackList.Count)
        {
            return false;
        }

        SetLocalTrackIndex(index);

        await TransitionToStationAsync(
            ToFileUri(_localTrackList[_localTrackIndex]),
            _currentStationName ?? string.Empty,
            _currentStationFaviconUrl,
            _volume,
            playAfterSwitch: true,
            sourceKind: AudioSourceKind.Files,
            cancellationToken: cancellationToken);

        return true;
    }

    private void SetLocalTrackIndex(int index)
    {
        if (_localTrackIndex == index)
        {
            return;
        }

        LogService.Info("RadioPlayerService", $"Local track index changed: {_localTrackIndex} -> {index}");
        _localTrackIndex = index;
        TryEnqueueOnUi(() => LocalTrackChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Advances to the next track in the folder, if there is one. No wraparound.</summary>
    public Task<bool> NextLocalTrackAsync(CancellationToken cancellationToken = default) =>
        PlayLocalTrackAtIndexAsync(_localTrackIndex + 1, cancellationToken);

    /// <summary>Returns to the previous track in the folder, if there is one. No wraparound.</summary>
    public Task<bool> PreviousLocalTrackAsync(CancellationToken cancellationToken = default) =>
        PlayLocalTrackAtIndexAsync(_localTrackIndex - 1, cancellationToken);

    private static string ToFileUri(string path) => new Uri(path).AbsoluteUri;

    public void ClearPlaybackTarget()
    {
        Debug.WriteLine("=== ClearPlaybackTarget START ===");

        if (!string.IsNullOrWhiteSpace(_streamUrl))
        {
            Pause();
        }
        else
        {
            SetManualBuffering(false);
            StopMetadata();
        }

        ClearActiveBackendSource();
        _streamUrl = null;
        _currentStationName = null;
        _currentStationFaviconUrl = null;
        _currentAlbumArtUrl = null;
        _hasPlayedOnce = false;
        _wasExternalPause = false;
        _watchdog.ResetForStation();

        ScheduleSystemMediaTransportControlsUpdate();
        Debug.WriteLine("=== ClearPlaybackTarget END ===");
    }

    /// <summary>
    /// Start playback of the current stream.
    /// Always applies buffer settings based on current buffer level using GetBufferedRanges.
    /// </summary>
    public void Play()
    {
        Debug.WriteLine($"=== Play START ===");
        Debug.WriteLine($"[RadioPlayerService] Play called");
        Debug.WriteLine($"[RadioPlayerService] Current stream URL: {_streamUrl}");
        Debug.WriteLine($"[RadioPlayerService] Current IsPlaying: {_player.PlaybackSession.PlaybackState}");
        Debug.WriteLine($"[RadioPlayerService] Player.Source is null: {_player.Source == null}");
        Debug.WriteLine($"[RadioPlayerService] Has played once: {_hasPlayedOnce}");
        Debug.WriteLine($"[RadioPlayerService] Was external pause: {_wasExternalPause}");
        Debug.WriteLine($"[RadioPlayerService] Required buffer: {RequiredBufferDuration.TotalMilliseconds}ms");

        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            // Nothing to play yet - reachable when a hardware/SMTC play command arrives before
            // any station has ever been selected (the app can be running with a tray icon and
            // no window open yet). Pause() already treats this as a harmless no-op rather than
            // an error; Play() used to throw here instead, which surfaced a raw, meaningless
            // "Call SetStreamUrl first" message to the user with nothing they could do about it.
            Debug.WriteLine("[RadioPlayerService] Play called with no stream URL set - nothing to play yet");
            Debug.WriteLine($"=== Play END (no URL) ===");
            return;
        }

        LogService.Info("RadioPlayerService",
            $"Play requested for {LogService.Redact(_streamUrl)} (hasPlayedOnce={_hasPlayedOnce}, wasExternalPause={_wasExternalPause})");

        // Fresh user-initiated attempt: allow failures to be reported and retried again,
        // and give the recovery ladder a clean slate so a previous give-up doesn't stop
        // the watchdog from protecting this attempt.
        ResetPlaybackFailureTracking();
        _watchdog.ResetForStation();

        switch (_activeSourceKind)
        {
            case AudioSourceKind.WhiteNoise:
                PlayWhiteNoise();
                Debug.WriteLine($"=== Play END (white noise) ===");
                return;

            // Files shares Radio's streaming path below: by the time Play() runs, _streamUrl
            // is always the current track's real file URI (set by PlayLocalMusicStationAsync/
            // PlayLocalTrackAtIndexAsync via TransitionToStationAsync), so the same
            // IPlaybackBackend.PrepareAsync pipeline that opens a radio stream opens a local
            // file just as well.
            case AudioSourceKind.Radio:
            case AudioSourceKind.Files:
            default:
                break;
        }

        // Don't attempt to open a stream when the machine is offline - it would just
        // spin through prepare/fallback and fail. Tell the user instead.
        if (_activeSourceKind is AudioSourceKind.Radio && !NetworkStatusService.IsInternetAvailable())
        {
            LogService.Warn("RadioPlayerService", "No internet connection; aborting play attempt");
            Debug.WriteLine("[RadioPlayerService] No network available, aborting play attempt");
            ReportPlaybackFailure();
            Debug.WriteLine($"=== Play END (no network) ===");
            return;
        }

        // Cancel any in-flight play attempt (e.g. still buffering) before starting a new one
        CancelPendingPlayAttempt();
        _playAttemptCts = new CancellationTokenSource();

        // Always use buffer-aware playback to apply buffer settings
        // Fire and forget - PlayWithBufferAsync handles everything including buffering
        _ = PlayWithBufferInternalAsync(_playAttemptCts.Token);

        Debug.WriteLine($"=== Play END ===");
    }

    /// <summary>
    /// Starts a white noise station. Bypasses the streaming pipeline entirely - there is no
    /// network round trip, no backend to prepare and no buffer to wait out, so none of the
    /// machinery <see cref="PlayWithBufferInternalAsync"/> exists for applies here.
    /// </summary>
    private void PlayWhiteNoise()
    {
        Debug.WriteLine("[RadioPlayerService] Playing white noise");

        CancelPendingPlayAttempt();
        _whiteNoiseEngine.Play(_whiteNoiseColor, _volume);
        _wasExternalPause = false;

        // The engine fails closed (no audio device, WASAPI busy) rather than throwing, so its
        // own IsPlaying is the only way to know whether this actually started. Reporting
        // success regardless would leave the UI showing "Playing" over silence with nothing to
        // ever correct it - exactly the kind of failure that reads as an error "persisting".
        bool started = _whiteNoiseEngine.IsPlaying;
        if (started)
        {
            // Genuine audio reaches the speakers for as long as this plays, so letting the
            // watchdog's silence monitor run over it behaves exactly as it should for a real
            // stream: it stays quiet unless the render device itself goes dead.
            _watchdog.NotifyUserIntentionToPlay();
        }
        else
        {
            LogService.Warn("RadioPlayerService", "White noise failed to start - no usable audio output");
        }

        TryEnqueueOnUi(() =>
        {
            PlaybackStateChanged?.Invoke(this, started);
            BufferingStateChanged?.Invoke(this, false);
            ScheduleSystemMediaTransportControlsUpdate();

            if (!started)
            {
                PlaybackFailed?.Invoke(this,
                    LocalizationService.GetString(
                        "PlaybackFailure_WhiteNoiseNoOutput",
                        "Couldn't play white noise - no audio output device is available."));
            }
        });
    }

    /// <summary>
    /// Whether the user currently wants audio: either a backend is already playing, or a play
    /// attempt started by <see cref="Play"/> is still in flight. Pause and Stop cancel that
    /// attempt, so this turns false as soon as the user backs out.
    /// <para>
    /// Recovery paths need this rather than <c>ActiveBackend.IsPlaying</c>: they run because a
    /// backend just failed, and a backend that failed on the first play never reached Playing
    /// at all.
    /// </para>
    /// </summary>
    private bool IsPlaybackWanted =>
        ActiveBackend.IsPlaying || _playAttemptCts is { IsCancellationRequested: false };

    /// <summary>
    /// Cancels an in-flight play attempt started by Play(), if one is pending.
    /// Used so a play/pause toggle received while still buffering aborts the attempt
    /// instead of letting it resume playback after the user already asked to stop.
    /// </summary>
    private void CancelPendingPlayAttempt()
    {
        if (_playAttemptCts is null)
            return;

        Debug.WriteLine("[RadioPlayerService] Cancelling in-flight play attempt");
        _playAttemptCts.Cancel();
        _playAttemptCts.Dispose();
        _playAttemptCts = null;
    }

    /// <summary>
    /// Internal method that handles buffered playback asynchronously.
    /// This is called by Play() to apply buffer settings every time playback starts.
    /// The stream is paused during buffering, then resumed once sufficient buffer is achieved.
    /// </summary>
    private async Task PlayWithBufferInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Recreate the playback source whenever there isn't a live one to resume,
            // or when an external pause means we must seek back to the live position.
            // Note _hasPlayedOnce must NOT gate this: a watchdog recovery clears the
            // source without changing the URL, so _hasPlayedOnce stays true while the
            // source is gone. Gating on it there left us calling Play() on a null source.
            bool hasSource = _nativeBackend.CurrentPlaybackItem is not null ||
                             (_libVlcBackend?.VlcMediaPlayer.Media is not null);
            bool needsRecreation = !hasSource || _wasExternalPause;

            if (needsRecreation)
            {
                string reason = _wasExternalPause
                    ? "Recreating playback source after external pause"
                    : _hasPlayedOnce
                        ? "Recreating playback source - no live source to resume"
                        : "First play - preparing playback source";

                Debug.WriteLine($"[RadioPlayerService] {reason}");

                ClearActiveBackendSource();

                LogService.Info("RadioPlayerService", reason);

                PlaybackPrepareResult prepareResult = await PrepareStreamAsync(cancellationToken);
                if (!prepareResult.Success)
                {
                    LogService.Error("RadioPlayerService", $"Prepare failed: {prepareResult.ErrorMessage}");
                    Debug.WriteLine($"[RadioPlayerService] Prepare failed: {prepareResult.ErrorMessage}");
                    SetManualBuffering(false);
                    ReportPlaybackFailure(prepareResult.ErrorMessage);
                    return;
                }

                if (prepareResult.UsedFallback)
                {
                    LogService.Info("RadioPlayerService", $"Playing via fallback backend {prepareResult.Backend}");
                    Debug.WriteLine("[RadioPlayerService] Using LibVLC fallback for playback");
                }

                _hasPlayedOnce = true;
                StartMetadataForActiveBackend();
                Debug.WriteLine("[RadioPlayerService] Started metadata after prepare");
            }
            else
            {
                Debug.WriteLine("[RadioPlayerService] Reusing existing playback source - resuming playback");
            }

            // A local file opens near-instantly and has no "live edge" to wait for, so the
            // buffer-then-wait dance below - built for a network stream - would only impose an
            // artificial delay on every play/resume.
            bool useNativeBuffering = ActivePlaybackBackend == PlaybackBackendKind.Native;
            bool needsBuffering = useNativeBuffering &&
                                   RequiredBufferDuration > TimeSpan.Zero &&
                                   _activeSourceKind == AudioSourceKind.Radio;

            if (needsBuffering)
            {
                SetActiveBackendVolume(0);

                // Start playback briefly to initiate buffering
                Debug.WriteLine("[RadioPlayerService] Starting playback to initiate buffering...");
                SetInternalStateChange(true);
                ActiveBackend.Play();

                // Small delay to ensure play command is processed before pausing
                await Task.Delay(100, cancellationToken);

                // Pause to prevent audio from playing while we buffer
                Debug.WriteLine("[RadioPlayerService] Pausing for buffering...");
                ActiveBackend.Pause();

                // Set manual buffering state so UI shows buffering during user-configured delay
                SetManualBuffering(true);
                Debug.WriteLine("[RadioPlayerService] Manual buffering state set to true");

                // Wait for the user-set buffer amount of time
                Debug.WriteLine($"[RadioPlayerService] Waiting for buffer time: {RequiredBufferDuration.TotalMilliseconds}ms...");
                await Task.Delay(RequiredBufferDuration, cancellationToken);

                // Check if buffer is complete using GetBufferedRanges
                TimeSpan bufferedDuration = TotalBufferedDuration;
                Debug.WriteLine($"[RadioPlayerService] After wait - Buffered: {bufferedDuration.TotalMilliseconds}ms, Required: {RequiredBufferDuration.TotalMilliseconds}ms");

                // If buffer is not yet sufficient, wait a bit more
                if (bufferedDuration < RequiredBufferDuration)
                {
                    Debug.WriteLine("[RadioPlayerService] Buffer not yet complete, waiting more...");
                    await WaitForSufficientBufferAsync(cancellationToken);
                }

                // The wait above can return early on cancellation without throwing; re-check
                // here so a play/pause toggle during buffering aborts instead of resuming.
                cancellationToken.ThrowIfCancellationRequested();

                // Now resume playback
                Debug.WriteLine("[RadioPlayerService] Buffer complete. Calling _player.Play()...");
                await PlayActiveBackendWithFadeInAsync(cancellationToken);

                // Clear manual buffering state as playback is resuming
                SetManualBuffering(false);
                Debug.WriteLine("[RadioPlayerService] Manual buffering state cleared");
                Debug.WriteLine("[RadioPlayerService] Playback resumed after buffering");
            }
            else
            {
                // No buffering needed - start playback immediately
                Debug.WriteLine("[RadioPlayerService] No buffering required (default level). Starting playback...");
                await PlayActiveBackendWithFadeInAsync(cancellationToken);
                Debug.WriteLine("[RadioPlayerService] _player.Play() called successfully");
            }

            // Clear the external pause flag
            _wasExternalPause = false;
            Debug.WriteLine("[RadioPlayerService] Cleared external pause flag");

            _watchdog.NotifyUserIntentionToPlay();
            Debug.WriteLine("[RadioPlayerService] Notified watchdog of user intention to play");

            // Ensure metadata providers are running for the active backend
            StartMetadataForActiveBackend();
            Debug.WriteLine("[RadioPlayerService] Ensured metadata for active backend");

            // Log final buffer state
            LogBufferedRanges();

            // Play() is fire-and-forget on both engines, so reaching here proves nothing.
            // Verify the engine actually produced playback and switch to the other one if
            // it did not, rather than leaving the user with a silent player and no error.
            if (!await ConfirmPlaybackOrSwitchEngineAsync(cancellationToken))
            {
                SetManualBuffering(false);
                string? diagnosis = await DiagnoseStreamFailureAsync(cancellationToken);
                ReportPlaybackFailure(diagnosis, tooManyAttempts: true);
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[RadioPlayerService] Play attempt cancelled (user requested pause/toggle during buffering)");

            // Make sure playback doesn't sneak in after the user asked to stop
            SetInternalStateChange(true);
            ActiveBackend.Pause();
            SetManualBuffering(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] EXCEPTION in PlayWithBufferInternalAsync: {ex.Message}");
            Debug.WriteLine($"[RadioPlayerService] Exception details: {ex}");

            // Clear manual buffering state on error
            SetManualBuffering(false);
            Debug.WriteLine("[RadioPlayerService] Cleared manual buffering state due to exception");

            Debug.WriteLine("[RadioPlayerService] Re-preparing playback source and trying again...");

            try
            {
                ClearActiveBackendSource();
                PlaybackPrepareResult prepareResult = await PrepareStreamAsync(cancellationToken);
                if (!prepareResult.Success)
                {
                    Debug.WriteLine($"[RadioPlayerService] Retry prepare failed: {prepareResult.ErrorMessage}");
                    ReportPlaybackFailure(prepareResult.ErrorMessage, tooManyAttempts: true);
                    return;
                }

                StartMetadataForActiveBackend();
                Debug.WriteLine("[RadioPlayerService] Started metadata after retry prepare");

                bool useNativeBuffering = ActivePlaybackBackend == PlaybackBackendKind.Native;
                bool needsBuffering = useNativeBuffering && RequiredBufferDuration > TimeSpan.Zero;

                if (needsBuffering)
                {
                    SetActiveBackendVolume(0);
                    SetInternalStateChange(true);
                    ActiveBackend.Play();
                    ActiveBackend.Pause();

                    SetManualBuffering(true);
                    Debug.WriteLine("[RadioPlayerService] Started and paused for buffering (retry), manual buffering set");

                    await Task.Delay(RequiredBufferDuration, cancellationToken);

                    await PlayActiveBackendWithFadeInAsync(cancellationToken);
                    SetManualBuffering(false);
                    Debug.WriteLine("[RadioPlayerService] Playback resumed after buffering (retry), manual buffering cleared");
                }
                else
                {
                    await PlayActiveBackendWithFadeInAsync(cancellationToken);
                    Debug.WriteLine("[RadioPlayerService] ActiveBackend.Play() called successfully (retry)");
                }

                _wasExternalPause = false;
                _hasPlayedOnce = true;
                _watchdog.NotifyUserIntentionToPlay();
                StartMetadataForActiveBackend();
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[RadioPlayerService] Play attempt cancelled during retry (user requested pause/toggle during buffering)");
                SetInternalStateChange(true);
                ActiveBackend.Pause();
                SetManualBuffering(false);
            }
            catch (Exception retryEx)
            {
                SetInternalStateChange(false);

                // Clear manual buffering state on retry failure
                SetManualBuffering(false);
                Debug.WriteLine("[RadioPlayerService] Cleared manual buffering state due to retry exception");

                Debug.WriteLine($"[RadioPlayerService] EXCEPTION on retry: {retryEx.Message}");
                // Log the error but don't throw - this is a fire-and-forget async method.
                // Surface the failure to the user after exhausting the retry.
                ReportPlaybackFailure(retryEx.Message, tooManyAttempts: true);
            }
        }
    }

    /// <summary>
    /// Waits for sufficient buffer based on current buffer level settings.
    /// Uses MediaPlaybackSession.GetBufferedRanges to monitor buffering progress.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    private async Task WaitForSufficientBufferAsync(CancellationToken cancellationToken = default)
    {
        // Calculate timeout based on required buffer (minimum 10s, max 30s)
        int timeoutMs = Math.Clamp((int)RequiredBufferDuration.TotalMilliseconds, 400, 8000);
        const int checkIntervalMs = 250; // Check every 250ms
        int elapsed = 0;

        Debug.WriteLine($"[RadioPlayerService] Waiting for {RequiredBufferDuration.TotalMilliseconds}ms of buffer (timeout: {timeoutMs}ms)...");

        while (elapsed < timeoutMs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine("[RadioPlayerService] Buffer wait cancelled");
                return;
            }

            // Check if stream is fully buffered (BufferingProgress >= 1.0)
            if (BufferingProgress >= 1.0)
            {
                Debug.WriteLine($"[RadioPlayerService] Stream fully buffered (BufferingProgress: {BufferingProgress:P0})");
                return;
            }

            // Check buffered ranges using GetBufferedRanges
            TimeSpan bufferedDuration = TotalBufferedDuration;

            if (bufferedDuration >= RequiredBufferDuration)
            {
                Debug.WriteLine($"[RadioPlayerService] Sufficient buffer achieved: {bufferedDuration.TotalMilliseconds}ms >= {RequiredBufferDuration.TotalMilliseconds}ms");
                return;
            }

            try
            {
                await Task.Delay(checkIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[RadioPlayerService] Buffer wait cancelled during delay");
                return;
            }
            elapsed += checkIntervalMs;
        }

        Debug.WriteLine($"[RadioPlayerService] Buffer wait timeout after {timeoutMs}ms. Current buffer: {TotalBufferedDuration.TotalMilliseconds}ms");
    }

    /// <summary>
    /// Starts playback and waits for sufficient buffer based on current buffer level setting.
    /// Uses MediaPlaybackSession.GetBufferedRanges to monitor buffering progress.
    /// The stream is paused during buffering, then resumed once sufficient buffer is achieved.
    /// This method is primarily used by the watchdog for recovery scenarios that need cancellation support.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel waiting</param>
    /// <returns>True if playback started with sufficient buffer, false if cancelled or timeout</returns>
    public async Task<bool> PlayWithBufferAsync(CancellationToken cancellationToken = default)
    {
        Debug.WriteLine($"=== PlayWithBufferAsync START ===");
        Debug.WriteLine($"[RadioPlayerService] Required buffer duration: {RequiredBufferDuration.TotalMilliseconds}ms");

        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            // See Play(): this is a caller with nothing to play yet, not a failure.
            Debug.WriteLine("[RadioPlayerService] PlayWithBufferAsync called with no stream URL set");
            return false;
        }

        bool needsBuffering;

        try
        {
            // See PlayWithBufferInternalAsync: _hasPlayedOnce must not gate this. The
            // watchdog recovery path clears the source while leaving _hasPlayedOnce true.
            bool hasSource = _nativeBackend.CurrentPlaybackItem is not null ||
                             (_libVlcBackend?.VlcMediaPlayer.Media is not null);
            bool needsRecreation = !hasSource || _wasExternalPause;

            if (needsRecreation)
            {
                ClearActiveBackendSource();
                PlaybackPrepareResult prepareResult = await PrepareStreamAsync(cancellationToken);
                if (!prepareResult.Success)
                {
                    Debug.WriteLine($"[RadioPlayerService] Prepare failed in PlayWithBufferAsync: {prepareResult.ErrorMessage}");
                    return false;
                }

                _hasPlayedOnce = true;
                StartMetadataForActiveBackend();
                Debug.WriteLine("[RadioPlayerService] Started metadata after prepare (PlayWithBufferAsync)");
            }

            needsBuffering = ActivePlaybackBackend == PlaybackBackendKind.Native &&
                             RequiredBufferDuration > TimeSpan.Zero;

            if (needsBuffering)
            {
                SetActiveBackendVolume(0);

                // Start playback briefly to initiate buffering
                Debug.WriteLine("[RadioPlayerService] Starting playback to initiate buffering...");
                SetInternalStateChange(true);
                ActiveBackend.Play();

                // Small delay to ensure play command is processed before pausing
                await Task.Delay(100, cancellationToken);

                // Pause to prevent audio from playing while we buffer
                Debug.WriteLine("[RadioPlayerService] Pausing for buffering...");
                ActiveBackend.Pause();

                // Set manual buffering state so UI shows buffering during user-configured delay
                SetManualBuffering(true);
                Debug.WriteLine("[RadioPlayerService] Manual buffering state set to true");

                // Wait for the user-set buffer amount of time
                Debug.WriteLine($"[RadioPlayerService] Waiting for buffer time: {RequiredBufferDuration.TotalMilliseconds}ms...");
                try
                {
                    await Task.Delay(RequiredBufferDuration, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[RadioPlayerService] Buffer wait cancelled during initial delay");
                    SetManualBuffering(false);
                    return false;
                }

                // Check if buffer is complete using GetBufferedRanges
                TimeSpan bufferedDuration = TotalBufferedDuration;
                Debug.WriteLine($"[RadioPlayerService] After wait - Buffered: {bufferedDuration.TotalMilliseconds}ms, Required: {RequiredBufferDuration.TotalMilliseconds}ms");

                // If buffer is not yet sufficient, wait more with timeout
                if (bufferedDuration < RequiredBufferDuration)
                {
                    Debug.WriteLine("[RadioPlayerService] Buffer not yet complete, waiting more...");
                    int additionalTimeoutMs = Math.Clamp((int)RequiredBufferDuration.TotalMilliseconds * 2, 5000, 20000);
                    const int checkIntervalMs = 250;
                    int elapsed = 0;

                    while (elapsed < additionalTimeoutMs)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Debug.WriteLine("[RadioPlayerService] Buffer wait cancelled");
                            SetManualBuffering(false);
                            return false;
                        }

                        if (BufferingProgress >= 1.0)
                        {
                            Debug.WriteLine($"[RadioPlayerService] Stream fully buffered (BufferingProgress: {BufferingProgress:P0})");
                            break;
                        }

                        bufferedDuration = TotalBufferedDuration;
                        if (bufferedDuration >= RequiredBufferDuration)
                        {
                            Debug.WriteLine($"[RadioPlayerService] Sufficient buffer achieved: {bufferedDuration.TotalMilliseconds}ms >= {RequiredBufferDuration.TotalMilliseconds}ms");
                            break;
                        }

                        await Task.Delay(checkIntervalMs, cancellationToken);
                        elapsed += checkIntervalMs;
                    }
                }

                // Now resume playback
                Debug.WriteLine("[RadioPlayerService] Buffer complete. Calling _player.Play()...");
                await PlayActiveBackendWithFadeInAsync(cancellationToken);

                // Clear manual buffering state as playback is resuming
                SetManualBuffering(false);
                Debug.WriteLine("[RadioPlayerService] Manual buffering state cleared");
                Debug.WriteLine("[RadioPlayerService] Playback resumed after buffering");
            }
            else
            {
                // No buffering needed - start playback immediately
                Debug.WriteLine("[RadioPlayerService] No buffering required (default level). Starting playback...");
                await PlayActiveBackendWithFadeInAsync(cancellationToken);
            }

            _wasExternalPause = false;
            _watchdog.NotifyUserIntentionToPlay();
            StartMetadataForActiveBackend();
            Debug.WriteLine("[RadioPlayerService] Ensured metadata for active backend (PlayWithBufferAsync)");

            LogBufferedRanges();

            // Play() is fire-and-forget on both backends, so returning true here without
            // checking would report success for a no-op. The watchdog relies on this
            // result to decide whether to escalate, so it has to be honest.
            bool confirmed = await WaitForPlaybackConfirmedAsync(
                PlaybackConfirmationTimeout,
                cancellationToken);

            if (!confirmed)
            {
                LogService.Warn("RadioPlayerService",
                    $"Playback did not start within {PlaybackConfirmationTimeout.TotalSeconds:F0}s of Play() " +
                    $"({DescribeActiveBackendState()})");

                // Escalation is the watchdog ladder's job here, but the engine memory should
                // still learn from this attempt so the next play starts on the better engine.
                if (!string.IsNullOrWhiteSpace(_streamUrl))
                {
                    _playbackEngineSelector.RecordBackendFailure(_streamUrl, ActivePlaybackBackend);
                }

                if (ActivePlaybackBackend == PlaybackBackendKind.LibVlc)
                {
                    _libVlcBackend?.DumpDiagnostics("Watchdog play attempt did not reach playback");
                }
            }

            return confirmed;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[RadioPlayerService] PlayWithBufferAsync cancelled");
            SetManualBuffering(false);
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] EXCEPTION in PlayWithBufferAsync: {ex.Message}");
            SetManualBuffering(false);
            throw;
        }
        finally
        {
            Debug.WriteLine($"=== PlayWithBufferAsync END ===");
        }
    }

    /// <summary>
    /// How long to wait for a backend to actually report playing after Play() is called,
    /// before treating the attempt as failed. Scales with the configured buffer so a large
    /// buffer setting doesn't get reported as a failure while it is still filling.
    /// </summary>
    private TimeSpan PlaybackConfirmationTimeout
    {
        get
        {
            TimeSpan scaled = RequiredBufferDuration + RequiredBufferDuration;
            return scaled > MinPlaybackConfirmationTimeout ? scaled : MinPlaybackConfirmationTimeout;
        }
    }

    /// <summary>
    /// Polls the active backend until it reports playing, or the timeout elapses.
    /// Mirrors the polling shape of <see cref="PlaybackEngineSelector.WaitForNativeOpenAsync"/>.
    /// </summary>
    /// <returns>True if the backend reported playing before the timeout.</returns>
    private async Task<bool> WaitForPlaybackConfirmedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        const int pollIntervalMs = 250;
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                if (ActiveBackend.IsPlaying)
                {
                    Debug.WriteLine("[RadioPlayerService] Playback confirmed - backend reports playing");
                    return true;
                }
            }
            catch (Exception ex)
            {
                // A disposed or half-torn-down backend counts as not playing.
                Debug.WriteLine($"[RadioPlayerService] Error probing playback state: {ex.Message}");
                return false;
            }

            try
            {
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Logs the current buffered ranges for debugging purposes.
    /// </summary>
    private void LogBufferedRanges()
    {
        try
        {
            IReadOnlyList<MediaTimeRange> bufferedRanges = ActiveBackend.GetBufferedRanges();
            if (bufferedRanges.Count == 0)
            {
                Debug.WriteLine("[RadioPlayerService] No buffered ranges available");
                return;
            }

            Debug.WriteLine($"[RadioPlayerService] Buffered ranges ({bufferedRanges.Count}):");
            TimeSpan totalBuffered = TimeSpan.Zero;
            foreach (MediaTimeRange range in bufferedRanges)
            {
                TimeSpan duration = range.End - range.Start;
                totalBuffered += duration;
                Debug.WriteLine($"  - {range.Start.TotalSeconds:F2}s to {range.End.TotalSeconds:F2}s (duration: {duration.TotalMilliseconds:F0}ms)");
            }
            Debug.WriteLine($"[RadioPlayerService] Total buffered: {totalBuffered.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Error logging buffered ranges: {ex.Message}");
        }
    }

    /// <summary>
    /// Raises the <see cref="PlaybackFailed"/> event on the UI thread.
    /// </summary>
    private void RaisePlaybackFailed(string message)
    {
        Debug.WriteLine($"[RadioPlayerService] Raising PlaybackFailed: {message}");

        // A failed stream produces no more audio, so a track still waiting out its delay
        // would be announced over silence.
        ResetTrackInfoHold();

        TryEnqueueOnUi(() => PlaybackFailed?.Invoke(this, message));
    }

    /// <summary>
    /// Surfaces a user-facing playback failure, at most once per play attempt.
    /// The message is tailored to the cause: offline, a specific stream error,
    /// or repeated failures. Call <see cref="ResetPlaybackFailureTracking"/> when
    /// a fresh attempt begins so the next failure can be reported again.
    /// </summary>
    private void ReportPlaybackFailure(string? detail = null, bool tooManyAttempts = false)
    {
        if (_hasReportedPlaybackFailure)
        {
            Debug.WriteLine("[RadioPlayerService] Playback failure already reported for this attempt, skipping");
            return;
        }

        _hasReportedPlaybackFailure = true;
        string message = BuildPlaybackFailureMessage(detail, tooManyAttempts);
        LogService.Error("RadioPlayerService",
            $"Reporting playback failure to user (tooManyAttempts={tooManyAttempts}): {message}");
        RaisePlaybackFailed(message);
    }

    /// <summary>
    /// Reports a playback failure after probing the stream, so the user is told what is
    /// actually wrong ("the server refused the connection", "this address is a playlist
    /// file") instead of the engine's own error text, which is rarely meaningful to them.
    /// <para>
    /// Claims the report slot before awaiting, so failures arriving from the other engine
    /// while the probe is running don't produce a second dialog.
    /// </para>
    /// </summary>
    private async Task ReportPlaybackFailureWithDiagnosisAsync(string? engineDetail, bool tooManyAttempts)
    {
        if (_hasReportedPlaybackFailure)
        {
            Debug.WriteLine("[RadioPlayerService] Playback failure already reported for this attempt, skipping");
            return;
        }

        _hasReportedPlaybackFailure = true;

        string? diagnosis = null;
        try
        {
            diagnosis = await DiagnoseStreamFailureAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("RadioPlayerService", "Stream diagnosis failed", ex);
        }

        LogService.Error("RadioPlayerService",
            $"Playback failed (tooManyAttempts={tooManyAttempts}); engine reported: {engineDetail ?? "(nothing)"}");

        string message = BuildPlaybackFailureMessage(diagnosis ?? engineDetail, tooManyAttempts);
        RaisePlaybackFailed(message);
    }

    /// <summary>
    /// Clears the per-attempt failure tracking so a new play attempt can report
    /// failures and retry with fallback again.
    /// </summary>
    private void ResetPlaybackFailureTracking()
    {
        _consecutivePlaybackFailures = 0;
        _hasReportedPlaybackFailure = false;
    }

    /// <summary>
    /// Builds a friendly, actionable message describing why playback failed.
    /// Prefers a no-network explanation, then any specific stream error detail.
    /// </summary>
    private static string BuildPlaybackFailureMessage(string? detail, bool tooManyAttempts)
    {
        if (!NetworkStatusService.IsInternetAvailable())
        {
            return NoNetworkMessage;
        }

        string sanitizedDetail = SanitizeFailureDetail(detail);
        bool hasDetail = !string.IsNullOrWhiteSpace(sanitizedDetail);

        if (!hasDetail)
        {
            return tooManyAttempts
                ? LocalizationService.GetString(
                    "PlaybackFailure_TooManyAttemptsNoDetail",
                    "Couldn't play this station after several attempts. The stream may be offline or the URL may be wrong.")
                : LocalizationService.GetString(
                    "PlaybackFailure_NoDetail",
                    "Couldn't play this station. The stream may be unavailable or the URL may be wrong.");
        }

        // A diagnosis from StreamDiagnostics is already a complete sentence explaining the
        // cause, so it reads better standing on its own than parenthesised inside a generic
        // apology. Raw engine error text is a fragment and still needs the wrapper.
        if (ReadsAsSentence(sanitizedDetail))
        {
            return string.Format(
                LocalizationService.GetString("PlaybackFailure_WithSentenceDetail", "Couldn't play this station. {0}"),
                sanitizedDetail);
        }

        return tooManyAttempts
            ? string.Format(
                LocalizationService.GetString(
                    "PlaybackFailure_TooManyAttemptsWithDetail",
                    "Couldn't play this station after several attempts ({0}). The stream may be offline or the URL may be wrong."),
                sanitizedDetail)
            : string.Format(
                LocalizationService.GetString("PlaybackFailure_WithDetail", "Couldn't play this station: {0}"),
                sanitizedDetail);
    }

    /// <summary>
    /// Whether a failure detail is already a self-contained sentence, as the stream
    /// diagnosis produces, rather than a raw error fragment from a playback engine.
    /// </summary>
    private static bool ReadsAsSentence(string detail)
    {
        return detail.Length > 0 &&
               char.IsUpper(detail[0]) &&
               (detail.EndsWith('.') || detail.EndsWith('!'));
    }

    /// <summary>
    /// Trims and length-limits a raw backend/stream error message so it reads well in a dialog.
    /// </summary>
    private static string SanitizeFailureDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        string trimmed = detail.Trim();
        const int maxLength = 200;
        return trimmed.Length > maxLength ? trimmed[..maxLength] + "…" : trimmed;
    }

    /// <summary>
    /// Handles a failure reported by a playback backend. Retries with the other
    /// backend up to a small limit, then reports the error to the user and stops
    /// retrying so a persistently broken stream can't loop indefinitely.
    /// </summary>
    private void HandleBackendFailure(PlaybackFailureEventArgs e)
    {
        if (_hasReportedPlaybackFailure)
        {
            Debug.WriteLine("[RadioPlayerService] Ignoring backend failure - already reported for this attempt");
            return;
        }

        _consecutivePlaybackFailures++;
        Debug.WriteLine($"[RadioPlayerService] Backend failure #{_consecutivePlaybackFailures} ({e.Backend}): {e.Message}");

        bool canFallback = e.CanRetryWithFallback && _libVlcBackend is not null;
        bool tooManyAttempts = _consecutivePlaybackFailures >= MaxConsecutivePlaybackFailures;

        LogService.Warn("RadioPlayerService",
            $"Backend failure #{_consecutivePlaybackFailures} ({e.Backend}): {e.Message} " +
            $"[canFallback={canFallback}, tooManyAttempts={tooManyAttempts}, {DescribeActiveBackendState()}]");

        // Teach the engine memory which engine this stream fails on, so the next play
        // starts with the other one instead of rediscovering the failure.
        if (!string.IsNullOrWhiteSpace(_streamUrl))
        {
            _playbackEngineSelector.RecordBackendFailure(_streamUrl, e.Backend);
        }

        if (tooManyAttempts || !canFallback)
        {
            SetManualBuffering(false);
            _ = ReportPlaybackFailureWithDiagnosisAsync(e.Message, tooManyAttempts);
            return;
        }

        _ = TryFallbackPlaybackAsync();
    }

    /// <summary>
    /// Sets the manual buffering state and notifies listeners.
    /// </summary>
    private void SetManualBuffering(bool isBuffering)
    {
        if (_isManuallyBuffering == isBuffering) return;

        Debug.WriteLine($"[RadioPlayerService] Manual buffering state changing from {_isManuallyBuffering} to {isBuffering}");
        _isManuallyBuffering = isBuffering;

        // Notify on UI thread
        TryEnqueueOnUi(() =>
        {
            BufferingStateChanged?.Invoke(this, IsBuffering);
        });
    }

    /// <summary>
    /// Stop playback and clean up resources
    /// </summary>
    public void Pause()
    {
        Debug.WriteLine($"=== Pause START ===");
        Debug.WriteLine($"[RadioPlayerService] Pause called");
        Debug.WriteLine($"[RadioPlayerService] Current stream URL: {_streamUrl}");
        Debug.WriteLine($"[RadioPlayerService] Current IsPlaying: {_player.PlaybackSession.PlaybackState}");

        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            Debug.WriteLine("[RadioPlayerService] No stream URL set, nothing to pause");
            Debug.WriteLine($"=== Pause END (no URL) ===");
            return;
        }

        switch (_activeSourceKind)
        {
            case AudioSourceKind.WhiteNoise:
                PauseWhiteNoise();
                Debug.WriteLine($"=== Pause END (white noise) ===");
                return;

            case AudioSourceKind.Files:
                PauseLocalMusic();
                Debug.WriteLine($"=== Pause END (local music) ===");
                return;

            case AudioSourceKind.Radio:
            default:
                break;
        }

        // Abort a still-buffering play attempt so it doesn't resume playback after this call
        CancelPendingPlayAttempt();

        try
        {
            Debug.WriteLine("[RadioPlayerService] Calling ActiveBackend.Pause()...");
            SetInternalStateChange(true);
            ActiveBackend.Pause();
            Debug.WriteLine("[RadioPlayerService] ActiveBackend.Pause() called successfully");

            // Clear manual buffering state when pausing
            SetManualBuffering(false);
            Debug.WriteLine("[RadioPlayerService] Cleared manual buffering state");

            // Mark that pause occurred - next play should recreate MediaSource to ensure live position
            _wasExternalPause = true;
            Debug.WriteLine("[RadioPlayerService] Marked for MediaSource recreation on next play (ensures live position)");

            _watchdog.NotifyUserIntentionToPause();
            Debug.WriteLine("[RadioPlayerService] Notified watchdog of user intention to pause");

            // Stop metadata polling
            StopMetadata();
            Debug.WriteLine("[RadioPlayerService] Stopped metadata");

            // Tear down the source on pause rather than just muting playback, so a paused
            // stream stops using data. Play() already recreates the source whenever
            // _wasExternalPause is set (see above), so resuming re-opens a fresh connection.
            ClearActiveBackendSource();
            Debug.WriteLine("[RadioPlayerService] Cleared active backend source");
        }
        catch (Exception ex)
        {
            SetInternalStateChange(false);
            Debug.WriteLine($"[RadioPlayerService] EXCEPTION in Pause: {ex.Message}");
            Debug.WriteLine($"[RadioPlayerService] Exception details: {ex}");
        }

        Debug.WriteLine($"=== Pause END ===");
    }

    /// <summary>
    /// Pauses a local music track. Deliberately does not set <see cref="_wasExternalPause"/> or
    /// tear down the backend source the way the Radio pause path does - those exist to force a
    /// fresh connection at the live edge on resume, which a local file has no equivalent of and
    /// would otherwise reset it to position 0 on every pause/resume.
    /// </summary>
    private void PauseLocalMusic()
    {
        Debug.WriteLine("[RadioPlayerService] Pausing local music");

        CancelPendingPlayAttempt();

        try
        {
            SetInternalStateChange(true);
            ActiveBackend.Pause();
            SetManualBuffering(false);
            _watchdog.NotifyUserIntentionToPause();
        }
        catch (Exception ex)
        {
            SetInternalStateChange(false);
            Debug.WriteLine($"[RadioPlayerService] EXCEPTION in PauseLocalMusic: {ex.Message}");
        }
    }

    /// <summary>Stops a white noise station. See <see cref="PlayWhiteNoise"/>.</summary>
    private void PauseWhiteNoise()
    {
        Debug.WriteLine("[RadioPlayerService] Pausing white noise");

        CancelPendingPlayAttempt();
        _whiteNoiseEngine.Stop();
        _watchdog.NotifyUserIntentionToPause();

        TryEnqueueOnUi(() =>
        {
            PlaybackStateChanged?.Invoke(this, false);
            BufferingStateChanged?.Invoke(this, false);
            ScheduleSystemMediaTransportControlsUpdate();
        });
    }

    /// <summary>
    /// Toggle between play and pause
    /// </summary>
    public void TogglePlayPause()
    {
        Debug.WriteLine($"=== TogglePlayPause START ===");
        Debug.WriteLine($"[RadioPlayerService] Current IsPlaying: {IsPlaying}, IsBuffering: {IsBuffering}");
        Debug.WriteLine($"[RadioPlayerService] Current stream URL: {_streamUrl}");

        // Treat buffering as "playing" for toggle purposes so a press during a long
        // buffering wait cancels the pending play attempt instead of starting another one
        if (IsPlaying || IsBuffering)
        {
            Debug.WriteLine("[RadioPlayerService] Is playing or buffering, calling Pause()");
            Pause();
        }
        else
        {
            Debug.WriteLine("[RadioPlayerService] Not playing, calling Play()");
            Play();
        }

        Debug.WriteLine($"=== TogglePlayPause END ===");
    }

    private void TryEnqueueOnUi(DispatcherQueueHandler action)
    {
        if (_uiQueue is null)
        {
            action();
            return;
        }

        if (_uiQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _uiQueue.TryEnqueue(action);
        }
    }

    private void OnSystemMediaButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        Debug.WriteLine($"[RadioPlayerService] System media button pressed: {args.Button}");

        TryEnqueueOnUi(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Debug.WriteLine("[RadioPlayerService] Play button pressed from system controls");
                    Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    Debug.WriteLine("[RadioPlayerService] Pause button pressed from system controls");
                    Pause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    Debug.WriteLine("[RadioPlayerService] Next button pressed from system controls");
                    NextStationRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    Debug.WriteLine("[RadioPlayerService] Previous button pressed from system controls");
                    PreviousStationRequested?.Invoke(this, EventArgs.Empty);
                    break;
                default:
                    Debug.WriteLine($"[RadioPlayerService] Unhandled button: {args.Button}");
                    break;
            }
        });
    }

    /// <summary>
    /// Sets the internal state change flag with a timer to automatically clear it.
    /// This ensures the flag remains true long enough to cover asynchronous state changes.
    /// </summary>
    /// <param name="isInternal">True to mark state changes as internal, false to clear immediately</param>
    private void SetInternalStateChange(bool isInternal)
    {
        _isInternalStateChange = isInternal;

        if (isInternal)
        {
            Debug.WriteLine("[RadioPlayerService] Internal state change flag SET (will auto-clear in 1000ms)");

            // Clear any existing timer
            _internalStateChangeTimer?.Dispose();

            // Set a timer to clear the flag after 1000ms
            // This gives enough time for all async state changes from Play()/Pause() to complete
            // Increased from 500ms to 1000ms to better handle slower network connections
            _internalStateChangeTimer = new Timer(
                callback: _ =>
                {
                    _isInternalStateChange = false;
                    Debug.WriteLine("[RadioPlayerService] Internal state change flag AUTO-CLEARED");
                },
                state: null,
                dueTime: 1000, // 1000ms to cover all async state transitions including network delays
                period: System.Threading.Timeout.Infinite
            );
        }
        else
        {
            Debug.WriteLine("[RadioPlayerService] Internal state change flag CLEARED immediately");
            _internalStateChangeTimer?.Dispose();
            _internalStateChangeTimer = null;
        }
    }

    /// <summary>
    /// Schedules a debounced update to the System Media Transport Controls.
    /// Multiple rapid calls will be coalesced into a single update after 100ms of inactivity.
    /// </summary>
    private void ScheduleSystemMediaTransportControlsUpdate()
    {
        lock (_smtcUpdateLock)
        {
            // Mark that an update is pending
            _smtcUpdatePending = true;

            // Reset the timer - this will delay the update by another 100ms
            _smtcUpdateTimer?.Dispose();
            _smtcUpdateTimer = new Timer(
                callback: _ => ExecuteSystemMediaTransportControlsUpdate(),
                state: null,
                dueTime: 100, // 100ms delay
                period: System.Threading.Timeout.Infinite // Don't repeat
            );

            Debug.WriteLine("[RadioPlayerService] SMTC update scheduled (debounced)");
        }
    }

    /// <summary>
    /// Executes the actual System Media Transport Controls update on the UI thread.
    /// </summary>
    private void ExecuteSystemMediaTransportControlsUpdate()
    {
        lock (_smtcUpdateLock)
        {
            if (!_smtcUpdatePending)
            {
                return;
            }

            _smtcUpdatePending = false;
            Debug.WriteLine("[RadioPlayerService] Executing debounced SMTC update");
        }

        // Execute the update on the UI thread
        TryEnqueueOnUi(() =>
        {
            UpdateSystemMediaTransportControls();
        });
    }

    private void UpdateSystemMediaTransportControls()
    {
        if (_systemMediaControls == null)
            return;

        try
        {
            _systemMediaControls.IsNextEnabled = _isStationCyclingEnabled;
            _systemMediaControls.IsPreviousEnabled = _isStationCyclingEnabled;

            // Get the display updater
            SystemMediaTransportControlsDisplayUpdater updater = _systemMediaControls.DisplayUpdater;

            // Always set the type to Music for radio stations
            updater.Type = MediaPlaybackType.Music;

            // Update playback status
            _systemMediaControls.PlaybackStatus = IsPlaying
                ? MediaPlaybackStatus.Playing
                : MediaPlaybackStatus.Paused;

            StreamMetadata metadata = CurrentMetadata;

            // Set artist and title from metadata if available
            if (metadata.HasMetadata)
            {
                // Set artist - prefer metadata artist, fall back to station name
                if (!string.IsNullOrWhiteSpace(metadata.Artist))
                {
                    updater.MusicProperties.Artist = metadata.Artist;
                    // Only set AlbumArtist when we don't have artist info, otherwise it takes precedence
                    updater.MusicProperties.AlbumArtist = string.Empty;
                }
                else
                {
                    updater.MusicProperties.Artist = _currentStationName ?? "Radio Station";
                    updater.MusicProperties.AlbumArtist = string.Empty;
                }

                // Set title from metadata
                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    updater.MusicProperties.Title = metadata.Title;
                }
                else if (!string.IsNullOrWhiteSpace(metadata.StreamTitle))
                {
                    updater.MusicProperties.Title = metadata.StreamTitle;
                }
                else
                {
                    updater.MusicProperties.Title = "Now Playing";
                }

                // Set album title to station name for additional context
                updater.MusicProperties.AlbumTitle = _currentStationName ?? "Radio Station";

                Debug.WriteLine($"[RadioPlayerService] SMTC updated with metadata - Artist: {updater.MusicProperties.Artist}, Title: {updater.MusicProperties.Title}, Album: {updater.MusicProperties.AlbumTitle}");
            }
            else
            {
                // No metadata available, show station name
                updater.MusicProperties.Artist = _currentStationName ?? "Radio Station";
                updater.MusicProperties.Title = "Streaming...";
                updater.MusicProperties.AlbumArtist = string.Empty;
                updater.MusicProperties.AlbumTitle = string.Empty;
                Debug.WriteLine($"[RadioPlayerService] SMTC updated with station name: {_currentStationName}");
            }

            // Handle album art with proper priority: metadata album art > station favicon > none
            // Use async/await pattern to handle fallback properly
            _ = Task.Run(async () =>
            {
                bool thumbnailSet = false;

                // Try metadata album art first
                if (!string.IsNullOrWhiteSpace(metadata.AlbumArtUrl))
                {
                    if (metadata.AlbumArtUrl != _currentAlbumArtUrl)
                    {
                        Debug.WriteLine($"[RadioPlayerService] Attempting to set album art from metadata: {metadata.AlbumArtUrl}");
                        thumbnailSet = await SetAlbumArtAsync(updater, metadata.AlbumArtUrl);

                        if (thumbnailSet)
                        {
                            _currentAlbumArtUrl = metadata.AlbumArtUrl;
                            LogService.Info("RadioPlayerService", $"SMTC thumbnail set from track art: {ImageFormat.DescribeUrl(metadata.AlbumArtUrl)}");
                            Debug.WriteLine($"[RadioPlayerService] Successfully set album art from metadata");
                        }
                        else
                        {
                            LogService.Info("RadioPlayerService", $"Failed to set SMTC thumbnail from track art: {ImageFormat.DescribeUrl(metadata.AlbumArtUrl)}; will try favicon");
                            Debug.WriteLine($"[RadioPlayerService] Failed to set album art from metadata, will try favicon");
                        }
                    }
                    else
                    {
                        thumbnailSet = true; // Already set, no need to update
                        TryEnqueueOnUi(() => updater.Update());
                    }
                }

                // If metadata album art failed or wasn't available, try station favicon
                if (!thumbnailSet && !string.IsNullOrWhiteSpace(_currentStationFaviconUrl))
                {
                    if (_currentStationFaviconUrl != _currentAlbumArtUrl)
                    {
                        Debug.WriteLine($"[RadioPlayerService] Attempting to set favicon as fallback: {_currentStationFaviconUrl}");
                        thumbnailSet = await SetAlbumArtAsync(updater, _currentStationFaviconUrl);

                        if (thumbnailSet)
                        {
                            _currentAlbumArtUrl = _currentStationFaviconUrl;
                            LogService.Info("RadioPlayerService", $"SMTC thumbnail set from station favicon: {_currentStationFaviconUrl}");
                            Debug.WriteLine($"[RadioPlayerService] Successfully set favicon as thumbnail");
                        }
                        else
                        {
                            LogService.Info("RadioPlayerService", $"Failed to set SMTC thumbnail from station favicon: {_currentStationFaviconUrl}");
                            Debug.WriteLine($"[RadioPlayerService] Failed to set favicon as thumbnail");
                            _currentAlbumArtUrl = null; // Reset so we can retry later
                        }
                    }
                    else
                    {
                        thumbnailSet = true; // Already set, no need to update
                        TryEnqueueOnUi(() => updater.Update());
                    }
                }

                // If both failed or weren't available, clear the thumbnail
                if (!thumbnailSet)
                {
                    TryEnqueueOnUi(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(_currentAlbumArtUrl))
                        {
                            _currentAlbumArtUrl = null;
                            updater.Thumbnail = null;
                            LogService.Info("RadioPlayerService", "Cleared SMTC thumbnail (no track art or favicon available)");
                            Debug.WriteLine("[RadioPlayerService] Cleared album art");
                        }
                        updater.Update();
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Failed to update SystemMediaTransportControls: {ex.Message}");
        }
    }

    private async Task<bool> SetAlbumArtAsync(SystemMediaTransportControlsDisplayUpdater updater, string imageUrl)
    {
        try
        {
            byte[] imageData;
            if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int commaIndex = imageUrl.IndexOf(',');
                if (commaIndex < 0)
                {
                    return false;
                }

                string base64 = imageUrl[(commaIndex + 1)..];
                imageData = Convert.FromBase64String(base64);
                Debug.WriteLine($"[RadioPlayerService] Decoded embedded album art ({imageData.Length} bytes)");
                LogService.Info("AlbumArt", $"SMTC art from embedded art: {ImageFormat.Describe(imageData)}");
            }
            else if (Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            {
                // A local album's favicon is the folder's own cover art on disk (see
                // LocalMusicFolderScanner), not something HttpClient can dial - it throws
                // NotSupportedException on a file:// scheme.
                Debug.WriteLine($"[RadioPlayerService] Reading album art from disk: {uri.LocalPath}");
                imageData = await File.ReadAllBytesAsync(uri.LocalPath);
                Debug.WriteLine($"[RadioPlayerService] Read {imageData.Length} bytes of album art from disk");
                LogService.Info("AlbumArt", $"SMTC art from file {uri.LocalPath}: {ImageFormat.Describe(imageData)}");
            }
            else
            {
                Debug.WriteLine($"[RadioPlayerService] Downloading album art from: {imageUrl}");
                imageData = await _httpClient.GetByteArrayAsync(imageUrl);
                Debug.WriteLine($"[RadioPlayerService] Downloaded {imageData.Length} bytes of album art");
                LogService.Info("AlbumArt", $"SMTC art from {imageUrl}: {ImageFormat.Describe(imageData)}");
            }

            if (ImageFormat.DetectMime(imageData) is null)
            {
                // updater.Thumbnail accepts any stream; the shell only discovers it isn't an
                // image when it decodes it later, and then just shows nothing. Report it here
                // and let the caller fall through to the favicon instead.
                LogService.Warn("AlbumArt", $"SMTC art is not a recognized image; skipping it: {ImageFormat.Describe(imageData)}");
                return false;
            }

            // Create a random access stream from the image data
            InMemoryRandomAccessStream stream = new();
            DataWriter writer = new(stream.GetOutputStreamAt(0));
            writer.WriteBytes(imageData);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            writer.Dispose();

            // Seek to the beginning of the stream
            stream.Seek(0);

            // Create a RandomAccessStreamReference from the stream
            RandomAccessStreamReference thumbnail = RandomAccessStreamReference.CreateFromStream(stream);

            // Set the thumbnail on the UI thread. This must actually be awaited: TryEnqueueOnUi
            // only enqueues the action and returns immediately when called from this background
            // thread, so a bare bool written inside the callback would always still read false
            // here - which used to make every call look like a failure and fall through to the
            // favicon, then to clearing the thumbnail entirely, even when the set had worked.
            var tcs = new TaskCompletionSource<bool>();
            void SetThumbnail()
            {
                try
                {
                    updater.Thumbnail = thumbnail;
                    updater.Update();
                    Debug.WriteLine("[RadioPlayerService] Album art set successfully");
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RadioPlayerService] Failed to set album art thumbnail: {ex.Message}");
                    LogService.Warn("AlbumArt", $"SMTC updater rejected the thumbnail: {ex.GetType().Name}: {ex.Message}");
                    tcs.TrySetResult(false);
                }
            }

            if (_uiQueue is null || _uiQueue.HasThreadAccess)
            {
                SetThumbnail();
            }
            else if (!_uiQueue.TryEnqueue(SetThumbnail))
            {
                tcs.TrySetResult(false);
            }

            // Deliberately not disposing `stream` here: SMTC/Action Center reads the referenced
            // stream lazily after Update() returns, not synchronously, so disposing it this soon
            // risks the shell reading an already-closed stream and showing a blank/default icon
            // instead of the art that was just "successfully" set. It's small and short-lived
            // enough to leave for the GC once nothing references it anymore.
            return await tcs.Task;
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Failed to download album art: {ex.Message}");
            LogService.Warn("AlbumArt", $"SMTC art download failed for {imageUrl}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Error setting album art: {ex.Message}");
            LogService.Warn("AlbumArt", $"SMTC art failed for {ImageFormat.DescribeUrl(imageUrl)}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Debug.WriteLine("[RadioPlayerService] Dispose called");

        CancelPendingPlayAttempt();

        _systemMediaControls?.ButtonPressed -= OnSystemMediaButtonPressed;

        PowerManagementService.SetPlaybackActive(false);

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch
        {
            // Ignore errors during cleanup
        }

        // Dispose the debounce timer
        _smtcUpdateTimer?.Dispose();
        _smtcUpdateTimer = null;

        // Dispose the internal state change timer
        _internalStateChangeTimer?.Dispose();
        _internalStateChangeTimer = null;

        _watchdog.Dispose();
        DisposePlaybackEngine();
        _whiteNoiseEngine.Dispose();
        _httpClient.Dispose();

        ClearActiveBackendSource();

        _player.Dispose();
        Debug.WriteLine("[RadioPlayerService] Disposed");
    }
}
