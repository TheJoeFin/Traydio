using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services.Audio;
using Trdo.Services.Playback;
using Windows.Storage;

namespace Trdo.Services;

/// <summary>
/// Monitors the radio stream and automatically resumes playback when the stream stops unexpectedly.
/// Also tracks stutter patterns and can automatically increase buffer when stuttering is detected.
/// </summary>
public sealed partial class StreamWatchdogService : IDisposable
{
    private readonly RadioPlayerService _playerService;
    private readonly DispatcherQueue _uiQueue;
    private readonly AudioSilenceMonitorService _silenceMonitor;
    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private bool _isEnabled;
    private bool _userIntendedPlayback;
    private DateTime _lastStateCheck;
    private int _consecutiveFailures;
    private TimeSpan _lastPosition;
    private DateTime _lastPositionChangeTime;
    private double _lastBufferingProgress;

    // Recovery gate. Both the 5s health poll and the silence monitor can trigger recovery;
    // this makes sure only one of them is ever recovering at a time. 0 = idle, 1 = recovering.
    private int _recoveryGate;

    // Escalation ladder. Owns the failure window, the rung, and the transient buffer bump.
    private readonly RecoveryPolicy _policy;
    private bool _autoBufferIncreaseEnabled;
    private double _currentBufferLevel;
    private double? _stationBufferLevelOverride;
    private const string AutoBufferIncreaseKey = "AutoBufferIncreaseEnabled";
    private const string BufferLevelKey = "BufferLevel";
    private const string SilenceTimeoutKey = "SilenceTimeoutSeconds";
    private const double DefaultSilenceTimeoutSeconds = 5.0;

    // Configuration
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _recoveryDelay = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _backoffDelay = TimeSpan.FromSeconds(30);

    // Stutter detection configuration
    private readonly TimeSpan _stutterWindow = RecoveryPolicy.DefaultFailureWindow;  // Time window to count recovery attempts
    private const double MaxBufferLevel = 3.0;  // Maximum buffer level (0=default, 1=medium, 2=large, 3=extra large)
    private const bool DefaultAutoBufferIncreaseEnabled = true;  // Auto-buffer increase is enabled by default for better user experience
    private const double DefaultBufferLevel = 0.0;  // Start with default (no extra delay) buffer level

    public event EventHandler<StreamWatchdogEventArgs>? StreamStatusChanged;
    public event EventHandler<StutterDetectedEventArgs>? StutterDetected;
    public event EventHandler<double>? BufferLevelChanged;

    /// <summary>
    /// Raised periodically with the current audio output RMS level (0–1 scale).
    /// Forwarded from the NAudio silence monitor for UI visualisation.
    /// Fires on a background thread — marshal to the UI thread before touching XAML.
    /// </summary>
    public event Action<float>? AudioLevelUpdated;

    /// <summary>
    /// Whether the WASAPI loopback monitor is currently capturing. When it is not,
    /// <see cref="AudioLevelUpdated"/> says nothing about whether audio is reaching the
    /// speakers, and consumers must not read silence into its absence.
    /// </summary>
    public bool IsAudioMonitorRunning => _silenceMonitor.IsMonitoring;

    /// <summary>
    /// Gets or sets the silence detection timeout in seconds.
    /// If the audio output is silent for longer than this while the stream is supposed to be playing,
    /// a recovery attempt is triggered. Persisted to local settings.
    /// </summary>
    public double SilenceTimeoutSeconds
    {
        get => _silenceMonitor.SilenceTimeoutSeconds;
        set
        {
            if (Math.Abs(_silenceMonitor.SilenceTimeoutSeconds - value) < 0.01) return;
            _silenceMonitor.SilenceTimeoutSeconds = value;
            SaveSilenceTimeoutSetting();
            Debug.WriteLine($"[Watchdog] Silence timeout set to: {value}s");
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;

            if (_isEnabled)
                Start();
            else
                Stop();
        }
    }

    /// <summary>
    /// Gets or sets whether auto-buffer increase is enabled.
    /// When enabled, the buffer level automatically increases when stutter is detected.
    /// </summary>
    public bool AutoBufferIncreaseEnabled
    {
        get => _autoBufferIncreaseEnabled;
        set
        {
            if (_autoBufferIncreaseEnabled == value) return;
            _autoBufferIncreaseEnabled = value;
            _policy.AutoBufferIncreaseEnabled = value;
            SaveAutoBufferSettings();
            Debug.WriteLine($"[Watchdog] Auto-buffer increase set to: {value}");
        }
    }

