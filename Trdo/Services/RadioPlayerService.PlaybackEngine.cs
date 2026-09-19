using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services.Metadata;
using Trdo.Services.Playback;

namespace Trdo.Services;

public sealed partial class RadioPlayerService
{
    private StreamMetadataService _icyMetadataService = null!;
    private NativeTimedMetadataService _nativeTimedMetadataService = null!;
    private LibVlcMetadataProvider _libVlcMetadataProvider = null!;
    private HlsSegmentMetadataService _hlsSegmentMetadataService = null!;
    private StreamMetadataOrchestrator _metadataOrchestrator = null!;
    private MetadataPublishGate _publishGate = null!;
    private NativePlaybackBackend _nativeBackend = null!;
    private LibVlcPlaybackBackend? _libVlcBackend;
    private PlaybackEngineSelector _playbackEngineSelector = null!;
    private string? _lastPrepareError;

    // Guards engine switching. Both the confirmation deadline and a backend's own failure
    // event can decide to switch engines, and they can fire within the same few seconds.
    // Letting both run would switch twice and land back on the engine that just failed.
    // 0 = idle, 1 = a switch is in progress.
    private int _engineSwitchGate;

    public string? LastPrepareError => _lastPrepareError;

    public PlaybackBackendKind ActivePlaybackBackend => _playbackEngineSelector.ActiveBackendKind;

    private IPlaybackBackend ActiveBackend => _playbackEngineSelector.ActiveBackend;

    private void InitializePlaybackEngine()
    {
        _icyMetadataService = new StreamMetadataService();
        _nativeTimedMetadataService = new NativeTimedMetadataService(_uiQueue);
        _libVlcMetadataProvider = new LibVlcMetadataProvider();
        _hlsSegmentMetadataService = new HlsSegmentMetadataService();
        _metadataOrchestrator = new StreamMetadataOrchestrator(
            _icyMetadataService,
            _nativeTimedMetadataService,
            _libVlcMetadataProvider,
            _hlsSegmentMetadataService);

        // Everything the app shows about the current track comes out of the gate, not the
        // orchestrator: the orchestrator reports what the station has announced, the gate
        // reports what the listener can actually hear.
        _publishGate = new MetadataPublishGate
        {
            Log = line => LogService.Info("TrackInfoDelay", line),
            IsPlaybackActive = () => IsPlaying || IsBuffering
        };

        _metadataOrchestrator.MetadataChanged += (_, metadata) => _publishGate.Submit(metadata);

        _publishGate.MetadataPublished += (_, metadata) =>
        {
            Debug.WriteLine($"[RadioPlayerService] Metadata published: {metadata.DisplayText}");
            TryEnqueueOnUi(() =>
            {
                StreamMetadataChanged?.Invoke(this, metadata);
                ScheduleSystemMediaTransportControlsUpdate();
            });
        };

        _nativeBackend = new NativePlaybackBackend(_player, _httpClient);
        _nativeBackend.PlaybackFailed += OnNativePlaybackFailed;
        _nativeBackend.PlaybackEnded += OnBackendPlaybackEnded;

        if (LibVlcHost.TryInitialize() && LibVlcHost.Instance is not null)
        {
            _libVlcBackend = new LibVlcPlaybackBackend(LibVlcHost.Instance, LibVlcHost.LogCapture);
            _libVlcBackend.PlaybackStateChanged += OnLibVlcPlaybackStateChanged;
            _libVlcBackend.BufferingStateChanged += OnLibVlcBackendBufferingStateChanged;
            _libVlcBackend.PlaybackFailed += OnLibVlcPlaybackFailed;
            _libVlcBackend.PlaybackEnded += OnBackendPlaybackEnded;
        }

        _playbackEngineSelector = new PlaybackEngineSelector(_nativeBackend, _libVlcBackend);
        Debug.WriteLine("[RadioPlayerService] Playback engine initialized");
    }

