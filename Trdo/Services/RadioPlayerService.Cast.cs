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

    /// <summary>
    /// Raised on the UI thread after the cast target changes, including back to local output.
    /// </summary>
    public event EventHandler? CastTargetChanged;

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
    /// Routes playback to <paramref name="renderer"/>, or back to this PC's output when null.
    /// Takes ownership of the item. If something is playing it is re-opened on the new output;
    /// otherwise the change simply applies to the next play. White noise is generated locally
    /// and is not affected.
    /// </summary>
    public Task SetCastTargetAsync(RendererItem? renderer, string? displayName, CancellationToken cancellationToken = default)
    {
        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            return SetCastTargetInternalAsync(renderer, displayName, cancellationToken, resumePlayback: true);
        }

        TaskCompletionSource<bool> tcs = new();
        _uiQueue.TryEnqueue(async () =>
        {
            try
            {
                await SetCastTargetInternalAsync(renderer, displayName, cancellationToken, resumePlayback: true);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private async Task SetCastTargetInternalAsync(RendererItem? renderer, string? displayName, CancellationToken cancellationToken, bool resumePlayback)
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
            renderer?.Dispose();
            throw new InvalidOperationException("Casting requires the LibVLC engine, which is not available on this machine.");
        }

        if (renderer is not null && _castRenderer is not null &&
            renderer.NativeReference == _castRenderer.NativeReference)
        {
            // Same device picked again: nothing to change, and the duplicate handle is surplus.
            renderer.Dispose();
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
        string? name = renderer is null ? null : CastDevicePolicy.DisplayName(displayName ?? renderer.Name);

        LogService.Info("RadioPlayerService", renderer is null
            ? $"Stopping cast to '{_castTargetName}'; audio returns to this PC"
            : $"Casting to '{name}' ({CastDevicePolicy.DescribeKind(renderer.Type)})");

        _castRenderer = renderer;
        _castTargetName = name;
        _playbackEngineSelector.RequireLibVlc = renderer is not null;

        try
        {
            if (resume)
            {
                // The rebuild applies the renderer while the player is idle and then re-opens
                // the stream on LibVLC, so LibVLC never has to hot-swap outputs under a live
                // input, and a stream that was on the native engine moves across.
                await RebuildPlaybackPipelineInternalAsync(
                    recycleBackend: false,
                    cancellationToken,
                    reconfigure: ApplyCastRenderer);
            }
            else
            {
                ApplyCastRenderer();

                // Drop any prepared-but-paused source on either engine. The play path resumes
                // an existing source as-is, which would keep a paused station on the old
                // output (and on the native engine) when the user presses play again.
                _nativeBackend.ClearSource();
                _libVlcBackend.ClearSource();
                _player.Source = null;
                _wasExternalPause = false;
            }
        }
        finally
        {
            // LibVLC holds its own reference to whatever renderer the player is using, so the
            // previous wrapper can go regardless of how the switch went.
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
        }
    }

    private void DisposeCastTarget()
    {
        _castRenderer?.Dispose();
        _castRenderer = null;
        _castTargetName = null;
    }
}
