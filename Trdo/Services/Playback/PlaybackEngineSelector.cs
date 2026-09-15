using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Trdo.Services.Playback;

public sealed partial class PlaybackEngineSelector : IDisposable
{
    private readonly NativePlaybackBackend _nativeBackend;
    private readonly LibVlcPlaybackBackend? _libVlcBackend;
    private readonly EngineHealthStore _engineHealth;
    private IPlaybackBackend _activeBackend;

    public PlaybackEngineSelector(
        NativePlaybackBackend nativeBackend,
        LibVlcPlaybackBackend? libVlcBackend,
        EngineHealthStore? engineHealth = null)
    {
        _nativeBackend = nativeBackend;
        _libVlcBackend = libVlcBackend;
        _activeBackend = nativeBackend;
        _engineHealth = engineHealth ?? new EngineHealthStore(new LocalSettingsEngineHealthStorage());

        int removed = _engineHealth.RemoveLegacyRecords();
        if (removed > 0)
        {
            LogService.Info("PlaybackEngineSelector", $"Removed {removed} legacy engine preference(s)");
        }
    }

    public IPlaybackBackend ActiveBackend => _activeBackend;

    public PlaybackBackendKind ActiveBackendKind => _activeBackend.Kind;

    /// <summary>Whether the LibVLC engine is available at all on this machine/architecture.</summary>
    public bool IsLibVlcAvailable => _libVlcBackend is not null;

    /// <summary>
    /// Pins playback to LibVLC regardless of the engine mode or the per-stream memory. Set
    /// while a cast target is active: only LibVLC can send audio to a network renderer, so
    /// falling back to the native engine would quietly move the sound back to this PC.
    /// Ignored when LibVLC is unavailable, which the caller rules out before casting.
    /// </summary>
    public bool RequireLibVlc { get; set; }

    /// <summary>The per-stream engine memory, for diagnostics and the user-facing reset.</summary>
    public EngineHealthStore EngineHealth => _engineHealth;

    public event EventHandler<PlaybackBackendKind>? ActiveBackendChanged;

    public static PlaybackEngineMode GetEngineMode() => SettingsService.PlaybackEngineMode;

    public static void SetEngineMode(PlaybackEngineMode mode) => SettingsService.PlaybackEngineMode = mode;

    /// <summary>
    /// Describes what the app has learned about this stream, for the log and the
    /// diagnostics report. Returns a readable "nothing yet" rather than null.
    /// </summary>
    public string DescribeEngineHealth(string streamUrl)
    {
        EngineHealthRecord? record = _engineHealth.GetRecord(streamUrl);
        return record is null ? "no engine history for this stream" : record.ToString();
    }

    public async Task<PlaybackPrepareResult> PrepareAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        if (RequireLibVlc && _libVlcBackend is not null)
        {
            LogService.Info("PlaybackEngineSelector",
                $"LibVLC required (casting) -> LibVLC only for {LogService.Redact(streamUrl)}");
            return await PrepareWithBackendAsync(_libVlcBackend, streamUrl, usedFallback: false, cancellationToken);
        }

        PlaybackEngineMode mode = GetEngineMode();

        if (mode == PlaybackEngineMode.NativeOnly || _libVlcBackend is null)
        {
            LogService.Info("PlaybackEngineSelector",
                $"Mode={mode}, LibVLC available={_libVlcBackend is not null} -> Native only for {LogService.Redact(streamUrl)}");
            return await PrepareNativeAsync(streamUrl, cancellationToken);
        }

        bool nativeFirst = mode == PlaybackEngineMode.NativePreferred;
        string reason = $"mode {mode}";

        // Engine memory applies in every mode that allows both engines, not just Auto. A
        // station proven to need one engine should start there even when the user has
        // expressed a general preference for the other - the mode is a default, not a veto.
        PlaybackBackendKind? remembered = _engineHealth.GetPreferred(streamUrl);
        if (remembered is not null)
        {
            nativeFirst = remembered == PlaybackBackendKind.Native;
            reason = $"remembered engine ({_engineHealth.GetRecord(streamUrl)})";
        }

