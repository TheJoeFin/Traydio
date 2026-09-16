using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services.Playback;

namespace Trdo.Services;

/// <summary>
/// Casting to a Sonos player. Unlike a Chromecast, which LibVLC feeds from this PC, a Sonos
/// player is handed the stream's address and fetches it itself: this PC sends UPnP commands
/// (play, pause, stop, seek, volume) and polls the player for what it is doing, but no audio
/// passes through here. Local music is the one exception - the player cannot read a file on
/// this PC, so <see cref="LocalMediaHttpServer"/> serves it over HTTP for the duration.
/// <para>
/// While a Sonos target is set, radio and local music go to the speaker and every playback
/// state the app shows (<see cref="IsPlaying"/>, <see cref="IsBuffering"/>,
/// <see cref="Position"/>) reflects the speaker's report rather than a local engine. White
/// noise is generated locally and is not affected, matching the LibVLC cast path.
/// </para>
/// </summary>
public sealed partial class RadioPlayerService
{
    private enum SonosPlaybackState
    {
        Idle,
        Starting,
        Playing,
        Paused,
    }

    private const string SonosComponent = "RadioPlayerService.Sonos";
    private static readonly TimeSpan SonosStartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SonosStoppedGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SonosPollWhileStarting = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SonosPollWhilePlaying = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SonosPollWhileIdle = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SonosVolumeEchoGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SonosStopOnExitTimeout = TimeSpan.FromMilliseconds(1500);

    private SonosDevice? _sonosDevice;
    private SonosController? _sonosController;
    private LocalMediaHttpServer? _localMediaServer;
    private CancellationTokenSource? _sonosPollCts;
    private SonosPlaybackState _sonosState;

    // Bumped by every play, pause and target change so async work started for an earlier
    // request (a start still confirming, a stop still sending) recognises it is stale.
    private int _sonosSession;

    // The transport URI the speaker currently holds, so resuming a paused local track can
    // send Play alone instead of re-setting the URI, which would restart it from the top.
    private string? _sonosCurrentUri;
    private bool _sonosUserStopped;

    private int _sonosSpeakerVolume;
    private bool _isSonosSpeakerMuted;
    private DateTime _sonosVolumeTouchedUtc = DateTime.MinValue;
    private readonly Lock _sonosVolumeLock = new();
    private bool _sonosVolumeDirty;
    private bool _sonosMuteDirty;
    private bool _sonosVolumeSendRunning;

    private TimeSpan _sonosPosition;
    private DateTime _sonosPositionSampledUtc;
    private TimeSpan? _sonosDuration;
    private string? _sonosStreamContent;

    /// <summary>
    /// Raised on the UI thread when the speaker's own volume or mute state changes, whether
    /// from the slider here or from the Sonos app or the buttons on the player.
    /// </summary>
    public event EventHandler? SonosSpeakerVolumeChanged;

    public bool IsCastingToSonos => _sonosDevice is not null;

    public SonosDevice? SonosTarget => _sonosDevice;

    /// <summary>
    /// Whether the active source is the speaker's to play. White noise stays local even
    /// while a Sonos target is set.
    /// </summary>
    private bool UsesSonosForActiveSource => _sonosDevice is not null && _activeSourceKind != AudioSourceKind.WhiteNoise;