    /// <summary>
    /// Gets or sets the user's configured buffer level (0-3), persisted to local settings.
    /// 0 = Default, 1 = Medium, 2 = Large, 3 = Extra Large.
    /// <para>
    /// This is the user's setting and acts as a floor. Automatic escalation no longer writes
    /// here - it applies a transient, per-station bump on top via <see cref="EffectiveBufferLevel"/>,
    /// so a single bad station can't permanently degrade every other station.
    /// </para>
    /// </summary>
    public double BufferLevel
    {
        get => _currentBufferLevel;
        set
        {
            double clampedValue = Math.Clamp(value, 0, MaxBufferLevel);
            if (Math.Abs(_currentBufferLevel - clampedValue) < 0.0001) return;
            double oldValue = _currentBufferLevel;
            _currentBufferLevel = clampedValue;
            SaveAutoBufferSettings();
            Debug.WriteLine($"[Watchdog] Buffer level changed from {oldValue} to {_currentBufferLevel}");
            RaiseBufferLevelChanged(clampedValue);
        }
    }

    /// <summary>
    /// Gets or sets the current station's buffer level override, or <c>null</c> when
    /// the station follows the app-wide <see cref="BufferLevel"/>. Set by
    /// PlayerViewModel as stations are selected; never persisted here (it lives on
    /// the station itself).
    /// </summary>
    public double? StationBufferLevelOverride
    {
        get => _stationBufferLevelOverride;
        set
        {
            double? clamped = value is null ? null : Math.Clamp(value.Value, 0, MaxBufferLevel);
            if (clamped == _stationBufferLevelOverride) return;

            double previousEffectiveLevel = EffectiveBufferLevel;
            _stationBufferLevelOverride = clamped;
            Debug.WriteLine($"[Watchdog] Station buffer override set to: {(clamped?.ToString() ?? "none")}");

            if (Math.Abs(previousEffectiveLevel - EffectiveBufferLevel) > 0.0001)
            {
                RaiseBufferLevelChanged(_currentBufferLevel);
            }
        }
    }

    /// <summary>
    /// Gets the buffer level the current station starts from: its own override when
    /// it has one, otherwise the user's app-wide setting.
    /// </summary>
    public double BaseBufferLevel => _stationBufferLevelOverride ?? _currentBufferLevel;

    /// <summary>
    /// Gets the buffer level actually in force: the base level for the current
    /// station plus any transient escalation the recovery ladder has asked for,
    /// clamped to the maximum.
    /// </summary>
    public double EffectiveBufferLevel =>
        Math.Clamp(BaseBufferLevel + _policy.AutoBufferBump, 0, MaxBufferLevel);

    /// <summary>
    /// Gets the buffer delay in milliseconds based on the effective buffer level.
    /// </summary>
    public int BufferDelayMs
    {
        get
        {
            // Linear interpolation between buffer levels
            // Level 0 = 0ms, Level 1 = 2000ms, Level 2 = 4000ms, Level 3 = 8000ms
            double level = EffectiveBufferLevel;
            if (level <= 0) return 0;
            if (level >= 3) return 8000;
            if (level <= 1) return (int)(level * 2000);
            if (level <= 2) return (int)(2000 + (level - 1) * 2000);
            return (int)(4000 + (level - 2) * 4000);
        }
    }

    /// <summary>
    /// Gets a human-readable description of the user's configured buffer level.
    /// </summary>
    public string BufferLevelDescription => DescribeBufferLevel(_currentBufferLevel);

    /// <summary>
    /// Maps a buffer level (0-3) onto its human-readable name. Shared so per-station
    /// overrides are labelled identically to the app-wide setting.
    /// </summary>
    public static string DescribeBufferLevel(double level) => level switch
    {
        0 => "Default",
        1 => "Medium",
        2 => "Large",
        3 => "Extra Large",
        _ when level < 0.5 => "Default",
        _ when level < 1.5 => "Medium",
        _ when level < 2.5 => "Large",
        _ => "Extra Large"
    };