        IPlaybackBackend first = nativeFirst ? _nativeBackend : _libVlcBackend;
        IPlaybackBackend second = nativeFirst ? _libVlcBackend : _nativeBackend;

        LogService.Info("PlaybackEngineSelector",
            $"Trying {first.Kind} first for {LogService.Redact(streamUrl)} ({reason})");

        // Note: preparing successfully is not evidence a backend can play this stream -
        // LibVlcPlaybackBackend.PrepareAsync always succeeds. The preference is only
        // written once playback is actually confirmed, via ConfirmBackendHealthy.
        PlaybackPrepareResult firstResult = await PrepareWithBackendAsync(first, streamUrl, usedFallback: false, cancellationToken);
        if (firstResult.Success)
        {
            LogService.Info("PlaybackEngineSelector", $"{first.Kind} prepared successfully");
            return firstResult;
        }

        LogService.Warn("PlaybackEngineSelector",
            $"{first.Kind} prepare failed ({firstResult.ErrorMessage}); falling back to {second.Kind}");
        Debug.WriteLine($"[PlaybackEngineSelector] {first.Kind} prepare failed, falling back to {second.Kind}: {firstResult.ErrorMessage}");

        // A prepare that fails outright is real evidence against this engine for this stream.
        _engineHealth.RecordFailure(streamUrl, first.Kind);

        PlaybackPrepareResult fallbackResult = await PrepareWithBackendAsync(second, streamUrl, usedFallback: true, cancellationToken);
        if (fallbackResult.Success)
        {
            LogService.Warn("PlaybackEngineSelector", $"Fallback to {second.Kind} succeeded");
        }
        else
        {
            LogService.Error("PlaybackEngineSelector",
                $"Both backends failed to prepare; last error: {fallbackResult.ErrorMessage}");
        }

