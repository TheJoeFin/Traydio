using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Trdo.Services.Playback;

/// <summary>
/// LibVLC-based playback fallback for streams Windows Media Foundation cannot play.
/// </summary>
public sealed partial class LibVlcPlaybackBackend : IPlaybackBackend
{
    private readonly LibVLC _libVlc;
    private readonly LibVlcLogCapture? _logCapture;
    private VlcMediaPlayer _mediaPlayer;
    private Media? _currentMedia;
    private bool _isBuffering;
    private string? _currentStreamUrl;
    private readonly object _eventChainLock = new();
    private Task _eventChain = Task.CompletedTask;
    private bool _isDisposed;

    public LibVlcPlaybackBackend(LibVLC libVlc, LibVlcLogCapture? logCapture = null)
    {
        _libVlc = libVlc;
        _logCapture = logCapture;
        _mediaPlayer = CreateMediaPlayer();
    }

    private VlcMediaPlayer CreateMediaPlayer()
    {
        VlcMediaPlayer player = new(_libVlc);

        player.Playing += OnPlayerPlaying;
        player.Paused += OnPlayerStopped;
        player.Stopped += OnPlayerStopped;
        player.EndReached += OnPlayerStopped;
        player.EndReached += OnPlayerEndReached;
        player.Buffering += OnPlayerBuffering;
        player.EncounteredError += OnPlayerEncounteredError;

        return player;
    }

    private void DetachMediaPlayer(VlcMediaPlayer player)
    {
        player.Playing -= OnPlayerPlaying;
        player.Paused -= OnPlayerStopped;
        player.Stopped -= OnPlayerStopped;
        player.EndReached -= OnPlayerStopped;
        player.EndReached -= OnPlayerEndReached;
        player.Buffering -= OnPlayerBuffering;
        player.EncounteredError -= OnPlayerEncounteredError;
    }

    // The event tells us the new state, so it is carried across to the dispatch rather than
    // re-read from the player there: by the time a queued Playing is delivered the player may
    // already have failed, and reporting that back would flip the UI to the wrong state.
    private void OnPlayerPlaying(object? sender, EventArgs e) => RaiseStateChanged(isPlaying: true);

    private void OnPlayerStopped(object? sender, EventArgs e) => RaiseStateChanged(isPlaying: false);

    // Kept separate from OnPlayerStopped: that one must keep mapping EndReached onto
    // PlaybackStateChanged(false) for radio (a dropped stream reads as "stopped"), while this
    // is the one genuinely new signal - "the item finished on its own" - that local music's
    // auto-advance needs and nothing else here provides.
    private void OnPlayerEndReached(object? sender, EventArgs e) =>
        RaiseOffVlcThread(() => PlaybackEnded?.Invoke(this, EventArgs.Empty));

    private void OnPlayerBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        // Cache genuinely dips below 100 under ordinary network jitter for a live stream even
        // while audio keeps playing smoothly from what is already buffered - LibVLC reports the
        // read-ahead fill level here, not whether anything audible is actually stalling. A cache
        // dip only means the stream is really buffering (and radio static should react to it) if
        // the player has actually left the Playing state; otherwise this fires many times a
        // second throughout ordinary playback and reads as near-continuous static.
        bool isBuffering = e.Cache < 100f && !_mediaPlayer.IsPlaying;

        // LibVLC reports the cache percentage on nearly every read tick, not just when it
        // actually changes - a jittery live stream can call this dozens of times a second even
        // though the derived isBuffering value never moves. Without this guard, every one of
        // those redundant ticks still spawns a dispatch and re-runs RadioStaticService's fade
        // logic, which is needless overhead on a hot path and a plausible source of its own
        // audio glitches. Only a genuine transition is worth telling anyone about.
        if (_isBuffering == isBuffering)
            return;