    private async Task<PlaybackPrepareResult> PrepareStreamAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            throw new InvalidOperationException("No stream URL set. Call SetStreamUrl first.");
        }

        _libVlcBackend?.NetworkCachingMs = (int)RequiredBufferDuration.TotalMilliseconds;

        // Report the value LibVLC will actually use - a configured 0 becomes its own default.
        int cachingMs = _libVlcBackend?.EffectiveNetworkCachingMs ?? (int)RequiredBufferDuration.TotalMilliseconds;
        LogService.Info("RadioPlayerService",
            $"Preparing stream {LogService.Redact(_streamUrl)} (networkCaching={cachingMs}ms)");

        PlaybackPrepareResult result = await _playbackEngineSelector.PrepareAsync(_streamUrl, cancellationToken);
        _lastPrepareError = result.Success ? null : result.ErrorMessage;

        LogService.Info("RadioPlayerService",
            $"Prepare result: success={result.Success}, backend={result.Backend}, usedFallback={result.UsedFallback}" +
            (result.Success ? string.Empty : $", error={result.ErrorMessage}"));

        if (!result.Success)
        {
            return result;
        }

        if (result.Backend == PlaybackBackendKind.Native &&
            _libVlcBackend is not null &&
            PlaybackEngineSelector.GetEngineMode() == PlaybackEngineMode.NativePreferred)
        {
            bool opened = await _playbackEngineSelector.WaitForNativeOpenAsync(
                cancellationToken,
                TimeSpan.FromSeconds(15));

            if (!opened)
            {
                LogService.Warn("RadioPlayerService", "Native open timed out after 15s; attempting LibVLC fallback");
                Debug.WriteLine("[RadioPlayerService] Native open timeout, attempting LibVLC fallback");
                result = await _playbackEngineSelector.RetryWithFallbackAsync(_streamUrl, cancellationToken);
                _lastPrepareError = result.Success ? null : result.ErrorMessage;
            }
        }

        SyncActiveBackendVolume();
        return result;
    }

    public Task<bool> RetryWithPlaybackFallbackAsync(CancellationToken cancellationToken = default)
    {
        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            return RetryWithPlaybackFallbackInternalAsync(cancellationToken);
        }

        TaskCompletionSource<bool> tcs = new();
        _uiQueue.TryEnqueue(async () =>
        {
            try
            {
                bool result = await RetryWithPlaybackFallbackInternalAsync(cancellationToken);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private async Task<bool> RetryWithPlaybackFallbackInternalAsync(CancellationToken cancellationToken = default)
    {
        // Retrying from LibVLC to native needs no LibVLC backend; only the
        // native-to-LibVLC direction requires one.
        if (string.IsNullOrWhiteSpace(_streamUrl) ||
            (_libVlcBackend is null && ActivePlaybackBackend == PlaybackBackendKind.Native))
        {
            return false;
        }

        PlaybackPrepareResult result = await _playbackEngineSelector.RetryWithFallbackAsync(_streamUrl, cancellationToken);
        _lastPrepareError = result.Success ? null : result.ErrorMessage;
        if (!result.Success)
        {
            return false;
        }

        SyncActiveBackendVolume();
        return true;
    }

    /// <summary>
    /// Tears the playback pipeline down and rebuilds it from scratch for the current stream.
    /// This is the "hard reset" rung of the recovery ladder: unlike a soft retry, it discards
    /// the backend's player state, not just the media, so a poisoned pipeline can't survive.
    /// </summary>
    /// <param name="recycleBackend">
    /// When true, the active backend's underlying player object is recreated as well.
    /// </param>
    /// <returns>True if the rebuilt pipeline reached confirmed playback.</returns>
    public Task<bool> RebuildPlaybackPipelineAsync(bool recycleBackend, CancellationToken cancellationToken = default)
    {
        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            return RebuildPlaybackPipelineInternalAsync(recycleBackend, cancellationToken);
        }

        TaskCompletionSource<bool> tcs = new();
        _uiQueue.TryEnqueue(async () =>
        {
            try
            {
                bool result = await RebuildPlaybackPipelineInternalAsync(recycleBackend, cancellationToken);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // reconfigure runs while the pipeline is torn down, before the stream is re-prepared, for
    // changes LibVLC only honors on an idle player (a new cast renderer).
    private async Task<bool> RebuildPlaybackPipelineInternalAsync(
        bool recycleBackend,
        CancellationToken cancellationToken,
        Action? reconfigure = null)
    {
        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            return false;
        }

        LogService.Warn("RadioPlayerService",
            $"Rebuilding playback pipeline for {LogService.Redact(_streamUrl)} " +
            $"(backend={ActivePlaybackBackend}, recycleBackend={recycleBackend})");

        try
        {
            SetInternalStateChange(true);
            ActiveBackend.Pause();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Error pausing during rebuild: {ex.Message}");
        }

        StopMetadata();
        ClearActiveBackendSource();

        if (recycleBackend)
        {
            RecycleActiveBackend();
        }

        reconfigure?.Invoke();

        // A rebuilt pipeline has no source and is not resuming from a user pause, so both
        // flags must return to their first-play values or the prepare below would be skipped.
        _hasPlayedOnce = false;
        _wasExternalPause = false;

        PlaybackPrepareResult result = await PrepareStreamAsync(cancellationToken);
        if (!result.Success)
        {
            LogService.Error("RadioPlayerService", $"Rebuild prepare failed: {result.ErrorMessage}");
            SetManualBuffering(false);
            return false;
        }

        SyncActiveBackendVolume();

        bool started = await PlayWithBufferAsync(cancellationToken);
        LogService.Info("RadioPlayerService",
            $"Pipeline rebuild {(started ? "succeeded" : "did not reach playback")} on {ActivePlaybackBackend}");

        return started;
    }

    /// <summary>
    /// Recreates the underlying player object of the active backend, discarding any
    /// internal state the previous one accumulated.
    /// </summary>
    private void RecycleActiveBackend()
    {
        try
        {
            if (ActivePlaybackBackend == PlaybackBackendKind.LibVlc && _libVlcBackend is not null)
            {
                _libVlcBackend.Recycle();
            }
            else
            {
                // The native backend rides on the shared WinRT MediaPlayer, which cannot be
                // recreated without rebuilding SMTC and every subscriber. Dropping the source
                // is the equivalent teardown for it.
                _player.Source = null;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("RadioPlayerService", $"Backend recycle failed: {ex.Message}");
            Debug.WriteLine($"[RadioPlayerService] Backend recycle failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Marks the active backend as unable to play the current stream, so the next prepare
    /// starts with the other one, then rebuilds the pipeline.
    /// </summary>
    /// <returns>True if the rebuilt pipeline reached confirmed playback.</returns>
    public async Task<bool> SwitchBackendAndRebuildAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            return false;
        }

        if (_playbackEngineSelector.RequireLibVlc)
        {
            // Casting has no other engine to switch to, and marking LibVLC unusable for this
            // stream would outlive the cast session. Rebuild on the same engine instead.
            LogService.Warn("RadioPlayerService",
                $"Cannot switch engines for {LogService.Redact(_streamUrl)} while casting; rebuilding on LibVLC");
            return await RebuildPlaybackPipelineAsync(recycleBackend: true, cancellationToken);
        }

        LogService.Warn("RadioPlayerService",
            $"Marking {ActivePlaybackBackend} unhealthy for {LogService.Redact(_streamUrl)} and switching backend");
        _playbackEngineSelector.MarkBackendUnhealthy(_streamUrl, ActivePlaybackBackend);

        return await RebuildPlaybackPipelineAsync(recycleBackend: true, cancellationToken);
    }

    /// <summary>
    /// Waits for the active backend to actually reach playback and, if it does not, switches
    /// to the other engine and tries once more.
    /// <para>
    /// This closes the failure mode behind stations that "just don't play": an engine can
    /// accept the stream, report no error, and never produce audio. LibVLC in particular
    /// always succeeds at prepare and only raises <c>EncounteredError</c> for some failures,
    /// so without an explicit confirmation deadline nothing detected the stall on the user's
    /// play. Recovery was left to the watchdog, which takes three escalation rungs and the
    /// better part of a minute to reach the engine switch that fixes it.
    /// </para>
    /// </summary>
    /// <returns>True once an engine has reached confirmed playback.</returns>
    private async Task<bool> ConfirmPlaybackOrSwitchEngineAsync(CancellationToken cancellationToken)
    {
        if (await WaitForPlaybackConfirmedAsync(PlaybackConfirmationTimeout, cancellationToken))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            return false;
        }

        PlaybackBackendKind stalled = ActivePlaybackBackend;
        LogService.Warn("RadioPlayerService",
            $"{stalled} did not reach playback within {PlaybackConfirmationTimeout.TotalSeconds:F0}s " +
            $"for {LogService.Redact(_streamUrl)} ({DescribeActiveBackendState()})");

        if (stalled == PlaybackBackendKind.LibVlc)
        {
            _libVlcBackend?.DumpDiagnostics("Playback did not start");
        }

        _playbackEngineSelector.RecordBackendFailure(_streamUrl, stalled);

        if (!CanSwitchEngine(stalled))
        {
            LogService.Warn("RadioPlayerService",
                $"No alternative engine available for {LogService.Redact(_streamUrl)} " +
                $"(libVlcAvailable={_playbackEngineSelector.IsLibVlcAvailable}, mode={PlaybackEngineSelector.GetEngineMode()})");
            return false;
        }

        // The backend's own failure event may already be switching engines. Rather than
        // switch a second time, give that attempt the confirmation window instead.
        if (Interlocked.CompareExchange(ref _engineSwitchGate, 1, 0) != 0)
        {
            LogService.Info("RadioPlayerService",
                "An engine switch is already in progress; waiting for it rather than starting another");
            return await WaitForPlaybackConfirmedAsync(PlaybackConfirmationTimeout, cancellationToken);
        }

        try
        {
            LogService.Warn("RadioPlayerService", $"Switching engine after {stalled} stalled, and retrying");

            PlaybackPrepareResult result = await _playbackEngineSelector.RetryWithFallbackAsync(_streamUrl, cancellationToken);
            _lastPrepareError = result.Success ? null : result.ErrorMessage;

            if (!result.Success)
            {
                LogService.Error("RadioPlayerService", $"Engine switch prepare failed: {result.ErrorMessage}");
                return false;
            }

            SyncActiveBackendVolume();
            await PlayActiveBackendWithFadeInAsync(cancellationToken);
            StartMetadataForActiveBackend();

            bool confirmed = await WaitForPlaybackConfirmedAsync(PlaybackConfirmationTimeout, cancellationToken);
            if (confirmed)
            {
                LogService.Info("RadioPlayerService",
                    $"Engine switch to {ActivePlaybackBackend} restored playback for {LogService.Redact(_streamUrl)}");
            }
            else
            {
                LogService.Error("RadioPlayerService",
                    $"Both engines failed to play {LogService.Redact(_streamUrl)}; " +
                    $"{ActivePlaybackBackend} state: {DescribeActiveBackendState()}");
                _playbackEngineSelector.RecordBackendFailure(_streamUrl, ActivePlaybackBackend);
            }

            return confirmed;
        }
        finally
        {
            Interlocked.Exchange(ref _engineSwitchGate, 0);
        }
    }

    /// <summary>
    /// Whether there is another engine worth trying after <paramref name="current"/> failed.
    /// </summary>
    private bool CanSwitchEngine(PlaybackBackendKind current)
    {
        // The native engine cannot cast, so while a cast target is set there is nothing to switch to.
        if (_playbackEngineSelector.RequireLibVlc)
        {
            return false;
        }

        if (PlaybackEngineSelector.GetEngineMode() == PlaybackEngineMode.NativeOnly)
        {
            return false;
        }

        // Switching away from LibVLC only needs the native backend, which always exists;
        // switching to LibVLC needs LibVLC to have initialized on this machine.
        return current == PlaybackBackendKind.LibVlc || _playbackEngineSelector.IsLibVlcAvailable;
    }

    private string DescribeActiveBackendState()
    {
        try
        {
            string engineDetail = ActivePlaybackBackend == PlaybackBackendKind.LibVlc && _libVlcBackend is not null
                ? _libVlcBackend.DescribeState()
                : $"buffering={ActiveBackend.IsBuffering}, progress={ActiveBackend.BufferingProgress:P0}";

            return $"engine={ActivePlaybackBackend}, isPlaying={ActiveBackend.IsPlaying}, {engineDetail}";
        }
        catch (Exception ex)
        {
            return $"engine={ActivePlaybackBackend}, state unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Logs the active backend's state and, on LibVLC, its native log ring buffer. Meant for
    /// call sites that have evidence something is wrong (the NAudio silence monitor, a stall)
    /// but whose engine never raised its own failure event - the "reports playing, produces
    /// nothing" case that <see cref="LibVlcPlaybackBackend.PlaybackFailed"/> does not cover,
    /// because LibVLC's own state machine never left the Playing state.
    /// </summary>
    public void DumpActiveBackendDiagnostics(string context)
    {
        LogService.Warn("RadioPlayerService", $"{context} ({DescribeActiveBackendState()})");

        if (ActivePlaybackBackend == PlaybackBackendKind.LibVlc)
        {
            _libVlcBackend?.DumpDiagnostics(context);
        }
    }

    /// <summary>
    /// Works out why the current stream will not play and writes the full finding to the log,
    /// returning a short explanation suitable for showing the user.
    /// <para>
    /// Both engines can only report that they failed, not why. Probing the URL directly is
    /// the only way to tell "this station is offline" from "this address is a playlist file"
    /// from "both engines are being defeated by something local" — and those need completely
    /// different responses from the user.
    /// </para>
    /// </summary>
    public async Task<string?> DiagnoseStreamFailureAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            return null;
        }

        string url = _streamUrl;

        try
        {
            StreamDiagnosis diagnosis = await StreamDiagnostics.ProbeAsync(
                url,
                _httpClient,
                cancellationToken: cancellationToken);

            LogService.Error("StreamDiagnostics",
                $"Probe of {LogService.Redact(url)} -> {diagnosis.Result}: {diagnosis.Detail}");
            LogService.Info("StreamDiagnostics",
                $"Engine history: {_playbackEngineSelector.DescribeEngineHealth(url)}; " +
                $"mode={PlaybackEngineSelector.GetEngineMode()}, " +
                $"libVlcAvailable={_playbackEngineSelector.IsLibVlcAvailable}, " +
                $"lastActive={ActivePlaybackBackend}");

            if (diagnosis.Result == StreamProbeResult.PlaylistFile && diagnosis.PlaylistEntryUrl is not null)
            {
                string format = LocalizationService.GetString(
                    "StreamDiagnosis_TryPlaylistEntry",
                    "{0} Try using {1} as the stream address instead.");
                return string.Format(format, diagnosis.Summary, diagnosis.PlaylistEntryUrl);
            }

            if (diagnosis.ServerLooksHealthy)
            {
                // The server is fine, so the fault is on this machine's side. Say so rather
                // than blaming the station, and include what the engine reported.
                string? engineReason = _lastPrepareError ?? _libVlcBackend?.DescribeLastError();
                return string.IsNullOrWhiteSpace(engineReason)
                    ? LocalizationService.GetString(
                        "StreamDiagnosis_EngineFailedHealthyServer",
                        "The station is online, but neither playback engine could play it on this PC.")
                    : string.Format(
                        LocalizationService.GetString(
                            "StreamDiagnosis_EngineFailedHealthyServerWithReason",
                            "The station is online, but playback failed on this PC: {0}"),
                        engineReason);
            }

            return diagnosis.Summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("StreamDiagnostics", $"Probe of {LogService.Redact(url)} threw", ex);
            return null;
        }
    }

    private void StartMetadataForActiveBackend()
    {
        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            return;
        }

        // Local files have their own once-per-track tag read rather than the poll-based
        // orchestrator, which routes to ICY/native-timed/HLS sources that don't apply here.
        if (_activeSourceKind == AudioSourceKind.Files)
        {
            PublishLocalTrackMetadata();
            return;
        }

        if (_activeSourceKind != AudioSourceKind.Radio)
        {
            return;
        }

        _metadataOrchestrator.EnsureForPlayback(
            _streamUrl,
            _playbackEngineSelector.ActiveBackendKind,
            _nativeBackend.CurrentPlaybackItem,
            _libVlcBackend?.VlcMediaPlayer);
    }

    private void PublishLocalTrackMetadata()
    {
        if (_localTrackIndex < 0 || _localTrackIndex >= _localTrackList.Count)
        {
            return;
        }

        string path = _localTrackList[_localTrackIndex];
        _ = PublishLocalTrackMetadataAsync(path);
    }

    private async Task PublishLocalTrackMetadataAsync(string path)
    {
        try
        {
            StreamMetadata metadata = await LocalFileMetadataService.ReadAsync(path);
            _publishGate.Submit(metadata);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioPlayerService] Failed to read local track metadata: {ex.Message}");
        }
    }

    private void StopMetadata()
    {
        // Order matters: the blank has to reach the gate before Reset arms the station-start
        // flag, so it cannot be mistaken for that next station's opening track.
        _metadataOrchestrator.StopAll();

        // Submitted directly rather than trusting StopAll's own push to survive the
        // orchestrator's internal dedup: that dedup compares against the orchestrator's own
        // "current metadata", which local music never updates (it feeds the gate straight,
        // bypassing the orchestrator entirely). So leaving a local station's folder for a radio
        // station the orchestrator still remembers as blank would have StopAll's push
        // dropped as a no-op "repeat", leaving the local track's title on screen - looking like
        // the radio station is playing it - until the new station's own metadata eventually
        // arrives. A direct submit here always reaches the gate regardless of what the
        // orchestrator last saw.
        _publishGate.Submit(StreamMetadata.Empty);
        _publishGate.Reset();
    }

    public async Task RefreshMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_streamUrl) || _activeSourceKind != AudioSourceKind.Radio)
        {
            return;
        }

        await _metadataOrchestrator.RefreshAsync(
            _streamUrl,
            _playbackEngineSelector.ActiveBackendKind,
            _nativeBackend.CurrentPlaybackItem,
            _libVlcBackend?.VlcMediaPlayer,
            cancellationToken);
    }

    /// <summary>
    /// Records the active backend as proven for the current stream. Called from the
    /// backends' playing transitions - the only reliable evidence a backend works here.
    /// </summary>
    private void ConfirmActiveBackendHealthy()
    {
        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            return;
        }

        _playbackEngineSelector.ConfirmBackendHealthy(_streamUrl, ActivePlaybackBackend);
    }

    private void SyncActiveBackendVolume()
    {
        SetActiveBackendVolume(GetActiveBackendTargetVolume());
    }

    private double GetActiveBackendTargetVolume()
    {
        return ActivePlaybackBackend == PlaybackBackendKind.Native
            ? Math.Min(EffectiveVolume, 1)
            : EffectiveVolume;
    }

    private void SetActiveBackendVolume(double volume)
    {
        double maximum = ActivePlaybackBackend == PlaybackBackendKind.Native ? 1 : 2;
        _activeBackendVolume = Math.Clamp(volume, 0, maximum);
        ActiveBackend.SetVolume(_activeBackendVolume);
    }

    private async Task PlayActiveBackendWithFadeInAsync(CancellationToken cancellationToken)
    {
        SetActiveBackendVolume(0);
        SetInternalStateChange(true);
        ActiveBackend.Play();

        await FadeActiveBackendVolumeAsync(
            targetVolume: GetActiveBackendTargetVolume(),
            FadeInDuration,
            followUserVolume: true,
            cancellationToken);
    }

    private async Task FadeActiveBackendVolumeAsync(
        double targetVolume,
        TimeSpan duration,
        bool followUserVolume,
        CancellationToken cancellationToken)
    {
        await _volumeFadeLock.WaitAsync(cancellationToken);
        try
        {
            _isVolumeFading = true;
            double startVolume = _activeBackendVolume;
            int steps = Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds / FadeStepInterval.TotalMilliseconds));

            for (int step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double progress = (double)step / steps;
                double easedProgress = progress * progress * (3 - (2 * progress));
                double currentTarget = followUserVolume
                    ? GetActiveBackendTargetVolume()
                    : targetVolume;

                SetActiveBackendVolume(startVolume + ((currentTarget - startVolume) * easedProgress));

                if (step < steps)
                {
                    await Task.Delay(FadeStepInterval, cancellationToken);
                }
            }

            SetActiveBackendVolume(followUserVolume ? GetActiveBackendTargetVolume() : targetVolume);
        }
        finally
        {
            _isVolumeFading = false;
            _volumeFadeLock.Release();
        }
    }

    private void ClearActiveBackendSource()
    {
        ActiveBackend.ClearSource();
        _player.Source = null;
    }

    private void OnNativePlaybackFailed(object? sender, PlaybackFailureEventArgs e)
    {
        LogService.Warn("RadioPlayerService", $"Native backend reported failure: {e.Message}");
        Debug.WriteLine($"[RadioPlayerService] Native playback failed: {e.Message}");
        HandleBackendFailure(e);
    }

    private async Task TryFallbackPlaybackAsync()
    {
        if (string.IsNullOrWhiteSpace(_streamUrl))
        {
            return;
        }

        // The confirmation deadline may already be switching engines for this same attempt.
        if (Interlocked.CompareExchange(ref _engineSwitchGate, 1, 0) != 0)
        {
            LogService.Info("RadioPlayerService",
                "Skipping fallback - an engine switch is already in progress for this stream");
            return;
        }

        try
        {
            await TryFallbackPlaybackCoreAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _engineSwitchGate, 0);
        }
    }

    private async Task TryFallbackPlaybackCoreAsync()
    {
        try
        {
            await TryFallbackPlaybackUnguardedAsync();
        }
        catch (Exception ex)
        {
            // Nothing above this is awaited - HandleBackendFailure starts the fallback and
            // walks away - so an escaping exception would otherwise vanish, leaving the user
            // on "Buffering..." with no error: the very symptom of issue #109, on the path
            // taken by every first-play failure.
            LogService.Error("RadioPlayerService", "Playback fallback threw", ex);
            Debug.WriteLine($"[RadioPlayerService] EXCEPTION in playback fallback: {ex}");
            SetManualBuffering(false);
            await ReportPlaybackFailureWithDiagnosisAsync(ex.Message, tooManyAttempts: true);
        }
    }

    private async Task TryFallbackPlaybackUnguardedAsync()
    {
        // Not ActiveBackend.IsPlaying: we get here *because* a backend just failed, so on the
        // very first play it has already dropped out of Playing and that test is always false.
        // It left the fallback engine prepared but never started, so the user sat on
        // "Buffering..." forever with no error - see issue #109. What matters is whether the
        // user still wants audio, which an in-flight play attempt says just as well as an
        // already-playing stream does.
        bool wantsPlayback = IsPlaybackWanted;
        LogService.Warn("RadioPlayerService", $"Trying playback fallback (wantsPlayback={wantsPlayback})");
        PlaybackPrepareResult result = await _playbackEngineSelector.RetryWithFallbackAsync(_streamUrl!);
        _lastPrepareError = result.Success ? null : result.ErrorMessage;

        if (!result.Success)
        {
            LogService.Error("RadioPlayerService", $"Fallback prepare failed: {result.ErrorMessage}");
            Debug.WriteLine($"[RadioPlayerService] Fallback prepare failed: {result.ErrorMessage}");
            SetManualBuffering(false);
            await ReportPlaybackFailureWithDiagnosisAsync(result.ErrorMessage, tooManyAttempts: true);
            return;
        }

        SyncActiveBackendVolume();

        // Re-check rather than trusting the value from before the prepare: preparing a backend
        // takes time, and the user may have paused or stopped during it.
        if (!wantsPlayback || !IsPlaybackWanted)
        {
            // Paused or stopped mid-failure: leave the fallback prepared and ready to resume,
            // but don't start audio the user did not ask for.
            SetManualBuffering(false);
            return;
        }

        await PlayActiveBackendWithFadeInAsync(CancellationToken.None);
        StartMetadataForActiveBackend();
        _watchdog.NotifyUserIntentionToPlay();

        // The fallback engine is fire-and-forget too, so verify it rather than assuming
        // the switch worked - otherwise a stream neither engine can play looks like it
        // recovered and the user is left with silence and no explanation.
        if (await WaitForPlaybackConfirmedAsync(PlaybackConfirmationTimeout, CancellationToken.None))
        {
            SetManualBuffering(false);
            return;
        }

        LogService.Error("RadioPlayerService",
            $"Fallback to {ActivePlaybackBackend} did not reach playback ({DescribeActiveBackendState()})");
        _playbackEngineSelector.RecordBackendFailure(_streamUrl!, ActivePlaybackBackend);
        SetManualBuffering(false);
        await ReportPlaybackFailureWithDiagnosisAsync(
            $"neither engine could play the stream (last tried {ActivePlaybackBackend})",
            tooManyAttempts: true);
    }

    private void OnLibVlcPlaybackStateChanged(object? sender, bool isPlaying)
    {
        LogService.Info("RadioPlayerService", $"LibVLC state -> isPlaying={isPlaying}");

        // Reaching Playing means the current attempt succeeded - reset failure tracking.
        if (isPlaying)
        {
            ResetPlaybackFailureTracking();
            ConfirmActiveBackendHealthy();
        }
        else if (!IsBuffering)
        {
            // Paused, Stopped or EndReached, none of which route through Pause(). A stall
            // reports buffering instead of a state change, so this cannot fire mid-track.
            ResetTrackInfoHold();
        }

        TryEnqueueOnUi(() =>
        {
            PlaybackStateChanged?.Invoke(this, isPlaying);
            ScheduleSystemMediaTransportControlsUpdate();
        });
    }

    private void OnLibVlcBackendBufferingStateChanged(object? sender, bool isBuffering)
    {
        LogService.Info("RadioPlayerService", $"LibVLC BufferingStateChanged received: isBuffering={isBuffering}, _isUserPaused={_isUserPaused}, _isManuallyBuffering={_isManuallyBuffering}");

        TryEnqueueOnUi(() =>
        {
            // See _isUserPaused: a user pause's source teardown can read back as buffering
            // even though nothing is actually searching for a signal.
            if (_isUserPaused && isBuffering && !_isManuallyBuffering)
            {
                LogService.Info("RadioPlayerService", "LibVLC BufferingStateChanged: suppressed - _isUserPaused is set");
                return;
            }

            LogService.Info("RadioPlayerService", $"LibVLC BufferingStateChanged: raising BufferingStateChanged({isBuffering || _isManuallyBuffering})");
            BufferingStateChanged?.Invoke(this, isBuffering || _isManuallyBuffering);
        });
    }

    private void OnLibVlcPlaybackFailed(object? sender, PlaybackFailureEventArgs e)
    {
        LogService.Warn("RadioPlayerService", $"LibVLC backend reported failure: {e.Message}");
        Debug.WriteLine($"[RadioPlayerService] LibVLC playback failed: {e.Message}");
        HandleBackendFailure(e);
    }

    /// <summary>
    /// Auto-advances a local music station when its current track ends on its own. Both
    /// backends are always subscribed regardless of which is active - only the active one has
    /// media loaded, so only it can ever raise this - and the <see cref="AudioSourceKind.Files"/>
    /// check keeps a radio stream's own end-of-media (rare, but possible on a VOD-style URL)
    /// from being treated as a track change.
    /// </summary>
    private void OnBackendPlaybackEnded(object? sender, EventArgs e)
    {
        if (_activeSourceKind != AudioSourceKind.Files ||
            Interlocked.CompareExchange(ref _localTrackCompletionGate, 1, 0) != 0)
        {
            return;
        }

        TryEnqueueOnUi(() => _ = HandleLocalTrackEndedAsync());
    }

    private async Task HandleLocalTrackEndedAsync()
    {
        try
        {
            LocalMusicTrackEndAction action = LocalMusicPlaybackPolicy.ResolveTrackEnd(
                _localTrackIndex,
                _localTrackList.Count,
                SettingsService.LocalMusicLoopMode);

            SetLocalTrackIndex(action.SelectedTrackIndex);
            if (action.ShouldPlay)
                await PlayLocalTrackAtIndexAsync(action.SelectedTrackIndex);
            else if (action.SelectedTrackIndex == 0)
            {
                Seek(TimeSpan.Zero);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _localTrackCompletionGate, 0);
        }
    }

    private void DisposePlaybackEngine()
    {
        _nativeBackend.PlaybackFailed -= OnNativePlaybackFailed;
        _nativeBackend.PlaybackEnded -= OnBackendPlaybackEnded;

        if (_libVlcBackend is not null)
        {
            _libVlcBackend.PlaybackStateChanged -= OnLibVlcPlaybackStateChanged;
            _libVlcBackend.BufferingStateChanged -= OnLibVlcBackendBufferingStateChanged;
            _libVlcBackend.PlaybackFailed -= OnLibVlcPlaybackFailed;
            _libVlcBackend.PlaybackEnded -= OnBackendPlaybackEnded;
        }

        _playbackEngineSelector.Dispose();
        DisposeCastTarget();
        DisposeSonosTarget();
        _metadataOrchestrator.Dispose();
        _publishGate.Dispose();
        _icyMetadataService.Dispose();
        _hlsSegmentMetadataService.Dispose();
    }
}