    public StreamWatchdogService(RadioPlayerService playerService)
    {
        _playerService = playerService ?? throw new ArgumentNullException(nameof(playerService));
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        _lastStateCheck = DateTime.UtcNow;
        _userIntendedPlayback = false;
        _lastPosition = TimeSpan.Zero;
        _lastPositionChangeTime = DateTime.UtcNow;
        _lastBufferingProgress = 0;
        _policy = new RecoveryPolicy(failureWindow: _stutterWindow);

        // Initialize NAudio silence monitor
        _silenceMonitor = new AudioSilenceMonitorService();
        _silenceMonitor.SilenceDetected += OnSilenceDetected;
        _silenceMonitor.AudioLevelUpdated += OnAudioLevelFromMonitor;

        // Radio static is our own noise on the output; without this the monitor would hear it and
        // conclude a dead stream is still playing.
        _silenceMonitor.ShouldIgnoreCapturedAudio = () => RadioStaticService.Instance.IsAudible;

        // Load settings
        LoadAutoBufferSettings();
        LoadSilenceTimeoutSetting();

        _policy.AutoBufferIncreaseEnabled = _autoBufferIncreaseEnabled;
    }

    private void LoadAutoBufferSettings()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(AutoBufferIncreaseKey, out object? autoBufferValue))
            {
                _autoBufferIncreaseEnabled = autoBufferValue switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out bool b2) => b2,
                    _ => DefaultAutoBufferIncreaseEnabled
                };
            }
            else
            {
                _autoBufferIncreaseEnabled = DefaultAutoBufferIncreaseEnabled;
            }

            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(BufferLevelKey, out object? bufferLevelValue))
            {
                _currentBufferLevel = bufferLevelValue switch
                {
                    double d => Math.Clamp(d, 0, MaxBufferLevel),
                    int i => Math.Clamp((double)i, 0, MaxBufferLevel),
                    string s when double.TryParse(s, out double d2) => Math.Clamp(d2, 0, MaxBufferLevel),
                    _ => DefaultBufferLevel
                };
            }
            else
            {
                _currentBufferLevel = DefaultBufferLevel;
            }

            Debug.WriteLine($"[Watchdog] Loaded settings - AutoBufferIncrease: {_autoBufferIncreaseEnabled}, BufferLevel: {_currentBufferLevel}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error loading auto-buffer settings: {ex.Message}");
            _autoBufferIncreaseEnabled = DefaultAutoBufferIncreaseEnabled;
            _currentBufferLevel = DefaultBufferLevel;
        }
    }

    private void SaveAutoBufferSettings()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[AutoBufferIncreaseKey] = _autoBufferIncreaseEnabled;
            ApplicationData.Current.LocalSettings.Values[BufferLevelKey] = _currentBufferLevel;
            Debug.WriteLine($"[Watchdog] Saved settings - AutoBufferIncrease: {_autoBufferIncreaseEnabled}, BufferLevel: {_currentBufferLevel}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error saving auto-buffer settings: {ex.Message}");
        }
    }

    private void LoadSilenceTimeoutSetting()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SilenceTimeoutKey, out object? value))
            {
                double timeout = value switch
                {
                    double d => d,
                    int i => (double)i,
                    string s when double.TryParse(s, out double d2) => d2,
                    _ => DefaultSilenceTimeoutSeconds
                };
                _silenceMonitor.SilenceTimeoutSeconds = Math.Clamp(timeout, 1.0, 60.0);
            }
            else
            {
                _silenceMonitor.SilenceTimeoutSeconds = DefaultSilenceTimeoutSeconds;
            }
            Debug.WriteLine($"[Watchdog] Loaded silence timeout: {_silenceMonitor.SilenceTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error loading silence timeout: {ex.Message}");
            _silenceMonitor.SilenceTimeoutSeconds = DefaultSilenceTimeoutSeconds;
        }
    }

    private void SaveSilenceTimeoutSetting()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[SilenceTimeoutKey] = _silenceMonitor.SilenceTimeoutSeconds;
            Debug.WriteLine($"[Watchdog] Saved silence timeout: {_silenceMonitor.SilenceTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error saving silence timeout: {ex.Message}");
        }
    }

    /// <summary>
    /// Notify the watchdog that the user intentionally started playback.
    /// </summary>
    public void NotifyUserIntentionToPlay()
    {
        _userIntendedPlayback = true;
        _consecutiveFailures = 0;
        _lastPositionChangeTime = DateTime.UtcNow;
        StartSilenceMonitor();
        Debug.WriteLine("[Watchdog] User started playback - monitoring active");
    }

    /// <summary>
    /// Notify the watchdog that the user intentionally paused/stopped playback.
    /// </summary>
    public void NotifyUserIntentionToPause()
    {
        _userIntendedPlayback = false;
        _consecutiveFailures = 0;
        StopSilenceMonitor();
        Debug.WriteLine("[Watchdog] User paused playback - recovery disabled");
    }

    /// <summary>
    /// Starts monitoring the stream.
    /// </summary>
    public void Start()
    {
        if (_monitoringTask is not null && !_monitoringTask.IsCompleted)
            return; // Already running

        _cts = new CancellationTokenSource();
        _consecutiveFailures = 0;
        _lastPositionChangeTime = DateTime.UtcNow;
        _monitoringTask = Task.Run(() => MonitorStreamAsync(_cts.Token));

        if (_userIntendedPlayback)
            StartSilenceMonitor();

        RaiseStatusChanged("Watchdog started", StreamWatchdogStatus.Monitoring);
    }

    /// <summary>
    /// Stops monitoring the stream.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _monitoringTask = null;
        _userIntendedPlayback = false;
        StopSilenceMonitor();
        RaiseStatusChanged("Watchdog stopped", StreamWatchdogStatus.Stopped);
    }

    private async Task MonitorStreamAsync(CancellationToken cancellationToken)
    {
        Debug.WriteLine("[Watchdog] Monitoring started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    break;

                await CheckStreamHealthAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Watchdog] Error in monitoring loop: {ex.Message}");
                RaiseStatusChanged($"Monitoring error: {ex.Message}", StreamWatchdogStatus.Error);
            }
        }

        Debug.WriteLine("[Watchdog] Monitoring stopped");
    }

    private async Task CheckStreamHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool isPlaying = false;
            AudioSourceKind activeSourceKind = AudioSourceKind.Radio;
            TimeSpan currentPosition = TimeSpan.Zero;
            double currentBufferingProgress = 0;

            // Get current state on UI thread
            await RunOnUiThreadAsync(() =>
            {
                try
                {
                    isPlaying = _playerService.IsPlaying;
                    activeSourceKind = _playerService.ActiveSourceKind;
                    currentPosition = _playerService.Position;
                    currentBufferingProgress = _playerService.BufferingProgress;
                }
                catch
                {
                    // Player might be disposed or in invalid state
                }
            });

            if (activeSourceKind != AudioSourceKind.Radio)
            {
                // Everything below is shaped around a network stream that can stall, drop, or
                // need a buffer bump - none of which applies to a locally-generated source like
                // white noise. The NAudio silence monitor (started by
                // NotifyUserIntentionToPlay) is still the right thing watching its actual audio
                // output, so this only skips the stream-recovery ladder, not that.
                _lastStateCheck = DateTime.UtcNow;
                return;
            }

            // If stream is not playing, handle it as before
            if (!isPlaying)
            {
                // Only attempt recovery if user intended to have it playing
                if (!_userIntendedPlayback)
                {
                    // User intentionally stopped/paused - don't attempt recovery
                    return;
                }

                // Stream stopped unexpectedly - attempt recovery
                TimeSpan timeSinceLastCheck = DateTime.UtcNow - _lastStateCheck;

                // Only attempt recovery if enough time has passed
                if (timeSinceLastCheck > _checkInterval)
                {
                    _consecutiveFailures++;
                    Debug.WriteLine($"[Watchdog] Stream stopped unexpectedly. Attempt {_consecutiveFailures}");
                    await AttemptRecoveryAsync(cancellationToken);
                }

                _lastStateCheck = DateTime.UtcNow;
                return;
            }

            // Stream is playing - NAudio silence monitor handles actual audio detection
            if (!_userIntendedPlayback)
            {
                _userIntendedPlayback = true;
                StartSilenceMonitor();
                Debug.WriteLine("[Watchdog] Stream is playing - silence monitoring active");
            }
            _consecutiveFailures = 0;

            // Feed the ladder healthy observations. It de-escalates only after playback has
            // held for a sustained interval, not on a single healthy poll - a flapping stream
            // used to reset the counter here every few seconds and so never escalated at all.
            _policy.RecordPlaybackConfirmed();

            // Log position/buffering for debugging (silence detection is handled by NAudio)
            bool positionChanged = currentPosition != _lastPosition;
            bool bufferingProgressChanged = Math.Abs(currentBufferingProgress - _lastBufferingProgress) > 0.01;

            if (positionChanged || bufferingProgressChanged)
            {
                _lastPosition = currentPosition;
                _lastBufferingProgress = currentBufferingProgress;
                _lastPositionChangeTime = DateTime.UtcNow;
                Debug.WriteLine($"[Watchdog] Stream healthy - Position: {currentPosition}, Buffering: {currentBufferingProgress:P0}");
            }
            else
            {
                TimeSpan stalledDuration = DateTime.UtcNow - _lastPositionChangeTime;
                Debug.WriteLine($"[Watchdog] Position unchanged for {stalledDuration.TotalSeconds:F1}s (NAudio silence monitor active)");
            }

            _lastStateCheck = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error checking stream health: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs one rung of the recovery ladder. Guarded so the 5s health poll and the silence
    /// monitor - both of which can trigger recovery - never recover concurrently.
    /// </summary>
    private async Task AttemptRecoveryAsync(CancellationToken cancellationToken)
    {
        // 0 -> 1 means we took the gate; anything else means a recovery is already running.
        if (Interlocked.CompareExchange(ref _recoveryGate, 1, 0) != 0)
        {
            Debug.WriteLine("[Watchdog] Recovery already in progress, skipping");
            return;
        }

        try
        {
            if (_policy.HasGivenUp)
            {
                Debug.WriteLine("[Watchdog] Policy has given up; not attempting further recovery");
                return;
            }

            double previousEffectiveLevel = EffectiveBufferLevel;
            RecoveryAction action = _policy.RecordFailure();
            ReportStutterIfDetected(previousEffectiveLevel);

            RaiseStatusChanged(
                $"Stream stopped. Recovery attempt {_policy.FailuresInWindow} (action={action})",
                StreamWatchdogStatus.Recovering);

            if (action == RecoveryAction.GiveUp)
            {
                LogService.Error("Watchdog",
                    "Recovery ladder exhausted; giving up until the user or a station change restarts playback");
                RaiseStatusChanged(
                    "Unable to recover this stream. Try another station or play again.",
                    StreamWatchdogStatus.Error);
                _userIntendedPlayback = false;
                StopSilenceMonitor();
                return;
            }

            if (action == RecoveryAction.BackOff)
            {
                RaiseStatusChanged("Repeated failures. Waiting before retrying.", StreamWatchdogStatus.BackingOff);
                await Task.Delay(_backoffDelay, cancellationToken);
                return;
            }

            // Wait a bit before attempting recovery
            Debug.WriteLine($"[Watchdog] Waiting {_recoveryDelay.TotalMilliseconds}ms before recovery ({action})");
            await Task.Delay(_recoveryDelay, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            bool playbackStarted = action switch
            {
                RecoveryAction.SoftRetry => await SoftRecoverAsync(cancellationToken),
                RecoveryAction.RebuildPipeline =>
                    await _playerService.RebuildPlaybackPipelineAsync(recycleBackend: true, cancellationToken),
                RecoveryAction.SwitchBackend =>
                    await _playerService.SwitchBackendAndRebuildAsync(cancellationToken),
                _ => false
            };

            if (playbackStarted)
            {
                Debug.WriteLine($"[Watchdog] Stream recovery successful via {action}");
                RaiseStatusChanged("Stream resumed with buffer", StreamWatchdogStatus.Recovering);
            }
            else if (!cancellationToken.IsCancellationRequested)
            {
                // The attempt genuinely failed to reach playback. Feed that back into the
                // ladder so the next rung is reached, rather than silently dropping it.
                LogService.Warn("Watchdog", $"Recovery action {action} did not reach playback");
                _policy.RecordFailure();
                RaiseStatusChanged($"Recovery via {action} did not restore playback", StreamWatchdogStatus.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during recovery
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error during recovery: {ex.Message}");
            RaiseStatusChanged($"Recovery error: {ex.Message}", StreamWatchdogStatus.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _recoveryGate, 0);
        }
    }

    /// <summary>
    /// The gentlest rung: re-point the player at the same URL and play again. The player
    /// re-prepares the source because <c>SetStreamUrl</c> clears it.
    /// </summary>
    private async Task<bool> SoftRecoverAsync(CancellationToken cancellationToken)
    {
        Exception? playbackException = null;

        await RunOnUiThreadAsync(() =>
        {
            try
            {
                string? streamUrl = _playerService.StreamUrl;
                if (!string.IsNullOrEmpty(streamUrl))
                {
                    _playerService.SetStreamUrl(streamUrl);
                    Debug.WriteLine("[Watchdog] Stream URL set, starting buffered playback");
                }
            }
            catch (Exception ex)
            {
                playbackException = ex;
                Debug.WriteLine($"[Watchdog] Failed to set stream URL: {ex.Message}");
            }
        });

        if (playbackException != null)
        {
            RaiseStatusChanged($"Recovery failed: {playbackException.Message}", StreamWatchdogStatus.Error);
            return false;
        }

        Debug.WriteLine($"[Watchdog] Starting playback with buffer monitoring (required: {_playerService.RequiredBufferDuration.TotalMilliseconds}ms)");
        return await _playerService.PlayWithBufferAsync(cancellationToken);
    }

    /// <summary>
    /// Raises <see cref="StutterDetected"/> when the ladder's buffer bump changed the
    /// effective buffer level, preserving the pre-existing event contract for listeners.
    /// </summary>
    private void ReportStutterIfDetected(double previousEffectiveLevel)
    {
        double newEffectiveLevel = EffectiveBufferLevel;
        bool increased = newEffectiveLevel - previousEffectiveLevel > 0.0001;

        if (!increased && _policy.FailuresInWindow < 3)
        {
            return;
        }

        Debug.WriteLine($"[Watchdog] STUTTER - {_policy.FailuresInWindow} recoveries in {_stutterWindow.TotalMinutes}min window");

        RaiseStutterDetected(new StutterDetectedEventArgs
        {
            RecoveryAttemptCount = _policy.FailuresInWindow,
            TimeWindow = _stutterWindow,
            PreviousBufferLevel = previousEffectiveLevel,
            NewBufferLevel = newEffectiveLevel,
            BufferWasIncreased = increased
        });
    }

    /// <summary>
    /// Returns the recovery ladder to a clean slate. Called when the user changes station or
    /// clears the playback target, so a new stream doesn't inherit the previous one's
    /// escalation level or buffer bump. The user's configured <see cref="BufferLevel"/> is
    /// left untouched.
    /// </summary>
    public void ResetForStation()
    {
        double previousEffectiveLevel = EffectiveBufferLevel;
        _policy.ResetForStation();
        _consecutiveFailures = 0;

        if (Math.Abs(previousEffectiveLevel - EffectiveBufferLevel) > 0.0001)
        {
            RaiseBufferLevelChanged(_currentBufferLevel);
        }

        Debug.WriteLine("[Watchdog] Recovery state reset for new station");
    }

    /// <summary>
    /// Starts the NAudio silence monitor if the watchdog is enabled and user intends playback.
    /// </summary>
    private void StartSilenceMonitor()
    {
        if (_isEnabled && _userIntendedPlayback)
        {
            _silenceMonitor.Start();
        }
    }

    /// <summary>
    /// Stops the NAudio silence monitor.
    /// </summary>
    private void StopSilenceMonitor()
    {
        _silenceMonitor.Stop();
    }

    private void OnAudioLevelFromMonitor(float level) => AudioLevelUpdated?.Invoke(level);

    /// <summary>
    /// Called by the NAudio silence monitor when audio output has been silent
    /// for longer than <see cref="SilenceTimeoutSeconds"/>.
    /// </summary>
    private async void OnSilenceDetected(object? sender, EventArgs e)
    {
        if (!_isEnabled || !_userIntendedPlayback)
            return;

        try
        {
            StopSilenceMonitor();

            Debug.WriteLine("[Watchdog] NAudio silence detected - attempting stream recovery");
            RaiseStatusChanged("Stream is silent - refreshing", StreamWatchdogStatus.Recovering);

            // AttemptRecoveryAsync owns the recovery gate, so a health-poll recovery
            // already in flight makes this a no-op rather than a competing attempt.
            CancellationToken token = _cts?.Token ?? CancellationToken.None;
            await AttemptRecoveryAsync(token);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[Watchdog] Silence recovery cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error during silence recovery: {ex.Message}");
            RaiseStatusChanged($"Recovery error: {ex.Message}", StreamWatchdogStatus.Error);
        }
        finally
        {
            StartSilenceMonitor();
        }
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        TaskCompletionSource<bool> tcs = new();

        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }
        else
        {
            _uiQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }

        return tcs.Task;
    }

    private void RaiseStatusChanged(string message, StreamWatchdogStatus status)
    {
        Debug.WriteLine($"[Watchdog] {status}: {message}");

        // Info for normal states; Error status maps to Error, recovery/backoff to Warn.
        switch (status)
        {
            case StreamWatchdogStatus.Error:
                LogService.Error("Watchdog", $"{status}: {message}");
                break;
            case StreamWatchdogStatus.Recovering:
            case StreamWatchdogStatus.BackingOff:
                LogService.Warn("Watchdog", $"{status}: {message}");
                break;
            default:
                LogService.Info("Watchdog", $"{status}: {message}");
                break;
        }

        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            StreamStatusChanged?.Invoke(this, new StreamWatchdogEventArgs(message, status));
        }
        else
        {
            _uiQueue.TryEnqueue(() =>
            {
                StreamStatusChanged?.Invoke(this, new StreamWatchdogEventArgs(message, status));
            });
        }
    }

    private void RaiseStutterDetected(StutterDetectedEventArgs args)
    {
        LogService.Warn("Watchdog", $"Stutter detected (bufferIncreased={args.BufferWasIncreased}, newLevel={args.NewBufferLevel})");
        Debug.WriteLine($"[Watchdog] Raising StutterDetected event - BufferIncreased: {args.BufferWasIncreased}, NewLevel: {args.NewBufferLevel}");

        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            StutterDetected?.Invoke(this, args);
        }
        else
        {
            _uiQueue.TryEnqueue(() =>
            {
                StutterDetected?.Invoke(this, args);
            });
        }
    }

    private void RaiseBufferLevelChanged(double newLevel)
    {
        Debug.WriteLine($"[Watchdog] Raising BufferLevelChanged event - NewLevel: {newLevel}");

        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            BufferLevelChanged?.Invoke(this, newLevel);
        }
        else
        {
            _uiQueue.TryEnqueue(() =>
            {
                BufferLevelChanged?.Invoke(this, newLevel);
            });
        }
    }

    public void Dispose()
    {
        Stop();
        _silenceMonitor.SilenceDetected -= OnSilenceDetected;
        _silenceMonitor.AudioLevelUpdated -= OnAudioLevelFromMonitor;
        _silenceMonitor.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// Event arguments for stream watchdog status changes.
/// </summary>
public class StreamWatchdogEventArgs : EventArgs
{
    public string Message { get; }
    public StreamWatchdogStatus Status { get; }
    public DateTime Timestamp { get; }

    public StreamWatchdogEventArgs(string message, StreamWatchdogStatus status)
    {
        Message = message;
        Status = status;
        Timestamp = DateTime.UtcNow;
    }
}

/// <summary>
/// Status of the stream watchdog.
/// </summary>
public enum StreamWatchdogStatus
{
    Stopped,
    Monitoring,
    Recovering,
    BackingOff,
    Error
}

/// <summary>
/// Event arguments for stutter detection events.
/// </summary>
public class StutterDetectedEventArgs : EventArgs
{
    /// <summary>
    /// Number of recovery attempts within the time window that triggered stutter detection.
    /// </summary>
    public int RecoveryAttemptCount { get; init; }

    /// <summary>
    /// The time window used for stutter detection.
    /// </summary>
    public TimeSpan TimeWindow { get; init; }

    /// <summary>
    /// The buffer level before auto-increase was applied.
    /// </summary>
    public double PreviousBufferLevel { get; init; }

    /// <summary>
    /// The buffer level after auto-increase was applied (may be same as previous if at max or disabled).
    /// </summary>
    public double NewBufferLevel { get; init; }

    /// <summary>
    /// Whether the buffer level was actually increased.
    /// </summary>
    public bool BufferWasIncreased { get; init; }

    /// <summary>
    /// Timestamp when the stutter was detected.
    /// </summary>
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