        _isBuffering = isBuffering;
        RaiseOffVlcThread(() => BufferingStateChanged?.Invoke(this, isBuffering));
    }

    private void OnPlayerEncounteredError(object? sender, EventArgs e)
    {
        // Read the reason here, while the capture still describes this attempt: the fallback
        // that follows re-prepares a backend and resets it. Everything that touches the media
        // player itself waits until the dispatch has left LibVLC's thread.
        string reason = DescribeLastError();

        Debug.WriteLine($"[LibVlcPlaybackBackend] EncounteredError: {reason}");
        _logCapture?.DumpTo("LibVLC playback error");

        RaiseOffVlcThread(() =>
        {
            LogService.Error("LibVlcPlaybackBackend",
                $"LibVLC failed on {LogService.Redact(_currentStreamUrl)}: {reason} (state={DescribeState()})");

            PlaybackFailed?.Invoke(this, new PlaybackFailureEventArgs(
                PlaybackBackendKind.LibVlc,
                reason,
                canRetryWithFallback: true));
        });
    }

    private void RaiseStateChanged(bool isPlaying)
    {
        bool isBuffering = _isBuffering;
        RaiseOffVlcThread(() =>
        {
            PlaybackStateChanged?.Invoke(this, isPlaying);
            BufferingStateChanged?.Invoke(this, isBuffering);
        });
    }

    /// <summary>
    /// Hands an event on from the thread pool rather than from the LibVLC thread that raised it.
    /// <para>
    /// LibVLC forbids calling back into libvlc from inside one of its event callbacks: the
    /// callback runs with the media player's lock held, so <c>Stop</c> or assigning
    /// <c>Media</c> waits on a lock only the caller could release, and the process deadlocks.
    /// Our subscribers do exactly that — a playback failure switches engines, which clears
    /// this player's source — so nothing LibVLC raises may reach them on LibVLC's own thread
    /// (see issue #109).
    /// </para>
    /// <para>
    /// Events are chained rather than each queued independently so they still arrive in the
    /// order LibVLC produced them; a Stopped overtaking the Playing before it would leave the
    /// UI showing the wrong state. Deferring them does open a window in which
    /// <see cref="Recycle"/> replaces the player, so the one that raised the event is captured
    /// here — this runs on the raising thread, before any recycle could have swapped it — and
    /// anything from a player we have since moved off is dropped rather than reported as the
    /// current one's state. LibVLCSharp's own <c>sender</c> is the event manager rather than
    /// the player, so it cannot be used for this.
    /// </para>
    /// </summary>
    private void RaiseOffVlcThread(Action raise)
    {
        VlcMediaPlayer origin = _mediaPlayer;

        lock (_eventChainLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _eventChain = _eventChain.ContinueWith(
                _ =>
                {
                    try
                    {
                        if (_isDisposed || !ReferenceEquals(origin, _mediaPlayer))
                        {
                            return;
                        }

                        raise();
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("LibVlcPlaybackBackend", "Error dispatching LibVLC event", ex);
                        Debug.WriteLine($"[LibVlcPlaybackBackend] Error dispatching LibVLC event: {ex}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The reason LibVLC gave for the most recent failure, taken from its native log.
    /// Falls back to a generic string when the log had nothing (older LibVLC builds, or a
    /// failure raised before any line was written).
    /// </summary>
    public string DescribeLastError() =>
        _logCapture?.BuildFailureReason() ?? "LibVLC playback error (no detail in the LibVLC log)";

    /// <summary>
    /// Writes LibVLC's retained log lines and the player's state to the app log under the
    /// given context, so a failure that produced no error event still leaves an explanation.
    /// </summary>
    public void DumpDiagnostics(string context)
    {
        LogService.Warn("LibVlcPlaybackBackend", $"{context}: state={DescribeState()}");
        _logCapture?.DumpTo(context);
    }

    /// <summary>The player's current state, for logs and failure reports.</summary>
    public string DescribeState()
    {
        try
        {
            return $"{_mediaPlayer.State}, buffering={_isBuffering}";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Disposes the current media player and creates a fresh one on the same LibVLC
    /// instance. Used by the recovery ladder when re-preparing the media alone hasn't
    /// restored playback and the player's internal pipeline is suspect.
    /// </summary>
    public void Recycle()
    {
        Debug.WriteLine("[LibVlcPlaybackBackend] Recycling media player");

        VlcMediaPlayer old = _mediaPlayer;
        ClearSource();
        DetachMediaPlayer(old);

        _mediaPlayer = CreateMediaPlayer();

        try
        {
            old.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibVlcPlaybackBackend] Error disposing recycled player: {ex.Message}");
        }
    }

    /// <summary>
    /// Network cache duration passed to LibVLC at prepare time, mirroring the
    /// user's buffer setting. Values of zero or less use the LibVLC default.
    /// </summary>
    public int NetworkCachingMs { get; set; }

    /// <summary>LibVLC's own default when <see cref="NetworkCachingMs"/> is unset.</summary>
    public const int DefaultNetworkCachingMs = 3000;

    /// <summary>
    /// The network cache value that would actually be handed to LibVLC, after the default
    /// substitution. Logging <see cref="NetworkCachingMs"/> directly is misleading, because
    /// a value of zero silently becomes <see cref="DefaultNetworkCachingMs"/>.
    /// </summary>
    public int EffectiveNetworkCachingMs =>
        NetworkCachingMs > 0 ? NetworkCachingMs : DefaultNetworkCachingMs;

    public PlaybackBackendKind Kind => PlaybackBackendKind.LibVlc;

    public Windows.Media.Playback.MediaPlaybackItem? CurrentPlaybackItem => null;

    public VlcMediaPlayer VlcMediaPlayer => _mediaPlayer;

    public bool IsPlaying => _mediaPlayer.IsPlaying;

    public bool IsBuffering => _isBuffering;

    public double BufferingProgress => _isBuffering ? 0.5 : 1.0;

    public TimeSpan Position => TimeSpan.FromMilliseconds(_mediaPlayer.Time);

    public TimeSpan? Duration =>
        _mediaPlayer.Length > 0 ? TimeSpan.FromMilliseconds(_mediaPlayer.Length) : null;

    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<bool>? BufferingStateChanged;
    public event EventHandler<PlaybackFailureEventArgs>? PlaybackFailed;
    public event EventHandler? PlaybackEnded;

    public IReadOnlyList<MediaTimeRange> GetBufferedRanges() => [];

    // volume is a fraction where 1.0 == 100%; LibVLC accepts 0-200 (200% = amplified).
    public void SetVolume(double volume) => _mediaPlayer.Volume = (int)Math.Clamp(volume * 100, 0, 200);

    public Task<PlaybackPrepareResult> PrepareAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        ClearSource();

        // Start each attempt from a clean log so a failure is explained by this attempt
        // rather than by whatever the previous station left behind.
        _logCapture?.Reset();
        _currentStreamUrl = streamUrl;

        try
        {
            _currentMedia = new Media(_libVlc, streamUrl, FromType.FromLocation);
            _currentMedia.AddOption($":network-caching={EffectiveNetworkCachingMs}");

            // Shoutcast/Icecast servers drop long-lived connections routinely. Without
            // reconnect, LibVLC treats the drop as end-of-stream and stops instead of
            // resuming, which the watchdog then has to recover from.
            _currentMedia.AddOption(":http-reconnect");

            _mediaPlayer.Media = _currentMedia;
        }
        catch (Exception ex)
        {
            string error = $"LibVLC could not open the stream: {ex.Message}";
            LogService.Error("LibVlcPlaybackBackend", $"Prepare failed for {LogService.Redact(streamUrl)}", ex);
            return Task.FromResult(PlaybackPrepareResult.Failed(PlaybackBackendKind.LibVlc, error));
        }

        LogService.Info("LibVlcPlaybackBackend",
            $"Prepared {LogService.Redact(streamUrl)} (networkCaching={EffectiveNetworkCachingMs}ms)");
        Debug.WriteLine($"[LibVlcPlaybackBackend] Prepared source for {streamUrl}");
        return Task.FromResult(PlaybackPrepareResult.Succeeded(PlaybackBackendKind.LibVlc));
    }

    public void Play()
    {
        // Play returns false when LibVLC rejects the media outright. That is the one
        // synchronous failure signal it offers, and it used to be discarded.
        if (!_mediaPlayer.Play())
        {
            string reason = DescribeLastError();
            LogService.Error("LibVlcPlaybackBackend",
                $"LibVLC refused to start {LogService.Redact(_currentStreamUrl)}: {reason}");
            _logCapture?.DumpTo("LibVLC refused to start playback");

            PlaybackFailed?.Invoke(this, new PlaybackFailureEventArgs(
                PlaybackBackendKind.LibVlc,
                reason,
                canRetryWithFallback: true));
        }
    }

    public void Pause() => _mediaPlayer.Pause();

    public void Seek(TimeSpan position) => _mediaPlayer.Time = (long)position.TotalMilliseconds;

    public void ClearSource()
    {
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Stop();
        }

        _mediaPlayer.Media = null;
        _currentMedia?.Dispose();
        _currentMedia = null;
        _isBuffering = false;
    }

    public void Dispose()
    {
        lock (_eventChainLock)
        {
            _isDisposed = true;
        }

        ClearSource();
        DetachMediaPlayer(_mediaPlayer);
        _mediaPlayer.Dispose();
    }
}