        return fallbackResult;
    }

    public async Task<PlaybackPrepareResult> RetryWithFallbackAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        IPlaybackBackend? other = _activeBackend.Kind == PlaybackBackendKind.LibVlc
            ? _nativeBackend
            : _libVlcBackend;

        // While casting there is no other engine worth trying: re-preparing LibVLC is the
        // only retry that keeps the audio on the renderer.
        if (other is null || RequireLibVlc || GetEngineMode() == PlaybackEngineMode.NativeOnly)
        {
            return await PrepareAsync(streamUrl, cancellationToken);
        }

        LogService.Warn("PlaybackEngineSelector", $"Retrying with {other.Kind} fallback for {LogService.Redact(streamUrl)}");
        Debug.WriteLine($"[PlaybackEngineSelector] Retrying with {other.Kind} fallback");
        PlaybackPrepareResult result = await PrepareWithBackendAsync(other, streamUrl, usedFallback: true, cancellationToken);
        if (result.Success)
        {
            LogService.Info("PlaybackEngineSelector", $"{other.Kind} fallback retry succeeded");
        }
        else
        {
            LogService.Error("PlaybackEngineSelector", $"{other.Kind} fallback retry failed: {result.ErrorMessage}");
        }

        return result;
    }

    /// <summary>
    /// Records that a backend has actually played this stream, so it is tried first next time.
    /// Call this only on a confirmed playing transition - prepare success is not sufficient
    /// evidence, since the LibVLC backend's prepare cannot fail.
    /// </summary>
    public void ConfirmBackendHealthy(string streamUrl, PlaybackBackendKind backend)
    {
        if (_engineHealth.RecordSuccess(streamUrl, backend))
        {
            LogService.Info("PlaybackEngineSelector",
                $"Confirmed {backend} plays {LogService.Redact(streamUrl)}; remembering it");
        }
    }

    /// <summary>
    /// Records that a backend failed to play this stream. The engine memory decides whether
    /// that is enough to move the preference - a single failure usually is not, because
    /// stations go down for reasons that have nothing to do with the engine.
    /// </summary>
    /// <returns>The engine that will be tried first next time.</returns>
    public PlaybackBackendKind RecordBackendFailure(string streamUrl, PlaybackBackendKind backend)
    {
        PlaybackBackendKind preferred = _engineHealth.RecordFailure(streamUrl, backend);
        LogService.Warn("PlaybackEngineSelector",
            $"{backend} failed for {LogService.Redact(streamUrl)}; next attempt prefers {preferred} " +
            $"({_engineHealth.GetRecord(streamUrl)})");
        return preferred;
    }

    /// <summary>
    /// Records that a backend definitively cannot play this stream, so the next prepare starts
    /// with the other one. Used by the recovery ladder's backend-switch rung, which only runs
    /// after gentler recovery has already failed. Without this, a station that LibVLC can open
    /// but not play stays pinned to LibVLC forever.
    /// </summary>
    public void MarkBackendUnhealthy(string streamUrl, PlaybackBackendKind backend)
    {
        _engineHealth.MarkUnusable(streamUrl, backend);

        PlaybackBackendKind other = backend == PlaybackBackendKind.LibVlc
            ? PlaybackBackendKind.Native
            : PlaybackBackendKind.LibVlc;

        LogService.Warn("PlaybackEngineSelector",
            $"{backend} marked unusable for {LogService.Redact(streamUrl)}; preferring {other} next");
    }

    /// <summary>
    /// Waits for the given backend to show evidence it has opened the stream. Used to put a
    /// bound on an engine that neither plays nor reports an error - the failure mode that
    /// leaves the user staring at a silent player with nothing in the UI to explain it.
    /// </summary>
    public async Task<bool> WaitForBackendOpenAsync(
        PlaybackBackendKind backend,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_activeBackend.Kind != backend)
        {
            return true;
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasOpenEvidence())
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public Task<bool> WaitForNativeOpenAsync(CancellationToken cancellationToken, TimeSpan timeout) =>
        WaitForBackendOpenAsync(PlaybackBackendKind.Native, timeout, cancellationToken);

    private bool HasOpenEvidence()
    {
        try
        {
            if (_activeBackend.IsPlaying)
            {
                return true;
            }

            return _activeBackend.GetBufferedRanges().Count > 0 || _activeBackend.BufferingProgress > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PlaybackPrepareResult> PrepareNativeAsync(string streamUrl, CancellationToken cancellationToken)
    {
        SetActiveBackend(_nativeBackend);
        return await _nativeBackend.PrepareAsync(streamUrl, cancellationToken);
    }

    private async Task<PlaybackPrepareResult> PrepareWithBackendAsync(
        IPlaybackBackend? backend,
        string streamUrl,
        bool usedFallback,
        CancellationToken cancellationToken)
    {
        if (backend is null)
        {
            return PlaybackPrepareResult.Failed(PlaybackBackendKind.Native, "LibVLC backend is not available");
        }

        SetActiveBackend(backend);
        PlaybackPrepareResult result = await backend.PrepareAsync(streamUrl, cancellationToken);
        if (result.Success && usedFallback)
        {
            return PlaybackPrepareResult.Succeeded(backend.Kind, usedFallback: true);
        }

        return result;
    }

    private void SetActiveBackend(IPlaybackBackend backend)
    {
        if (ReferenceEquals(_activeBackend, backend))
        {
            return;
        }

        if (_activeBackend.Kind != backend.Kind)
        {
            _activeBackend.ClearSource();
        }

        _activeBackend = backend;
        ActiveBackendChanged?.Invoke(this, backend.Kind);
    }

    public void Dispose()
    {
        _nativeBackend.Dispose();
        _libVlcBackend?.Dispose();
        _activeBackend = _nativeBackend;
    }
}
