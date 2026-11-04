using LibVLCSharp.Shared;
using System;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services.Playback;

namespace Trdo.Services;

/// <summary>
/// Casting: sending the audio to a renderer on the local network instead of this PC's
/// output. Only LibVLC can do that, so a cast target pins playback to the LibVLC engine for
/// as long as it is set (see <see cref="PlaybackEngineSelector.RequireLibVlc"/>).
/// </summary>
public sealed partial class RadioPlayerService
{
    private RendererItem? _castRenderer;
    private string? _castTargetName;
    private CastAttempt? _pendingCastAttempt;
    private readonly CastIdleTimeout _castIdleTimeout = new();
    private Timer? _castIdleTimer;
    private bool _isEndingIdleCast;

    /// <summary>
    /// Raised on the UI thread after the cast target changes, including back to local output.
    /// </summary>
    public event EventHandler? CastTargetChanged;
    public event EventHandler<CastAttempt>? CastPlaybackConfirmed;

    /// <summary>
    /// Whether the cast picker is worth opening. Chromecast needs LibVLC, but a Sonos player
    /// is driven over plain HTTP, so there is always something to search for.
    /// </summary>
    public bool CanCast => true;

    /// <summary>Whether audio is going to a network device of either kind: a LibVLC renderer or a Sonos player.</summary>
    public bool IsCasting => _castRenderer is not null || _sonosDevice is not null;

    /// <summary>Display name of the device receiving the audio, or null when playing locally.</summary>
    public string? CastTargetName => _sonosDevice?.Name ?? _castTargetName;

    /// <summary>
    /// Whether <paramref name="device"/> is the device audio is going to right now. Compares
    /// identity (the Sonos target, or the renderer's native pointer), not display names, which
    /// can repeat across protocols and change while casting.
    /// </summary>
    internal bool IsCastTarget(CastDevice? device)
    {
        if (device is null)
            return false;

        if (device.Sonos is { } sonos)
            return _sonosDevice is { } current && sonos.SameTargetAs(current);

        return device.Item is { } item && _castRenderer is { } renderer &&
               item.NativeReference != IntPtr.Zero && item.NativeReference == renderer.NativeReference;
    }

    /// <summary>
    /// Routes playback to <paramref name="renderer"/>, or back to this PC's output when null.
    /// Takes ownership of the item, which must be a wrapper of its own (from
    /// <see cref="CastRendererDiscovery.Claim"/>), never one still held by a discovery list.
    /// If something is playing it is re-opened on the new output; otherwise the change simply
    /// applies to the next play. White noise is generated locally and is not affected.
    /// </summary>
    public Task SetCastTargetAsync(RendererItem? renderer, string? displayName, CancellationToken cancellationToken = default, CastAttempt? attempt = null)
    {
        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            return SetOwnedCastTargetAsync(renderer, displayName, cancellationToken, attempt);
        }