    /// <summary>
    /// The speaker's own volume, 0–100, separate from the app's stream volume (which does
    /// not reach a speaker that fetches the stream itself). Setting it sends the change to
    /// the player; rapid changes while dragging are coalesced into one request at a time.
    /// </summary>
    public int SonosSpeakerVolume
    {
        get => _sonosSpeakerVolume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (clamped == _sonosSpeakerVolume)
            {
                return;
            }

            _sonosSpeakerVolume = clamped;
            _sonosVolumeTouchedUtc = DateTime.UtcNow;
            SonosSpeakerVolumeChanged?.Invoke(this, EventArgs.Empty);
            QueueSonosVolumeSend(volume: true, mute: false);
        }
    }

    public bool IsSonosSpeakerMuted
    {
        get => _isSonosSpeakerMuted;
        set
        {
            if (value == _isSonosSpeakerMuted)
            {
                return;
            }

            _isSonosSpeakerMuted = value;
            _sonosVolumeTouchedUtc = DateTime.UtcNow;
            SonosSpeakerVolumeChanged?.Invoke(this, EventArgs.Empty);
            QueueSonosVolumeSend(volume: false, mute: true);
        }
    }

    /// <summary>
    /// Sends playback to <paramref name="device"/>, or back to this PC when null. If
    /// something is playing it carries on at the new output; otherwise the change applies to
    /// the next play. Replaces a LibVLC cast target if one is set.
    /// </summary>
    public Task SetSonosTargetAsync(SonosDevice? device, CancellationToken cancellationToken = default)
    {
        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            return SetSonosTargetInternalAsync(device, cancellationToken, resumePlayback: true);
        }

        TaskCompletionSource<bool> tcs = new();
        _uiQueue.TryEnqueue(async () =>
        {
            try
            {
                await SetSonosTargetInternalAsync(device, cancellationToken, resumePlayback: true);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private async Task SetSonosTargetInternalAsync(SonosDevice? device, CancellationToken cancellationToken, bool resumePlayback)
    {
        if (device is null && _sonosDevice is null)
        {
            return;
        }

        if (device is not null && device.SameTargetAs(_sonosDevice))
        {
            return;
        }

        // Decided before anything is torn down: IsPlaying reads whichever output is live now.
        bool resume = resumePlayback &&
                      IsPlaybackWanted &&
                      _activeSourceKind != AudioSourceKind.WhiteNoise &&
                      !string.IsNullOrWhiteSpace(_streamUrl);

        LogService.Info(SonosComponent, device is null
            ? $"Stopping cast to Sonos '{_sonosDevice?.Name}'; audio returns to this PC (resume={resume})"
            : $"Casting to Sonos '{device.Name}' ({device.Kind}) at {device.Address}:{device.Port} (resume={resume})");

        try
        {
            if (_sonosDevice is not null)
            {
                await StopSonosSessionAsync(stopSpeaker: true, cancellationToken);
            }
            else if (resume)
            {
                // Leaving local output for the speaker: stop the local engine first so the
                // stream is not fetched twice, and drop its source so coming back later starts
                // clean rather than resuming a stale connection or a file mid-way.
                Pause();
            }

            if (device is not null && _castRenderer is not null)
            {
                // A Chromecast and a Sonos cannot both be the target. Nothing is playing at
                // this point, so the renderer can be dropped without resuming locally.
                await SetCastTargetInternalAsync(null, null, cancellationToken, resumePlayback: false);
            }

            if (device is not null)
            {
                ClearActiveBackendSource();
                _wasExternalPause = false;
            }

            _sonosDevice = device;
            _sonosCurrentUri = null;
            _sonosStreamContent = null;
            _sonosDuration = null;
            _sonosPosition = TimeSpan.Zero;

            if (device is null)
            {
                StopSonosPolling();
                _sonosController?.Dispose();
                _sonosController = null;
                _localMediaServer?.Dispose();
                _localMediaServer = null;
            }
            else
            {
                _sonosController ??= new SonosController();
                StartSonosPolling();
            }
        }
        finally
        {
            TryEnqueueOnUi(() =>
            {
                CastTargetChanged?.Invoke(this, EventArgs.Empty);
                SonosSpeakerVolumeChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        if (resume)
        {
            Play();
        }
    }

    /// <summary>Starts the active source on the speaker. Called from <see cref="Play"/> under a fresh play-attempt token.</summary>
    private async Task PlayOnSonosAsync(CancellationToken cancellationToken)
    {
        int session = Interlocked.Increment(ref _sonosSession);
        SonosDevice? device = _sonosDevice;
        SonosController? controller = _sonosController;
        string? streamUrl = _streamUrl;
        AudioSourceKind kind = _activeSourceKind;

        if (device is null || controller is null || string.IsNullOrWhiteSpace(streamUrl))
        {
            return;
        }

        _sonosUserStopped = false;
        bool resumingPaused = _sonosState == SonosPlaybackState.Paused;
        SetSonosState(SonosPlaybackState.Starting);

        try
        {
            string sourceUrl = streamUrl;
            string title = _currentStationName ?? "Traydio";
            string? albumArt = _currentStationFaviconUrl;

            if (kind == AudioSourceKind.Files)
            {
                string path = new Uri(streamUrl).LocalPath;
                _localMediaServer ??= new LocalMediaHttpServer();
                sourceUrl = _localMediaServer.Register(path, device.Address);
                title = Path.GetFileNameWithoutExtension(path);
                albumArt = null; // a favicon is a local path for these; nothing the speaker can fetch
            }

            var candidates = SonosPolicy.TransportUriCandidates(sourceUrl, kind);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("There is no address to send to the speaker.");
            }

            bool needsUri = !(resumingPaused && string.Equals(_sonosCurrentUri, candidates[0], StringComparison.Ordinal));
            if (needsUri)
            {
                SonosActionException? rejected = null;
                foreach (string uri in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        string didl = SonosPolicy.BuildDidlLite(title, albumArt, kind, uri);
                        await controller.SetTransportUriAsync(device, uri, didl, cancellationToken);
                        _sonosCurrentUri = uri;
                        rejected = null;
                        LogService.Info(SonosComponent, $"Speaker '{device.Name}' accepted {LogService.Redact(uri)}");
                        break;
                    }
                    catch (SonosActionException ex) when (ex.ErrorCode is 714 or 716 or 402)
                    {
                        // Wrong container for this address; the next candidate may suit it.
                        LogService.Warn(SonosComponent, $"Speaker rejected {LogService.Redact(uri)} (UPnP {ex.ErrorCode}); trying the next form");
                        rejected = ex;
                    }
                }

                if (rejected is not null)
                {
                    throw rejected;
                }
            }

            await controller.PlayAsync(device, cancellationToken);

            // Play returns before the player has fetched anything. Watch the transport until
            // it reports PLAYING, or give up when it settles on STOPPED (a stream it cannot
            // decode) or never gets there at all.
            DateTime started = DateTime.UtcNow;
            bool confirmed = false;
            while (DateTime.UtcNow - started < SonosStartTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(SonosPollWhileStarting, cancellationToken);

                SonosTransportState transport = await controller.GetTransportStateAsync(device, cancellationToken);
                if (transport == SonosTransportState.Playing)
                {
                    confirmed = true;
                    break;
                }

                if (transport is SonosTransportState.Stopped or SonosTransportState.NoMediaPresent &&
                    DateTime.UtcNow - started > SonosStoppedGrace)
                {
                    throw new SonosActionException("Play", null,
                        string.Format(
                            LocalizationService.GetString("Sonos_StoppedUnexpectedly", "{0} stopped playing the stream."),
                            device.Name));
                }
            }

            if (!confirmed)
            {
                throw new SonosActionException("Play", null,
                    string.Format(
                        LocalizationService.GetString("Sonos_StartTimeout", "{0} did not start playing in time."),
                        device.Name));
            }

            if (session != Volatile.Read(ref _sonosSession))
            {
                return; // superseded by a pause or another play while confirming
            }

            _hasPlayedOnce = true;
            _sonosPosition = TimeSpan.Zero;
            _sonosPositionSampledUtc = DateTime.UtcNow;
            SetSonosState(SonosPlaybackState.Playing);
            LogService.Info(SonosComponent, $"Speaker '{device.Name}' is playing {LogService.Redact(streamUrl)}");

            TryEnqueueOnUi(() =>
            {
                if (_activeSourceKind == AudioSourceKind.Files)
                {
                    // The once-per-track tag read works from the file, not the engine.
                    StartMetadataForActiveBackend();
                }

                ScheduleSystemMediaTransportControlsUpdate();
            });
        }
        catch (OperationCanceledException)
        {
            // A pause or a newer play cancelled this attempt; whoever did so set the state.
        }
        catch (Exception ex)
        {
            if (session != Volatile.Read(ref _sonosSession))
            {
                return;
            }

            LogService.Error(SonosComponent, $"Playing on Sonos '{device.Name}' failed", ex);
            Debug.WriteLine($"[RadioPlayerService] Sonos play failed: {ex}");
            SetSonosState(SonosPlaybackState.Idle);

            string detail = ex is SonosActionException { ErrorCode: null } plain
                ? plain.Message
                : string.Format(
                    LocalizationService.GetString("Sonos_ReportedDetail", "{0} reported: {1}"),
                    device.Name,
                    ex.Message);
            ReportPlaybackFailure(detail);
        }
    }

    /// <summary>Pauses (a local track) or stops (a live stream) the speaker. Called from <see cref="Pause"/>.</summary>
    private void PauseOnSonos()
    {
        SonosDevice? device = _sonosDevice;
        SonosController? controller = _sonosController;
        if (device is null || controller is null)
        {
            return;
        }

        int session = Interlocked.Increment(ref _sonosSession);
        _sonosUserStopped = true;
        CancelPendingPlayAttempt();

        TryEnqueueOnUi(() => PlaybackPausedByUser?.Invoke(this, EventArgs.Empty));
        _watchdog.NotifyUserIntentionToPause();
        SetManualBuffering(false);

        bool isLocalTrack = _activeSourceKind == AudioSourceKind.Files;
        SetSonosState(isLocalTrack ? SonosPlaybackState.Paused : SonosPlaybackState.Idle);
        if (!isLocalTrack)
        {
            _sonosCurrentUri = null;
            _sonosStreamContent = null;
            StopMetadata();
        }

        LogService.Info(SonosComponent, $"{(isLocalTrack ? "Pausing" : "Stopping")} Sonos '{device.Name}'");

        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(8));
                if (isLocalTrack)
                {
                    try
                    {
                        await controller.PauseAsync(device, cts.Token);
                    }
                    catch (SonosActionException ex) when (ex.ErrorCode == 701)
                    {
                        // Nothing to pause (already stopped); make sure it is quiet anyway.
                        await controller.StopAsync(device, cts.Token);
                    }
                }
                else
                {
                    await controller.StopAsync(device, cts.Token);
                }
            }
            catch (Exception ex)
            {
                if (session == Volatile.Read(ref _sonosSession))
                {
                    LogService.Warn(SonosComponent, $"Could not stop Sonos '{device.Name}': {ex.Message}");
                }
            }
        });
    }

    private void SeekOnSonos(TimeSpan position)
    {
        SonosDevice? device = _sonosDevice;
        SonosController? controller = _sonosController;
        if (device is null || controller is null || _activeSourceKind != AudioSourceKind.Files)
        {
            return;
        }

        _sonosPosition = position;
        _sonosPositionSampledUtc = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(8));
                await controller.SeekAsync(device, position, cts.Token);
            }
            catch (Exception ex)
            {
                LogService.Warn(SonosComponent, $"Seek on Sonos '{device.Name}' failed: {ex.Message}");
            }
        });
    }

    private TimeSpan SonosPosition
    {
        get
        {
            TimeSpan position = _sonosPosition;
            if (_sonosState == SonosPlaybackState.Playing)
            {
                position += DateTime.UtcNow - _sonosPositionSampledUtc;
            }

            if (_sonosDuration is { } duration && position > duration)
            {
                position = duration;
            }

            return position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }
    }

    private TimeSpan? SonosDuration => _activeSourceKind == AudioSourceKind.Files ? _sonosDuration : null;

    private void SetSonosState(SonosPlaybackState state)
    {
        SonosPlaybackState previous = _sonosState;
        if (previous == state)
        {
            return;
        }

        _sonosState = state;
        bool wasPlaying = previous == SonosPlaybackState.Playing;
        bool isPlaying = state == SonosPlaybackState.Playing;
        bool wasBuffering = previous == SonosPlaybackState.Starting;
        bool isBuffering = state == SonosPlaybackState.Starting;

        TryEnqueueOnUi(() =>
        {
            if (wasBuffering != isBuffering)
            {
                BufferingStateChanged?.Invoke(this, isBuffering);
            }

            if (wasPlaying != isPlaying)
            {
                PlaybackStateChanged?.Invoke(this, isPlaying);
            }

            ScheduleSystemMediaTransportControlsUpdate();
        });
    }

    private void StartSonosPolling()
    {
        StopSonosPolling();
        CancellationTokenSource cts = new();
        _sonosPollCts = cts;
        _ = Task.Run(() => PollSonosAsync(cts.Token), CancellationToken.None);
    }

    private void StopSonosPolling()
    {
        CancellationTokenSource? cts = _sonosPollCts;
        _sonosPollCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    /// Keeps the app's picture of the speaker current: transport state (someone may pause
    /// or resume it from the Sonos app), position for the scrub bar, the ICY title of a radio
    /// stream, and the speaker's volume. Runs for as long as a Sonos target is set.
    /// </summary>
    private async Task PollSonosAsync(CancellationToken cancellationToken)
    {
        int tick = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SonosDevice? device = _sonosDevice;
            SonosController? controller = _sonosController;
            if (device is null || controller is null)
            {
                return;
            }

            SonosPlaybackState state = _sonosState;
            try
            {
                if (state is SonosPlaybackState.Playing or SonosPlaybackState.Paused)
                {
                    int session = Volatile.Read(ref _sonosSession);
                    SonosTransportState transport = await controller.GetTransportStateAsync(device, cancellationToken);
                    if (session == Volatile.Read(ref _sonosSession) && _sonosState == state)
                    {
                        await ApplySonosTransportAsync(device, controller, state, transport, cancellationToken);
                    }
                }

                if (tick % 2 == 0)
                {
                    await RefreshSonosVolumeAsync(device, controller, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogService.Warn(SonosComponent, $"Polling Sonos '{device.Name}' failed: {ex.Message}");
            }

            tick++;
            try
            {
                await Task.Delay(state == SonosPlaybackState.Playing ? SonosPollWhilePlaying : SonosPollWhileIdle, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ApplySonosTransportAsync(
        SonosDevice device,
        SonosController controller,
        SonosPlaybackState state,
        SonosTransportState transport,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case SonosPlaybackState.Playing:
                switch (transport)
                {
                    case SonosTransportState.Playing:
                        SonosPositionInfo info = await controller.GetPositionInfoAsync(device, cancellationToken);
                        _sonosPosition = info.Position;
                        _sonosPositionSampledUtc = DateTime.UtcNow;
                        _sonosDuration = info.Duration;
                        PublishSonosStreamContent(info.StreamContent);
                        break;

                    case SonosTransportState.PausedPlayback:
                        LogService.Info(SonosComponent, $"Sonos '{device.Name}' was paused from elsewhere");
                        SetSonosState(SonosPlaybackState.Paused);
                        break;

                    case SonosTransportState.Stopped:
                    case SonosTransportState.NoMediaPresent:
                        HandleSonosStoppedRemotely(device, transport);
                        break;
                }

                break;

            case SonosPlaybackState.Paused:
                if (transport == SonosTransportState.Playing)
                {
                    LogService.Info(SonosComponent, $"Sonos '{device.Name}' was resumed from elsewhere");
                    _sonosUserStopped = false;
                    _sonosPositionSampledUtc = DateTime.UtcNow;
                    SetSonosState(SonosPlaybackState.Playing);
                }
                else if (transport is SonosTransportState.Stopped or SonosTransportState.NoMediaPresent)
                {
                    _sonosCurrentUri = null;
                    SetSonosState(SonosPlaybackState.Idle);
                }

                break;
        }
    }

    /// <summary>
    /// The speaker stopped without being asked to by this app. For a local track that is the
    /// end of the file and the folder moves on; for a radio stream it means the stream
    /// dropped (or someone stopped it from the Sonos app), which is reported like any other
    /// playback failure.
    /// </summary>
    private void HandleSonosStoppedRemotely(SonosDevice device, SonosTransportState transport)
    {
        if (_sonosUserStopped)
        {
            return;
        }

        _sonosCurrentUri = null;
        if (SonosPolicy.IsNaturalEnd(_activeSourceKind, transport))
        {
            LogService.Info(SonosComponent, $"Sonos '{device.Name}' reached the end of the track");
            SetSonosState(SonosPlaybackState.Idle);
            OnBackendPlaybackEnded(this, EventArgs.Empty);
            return;
        }

        LogService.Warn(SonosComponent, $"Sonos '{device.Name}' stopped on its own ({transport})");
        SetSonosState(SonosPlaybackState.Idle);
        TryEnqueueOnUi(() =>
        {
            StopMetadata();
            ReportPlaybackFailure(string.Format(
                LocalizationService.GetString("Sonos_StoppedUnexpectedly", "{0} stopped playing the stream."),
                device.Name));
        });
    }

    /// <summary>
    /// The speaker relays the stream's ICY title in its position report, which stands in
    /// for the ICY reader this PC would run if it were fetching the stream itself.
    /// </summary>
    private void PublishSonosStreamContent(string? streamContent)
    {
        if (_activeSourceKind != AudioSourceKind.Radio)
        {
            return;
        }

        if (string.Equals(streamContent, _sonosStreamContent, StringComparison.Ordinal))
        {
            return;
        }

        _sonosStreamContent = streamContent;
        if (string.IsNullOrWhiteSpace(streamContent))
        {
            return;
        }

        StreamMetadata metadata = new() { StreamTitle = streamContent };
        StreamMetadataService.ParseArtistAndTitle(metadata);
        _publishGate.Submit(metadata);
    }

    private async Task RefreshSonosVolumeAsync(SonosDevice device, SonosController controller, CancellationToken cancellationToken)
    {
        // A reading taken while the user is dragging the slider would land after their
        // change and snap the thumb back, so recent local changes win over the speaker.
        if (DateTime.UtcNow - _sonosVolumeTouchedUtc < SonosVolumeEchoGuard)
        {
            return;
        }

        int volume = await controller.GetVolumeAsync(device, cancellationToken);
        bool muted = await controller.GetMuteAsync(device, cancellationToken);

        if (DateTime.UtcNow - _sonosVolumeTouchedUtc < SonosVolumeEchoGuard)
        {
            return;
        }

        if (volume == _sonosSpeakerVolume && muted == _isSonosSpeakerMuted)
        {
            return;
        }

        _sonosSpeakerVolume = volume;
        _isSonosSpeakerMuted = muted;
        TryEnqueueOnUi(() => SonosSpeakerVolumeChanged?.Invoke(this, EventArgs.Empty));
    }

    private void QueueSonosVolumeSend(bool volume, bool mute)
    {
        lock (_sonosVolumeLock)
        {
            _sonosVolumeDirty |= volume;
            _sonosMuteDirty |= mute;
            if (_sonosVolumeSendRunning)
            {
                return;
            }

            _sonosVolumeSendRunning = true;
        }

        _ = Task.Run(SendSonosVolumeAsync);
    }

    /// <summary>One request in flight at a time; whatever changed while it was out goes in the next one.</summary>
    private async Task SendSonosVolumeAsync()
    {
        while (true)
        {
            bool sendVolume;
            bool sendMute;
            lock (_sonosVolumeLock)
            {
                sendVolume = _sonosVolumeDirty;
                sendMute = _sonosMuteDirty;
                _sonosVolumeDirty = false;
                _sonosMuteDirty = false;
                if (!sendVolume && !sendMute)
                {
                    _sonosVolumeSendRunning = false;
                    return;
                }
            }

            SonosDevice? device = _sonosDevice;
            SonosController? controller = _sonosController;
            if (device is null || controller is null)
            {
                lock (_sonosVolumeLock)
                {
                    _sonosVolumeSendRunning = false;
                }

                return;
            }

            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(8));
                if (sendVolume)
                {
                    await controller.SetVolumeAsync(device, _sonosSpeakerVolume, cts.Token);
                }

                if (sendMute)
                {
                    await controller.SetMuteAsync(device, _isSonosSpeakerMuted, cts.Token);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn(SonosComponent, $"Setting volume on Sonos '{device.Name}' failed: {ex.Message}");
            }

            // The player takes a moment to report the new level back; hold the poll off so
            // it cannot echo the previous value over what the user just set.
            _sonosVolumeTouchedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Ends the speaker session without changing the target: stops polling and, optionally, the speaker itself.</summary>
    private async Task StopSonosSessionAsync(bool stopSpeaker, CancellationToken cancellationToken)
    {
        SonosDevice? device = _sonosDevice;
        SonosController? controller = _sonosController;

        Interlocked.Increment(ref _sonosSession);
        CancelPendingPlayAttempt();
        StopSonosPolling();
        _sonosUserStopped = true;

        bool wasActive = _sonosState != SonosPlaybackState.Idle;
        SetSonosState(SonosPlaybackState.Idle);
        _sonosCurrentUri = null;
        _sonosStreamContent = null;

        if (_activeSourceKind == AudioSourceKind.Radio)
        {
            StopMetadata();
        }

        if (stopSpeaker && wasActive && device is not null && controller is not null)
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await controller.StopAsync(device, timeout.Token);
            }
            catch (Exception ex)
            {
                LogService.Warn(SonosComponent, $"Could not stop Sonos '{device.Name}' while leaving it: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// App shutdown. A speaker fetching a radio stream would otherwise carry on after this
    /// PC's player is gone, so it is told to stop, but only for as long as a quick request
    /// takes - exit must not hang on an unreachable player.
    /// </summary>
    private void DisposeSonosTarget()
    {
        SonosDevice? device = _sonosDevice;
        SonosController? controller = _sonosController;
        bool wasActive = _sonosState != SonosPlaybackState.Idle;

        Interlocked.Increment(ref _sonosSession);
        StopSonosPolling();
        _sonosDevice = null;
        _sonosState = SonosPlaybackState.Idle;

        if (device is not null && controller is not null && wasActive)
        {
            try
            {
                using CancellationTokenSource cts = new(SonosStopOnExitTimeout);
                controller.StopAsync(device, cts.Token).Wait(SonosStopOnExitTimeout);
            }
            catch (Exception ex)
            {
                LogService.Warn(SonosComponent, $"Could not stop Sonos '{device.Name}' on exit: {ex.Message}");
            }
        }

        controller?.Dispose();
        _sonosController = null;
        _localMediaServer?.Dispose();
        _localMediaServer = null;
    }
}