        TaskCompletionSource<bool> tcs = new();
        if (!_uiQueue.TryEnqueue(async () =>
        {
            try
            {
                await SetOwnedCastTargetAsync(renderer, displayName, cancellationToken, attempt);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            renderer?.Dispose();
            tcs.SetException(new InvalidOperationException("The playback dispatcher is unavailable."));
        }
        return tcs.Task;
    }

    private async Task SetOwnedCastTargetAsync(RendererItem? renderer, string? displayName, CancellationToken cancellationToken, CastAttempt? attempt)
    {
        try
        {
            await SetCastTargetInternalAsync(renderer, displayName, cancellationToken, resumePlayback: true, attempt);
        }
        catch
        {
            if (!ReferenceEquals(renderer, _castRenderer))
                renderer?.Dispose();
            throw;
        }
    }

    private async Task SetCastTargetInternalAsync(RendererItem? renderer, string? displayName, CancellationToken cancellationToken, bool resumePlayback, CastAttempt? attempt = null)
    {
        if (renderer is null && _castRenderer is null)
        {
            // "Stop casting" with a Sonos target set means that one.
            if (_sonosDevice is not null)
            {
                await SetSonosTargetInternalAsync(null, cancellationToken, resumePlayback);
            }

            return;
        }

        if (_libVlcBackend is null)
        {
            throw new InvalidOperationException("Casting requires the LibVLC engine, which is not available on this machine.");
        }

        if (renderer is not null && _castRenderer is not null &&
            renderer.NativeReference == _castRenderer.NativeReference)
        {
            // Same device picked again: nothing to change, and the duplicate handle is surplus.
            renderer.Dispose();
            _pendingCastAttempt = attempt;
            if (_libVlcBackend.IsPlaying)
                ConfirmCastPlayback(attempt);
            return;
        }

        // Decided before a Sonos session is torn down, because IsPlaying reads the speaker
        // while one is set and would read false once it is gone.
        bool resume = resumePlayback &&
                      IsPlaybackWanted &&
                      _activeSourceKind != AudioSourceKind.WhiteNoise &&
                      !string.IsNullOrWhiteSpace(_streamUrl);

        if (renderer is not null && _sonosDevice is not null)
        {
            // A Sonos and a Chromecast cannot both be the target. The speaker is stopped
            // here and the stream re-opened on LibVLC below, so no local resume in between.
            await SetSonosTargetInternalAsync(null, cancellationToken, resumePlayback: false);
        }

        RendererItem? previous = _castRenderer;
        string? previousName = _castTargetName;
        CastAttempt? previousAttempt = _pendingCastAttempt;
        string? name = renderer is null ? null : CastDevicePolicy.DisplayName(displayName ?? renderer.Name);

        LogService.Info("RadioPlayerService", renderer is null
            ? $"Stopping cast to '{_castTargetName}'; audio returns to this PC"
            : $"Casting to '{name}' ({CastDevicePolicy.DescribeKind(renderer.Type)})");

        _castRenderer = renderer;
        _castTargetName = name;
        _pendingCastAttempt = attempt;
        _playbackEngineSelector.RequireLibVlc = renderer is not null;
        bool applied = false;
        void ApplyRenderer()
        {
            ApplyCastRenderer();
            applied = true;
        }

        try
        {
            if (resume)
            {
                // The rebuild applies the renderer while the player is idle and then re-opens
                // the stream on LibVLC, so LibVLC never has to hot-swap outputs under a live
                // input, and a stream that was on the native engine moves across.
                bool started = await RebuildPlaybackPipelineInternalAsync(
                    recycleBackend: false,
                    cancellationToken,
                    reconfigure: ApplyRenderer);
                if (!started)
                    throw new InvalidOperationException(LocalizationService.GetString(
                        "CastDeviceDialog_StartFailed", "The device did not start playing."));
            }
            else
            {
                ApplyRenderer();

                // Drop any prepared-but-paused source on either engine. The play path resumes
                // an existing source as-is, which would keep a paused station on the old
                // output (and on the native engine) when the user presses play again.
                _nativeBackend.ClearSource();
                _libVlcBackend.ClearSource();
                _player.Source = null;
                _wasExternalPause = false;
            }
        }
        catch
        {
            if (!applied)
            {
                _castRenderer = previous;
                _castTargetName = previousName;
                _pendingCastAttempt = previousAttempt;
                _playbackEngineSelector.RequireLibVlc = previous is not null;
            }
            else
            {
                _pendingCastAttempt = null;
            }
            throw;
        }
        finally
        {
            // A rejected setup keeps the previous target; a completed switch releases it.
            if (!ReferenceEquals(previous, _castRenderer))
                previous?.Dispose();
            TryEnqueueOnUi(() => CastTargetChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private void ApplyCastRenderer()
    {
        if (_libVlcBackend is not null && !_libVlcBackend.SetRenderer(_castRenderer))
        {
            LogService.Warn("RadioPlayerService",
                $"LibVLC rejected the cast renderer '{_castTargetName ?? "(local output)"}'");
            throw new InvalidOperationException(LocalizationService.GetString(
                "CastDeviceDialog_RendererRejected", "The playback engine rejected the cast device."));
        }
    }

    private void ConfirmCastPlayback(CastAttempt? attempt)
    {
        if (attempt is null || !ReferenceEquals(attempt, _pendingCastAttempt) ||
            _activeSourceKind == AudioSourceKind.WhiteNoise || !IsCasting)
            return;

        _pendingCastAttempt = null;
        CastPlaybackConfirmed?.Invoke(this, attempt);
    }

    private void OnRendererPlaybackStarted(object? sender, RendererItem renderer)
    {
        CastAttempt? attempt = _pendingCastAttempt;
        TryEnqueueOnUi(() =>
        {
            if (ReferenceEquals(renderer, _castRenderer))
                ConfirmCastPlayback(attempt);
        });
    }

    private void InitializeCastIdleTimeout()
    {
        CastTargetChanged += OnCastTargetChangedForIdleTimeout;
        PlaybackStateChanged += OnCastPlaybackActivityChanged;
        BufferingStateChanged += OnCastPlaybackActivityChanged;
        SettingsService.CastIdleTimeoutChanged += OnCastIdleTimeoutChanged;
    }

    private void OnCastTargetChangedForIdleTimeout(object? sender, EventArgs e)
    {
        _castIdleTimeout.Reset();
        _ = CheckCastIdleTimeoutAsync();
    }

    private void OnCastPlaybackActivityChanged(object? sender, bool isActive) =>
        _ = CheckCastIdleTimeoutAsync();

    private void OnCastIdleTimeoutChanged(object? sender, EventArgs e) =>
        TryEnqueueOnUi(() => _ = CheckCastIdleTimeoutAsync());

    private async Task CheckCastIdleTimeoutAsync()
    {
        if (_isDisposed || _isEndingIdleCast)
            return;

        try
        {
            int minutes = SettingsService.CastIdleTimeoutMinutes;
            if (IsCasting && minutes > 0)
            {
                _castIdleTimer ??= new Timer(
                    state => TryEnqueueOnUi(() => _ = CheckCastIdleTimeoutAsync()),
                    null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            else
            {
                _castIdleTimer?.Dispose();
                _castIdleTimer = null;
            }

            // A play token can outlive playback, including pauses from the speaker's own app.
            bool isActive = UsesSonosForActiveSource
                ? _sonosState is SonosPlaybackState.Playing or SonosPlaybackState.Starting
                : IsPlaying || (!_isUserPaused && IsBuffering);
            if (!_castIdleTimeout.ShouldEndCast(IsCasting, isActive, minutes, DateTimeOffset.UtcNow))
                return;

            _isEndingIdleCast = true;
            LogService.Info("RadioPlayerService",
                $"Ending cast to '{CastTargetName}' after {minutes} minutes without playback");
            await SetCastTargetInternalAsync(null, null, CancellationToken.None, resumePlayback: false);
        }
        catch (Exception ex)
        {
            _castIdleTimeout.Reset();
            LogService.Error("RadioPlayerService", "Could not end idle casting", ex);
            TryEnqueueOnUi(() => PlaybackFailed?.Invoke(this, LocalizationService.GetString(
                "CastIdleTimeout_EndFailed", "Couldn't stop casting automatically. Try Stop casting.")));
        }
        finally
        {
            _isEndingIdleCast = false;
        }
    }

    private void DisposeCastTarget()
    {
        SettingsService.CastIdleTimeoutChanged -= OnCastIdleTimeoutChanged;
        CastTargetChanged -= OnCastTargetChangedForIdleTimeout;
        PlaybackStateChanged -= OnCastPlaybackActivityChanged;
        BufferingStateChanged -= OnCastPlaybackActivityChanged;
        _castIdleTimer?.Dispose();
        _castIdleTimer = null;
        _castRenderer?.Dispose();
        _castRenderer = null;
        _castTargetName = null;
        _pendingCastAttempt = null;
    }
}
