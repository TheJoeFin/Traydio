using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Threading;

namespace Trdo.Services;

/// <summary>
/// The single owner of user-facing playback errors: everything that wants to tell the
/// user "this station won't play" reports it here, and this service decides whether,
/// when, and for how long that message is actually on screen.
/// <para>
/// Reporting a failure and showing it are separated on purpose. A report arrives while
/// the outcome is still being decided — the fallback engine may be seconds from
/// succeeding, and stream diagnosis probes the network before the message even exists —
/// and the tray popup keeps its page alive while hidden, so a dialog raised against it
/// does not fail, it waits and ambushes the user when they next open the flyout. This
/// service holds the report instead, re-judges it against live playback state (including
/// the WASAPI loopback, which is the only thing that can catch an engine claiming to play
/// while producing silence), and drops it the moment it stops being true.
/// </para>
/// </summary>
public sealed class PlaybackErrorService
{
    /// <summary>
    /// How often a held or shown error is re-judged. Fast enough that a dialog is
    /// withdrawn within a moment of playback recovering, slow enough to be free.
    /// </summary>
    private static readonly TimeSpan ReviewInterval = TimeSpan.FromSeconds(1);

    private readonly RadioPlayerService _player;
    private readonly DispatcherQueue? _uiQueue;
    private DispatcherQueueTimer? _reviewTimer;

    private string? _message;
    private string? _reportedStreamUrl;
    private DateTime _reportedAtUtc;
    private bool _isPresented;
    private bool _isHostWindowVisible;

    // Set by Shutdown(), which App.ShutdownAndExit() calls before disposing
    // RadioPlayerService.Instance. This is a singleton that is never itself disposed, but its
    // review timer ticks on the UI thread and BuildSignals() reads _player.IsPlaying/IsBuffering
    // - unguarded WinRT MediaPlayer.PlaybackSession touches. Without this, a tick already queued
    // on the dispatcher when the app quits could still fire after the MediaPlayer is released
    // and fail fast natively instead of throwing a catchable exception (same class of bug as
    // RadioPlayerService's SMTC DisplayUpdater fix).
    private volatile bool _isShutDown;

    /// <summary>
    /// Ticks of the last loopback frame loud enough to count as audio, or 0 if there has
    /// never been one. Written from the NAudio capture thread, so accessed via
    /// <see cref="Interlocked"/> rather than under the UI thread's assumptions.
    /// </summary>
    private long _lastAudioHeardTicks;

    public static PlaybackErrorService Instance { get; } = new();

    /// <summary>
    /// Raised on the UI thread when an error should be put on screen. Subscribe only
    /// while a presenter is actually able to show it: with no subscriber the service
    /// holds the error rather than considering it delivered.
    /// </summary>
    public event EventHandler<string>? ErrorPresented;

    /// <summary>
    /// Raised on the UI thread when an error that is currently on screen has stopped
    /// being true and should be taken down.
    /// </summary>
    public event EventHandler? ErrorWithdrawn;

    private PlaybackErrorService()
    {
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        _player = RadioPlayerService.Instance;

        _player.PlaybackFailed += (_, message) => Report(message);
        _player.PlaybackStateChanged += (_, _) => Review();
        _player.BufferingStateChanged += (_, _) => Review();
        _player.Watchdog.AudioLevelUpdated += OnAudioLevel;
    }

    /// <summary>
    /// Creates the service if it does not exist yet, so it is listening for failures
    /// from app start rather than from whenever the first page happens to touch it.
    /// </summary>
    public static void EnsureInitialized() => _ = Instance;

    /// <summary>
    /// Reports a playback failure. Whether the user ever sees it is this service's call.
    /// Safe to call from any thread.
    /// </summary>
    public void Report(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        RunOnUi(() =>
        {
            LogService.Info("PlaybackErrorService", $"Playback error reported: {message}");

            // A newer failure supersedes an older one: they describe the same broken
            // stream, and the latest attempt is the one the user is waiting on.
            bool supersededAShownError = _isPresented;
            if (supersededAShownError)
            {
                _isPresented = false;
                ErrorWithdrawn?.Invoke(this, EventArgs.Empty);
            }

            _message = message;
            _reportedStreamUrl = _player.StreamUrl;
            _reportedAtUtc = DateTime.UtcNow;

            EnsureReviewTimer();
            _reviewTimer?.Start();

            // Leave a superseded dialog a moment to finish closing: a ContentDialog
            // cannot be replaced in the same turn as the one it replaces. The next
            // review tick presents this error instead.
            if (!supersededAShownError)
            {
                ReviewCore();
            }
        });
    }

    /// <summary>
    /// Tells the service whether the window hosting the error presenter is on screen.
    /// Called by the tray popup as it shows and hides: its page stays loaded while
    /// hidden, so being subscribed is not the same as being able to show anything.
    /// </summary>
    public void SetHostWindowVisible(bool isVisible)
    {
        RunOnUi(() =>
        {
            if (_isHostWindowVisible == isVisible)
                return;

            _isHostWindowVisible = isVisible;
            ReviewCore();
        });
    }

    /// <summary>
    /// Called by the presenter once the user has dismissed the error, so it is not
    /// shown again and the service goes idle.
    /// </summary>
    public void NotifyErrorDismissed()
    {
        RunOnUi(() =>
        {
            if (_message is not null)
                LogService.Info("PlaybackErrorService", "Playback error dismissed by user");

            Clear();
        });
    }

    /// <summary>
    /// Drops any error that has not been shown yet, for the start of a fresh play
    /// attempt: whatever went wrong last time is about to be superseded by the outcome
    /// of this one. An error the user is currently reading is left alone.
    /// </summary>
    public void ClearPendingError()
    {
        RunOnUi(() =>
        {
            if (_message is null || _isPresented)
                return;

            Debug.WriteLine("[PlaybackErrorService] Dropping pending error for a new attempt");
            Clear();
        });
    }

    /// <summary>
    /// Records that the system audio output was audible. Runs on the NAudio capture
    /// thread ~30 times a second, so it does no more than stamp the clock — the review
    /// timer is what acts on it.
    /// </summary>
    private void OnAudioLevel(float level)
    {
        if (level >= AudioSilenceMonitorService.SilenceRmsThreshold)
        {
            Interlocked.Exchange(ref _lastAudioHeardTicks, DateTime.UtcNow.Ticks);
        }
    }

    private void Review() => RunOnUi(ReviewCore);

    /// <summary>
    /// Re-judges the held or shown error against live state. UI thread only.
    /// </summary>
    private void ReviewCore()
    {
        if (_isShutDown || _message is null)
            return;

        PlaybackErrorSignals signals = BuildSignals();

        if (_isPresented)
        {
            if (PlaybackErrorPolicy.ShouldWithdraw(in signals))
            {
                LogService.Info("PlaybackErrorService",
                    "Withdrawing playback error - it no longer describes the current state");
                _isPresented = false;
                Clear();
                ErrorWithdrawn?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        switch (PlaybackErrorPolicy.Evaluate(in signals))
        {
            case PlaybackErrorVerdict.Show:
                string message = _message;
                _isPresented = true;
                LogService.Info("PlaybackErrorService", $"Showing playback error: {message}");
                ErrorPresented?.Invoke(this, message);
                break;

            case PlaybackErrorVerdict.Discard:
                LogService.Info("PlaybackErrorService",
                    $"Suppressing playback error - {DescribeSuppression(in signals)}: {_message}");
                Clear();
                break;

            case PlaybackErrorVerdict.Hold:
                break;
        }
    }

    private PlaybackErrorSignals BuildSignals()
    {
        long audioTicks = Interlocked.Read(ref _lastAudioHeardTicks);
        double? secondsSinceAudioHeard = audioTicks == 0
            ? null
            : (DateTime.UtcNow - new DateTime(audioTicks, DateTimeKind.Utc)).TotalSeconds;

        return new PlaybackErrorSignals
        {
            IsPlaying = _player.IsPlaying,
            IsBuffering = _player.IsBuffering,
            IsHostVisible = _isHostWindowVisible && ErrorPresented is not null,
            StreamChangedSinceReport = !string.Equals(
                _reportedStreamUrl, _player.StreamUrl, StringComparison.Ordinal),
            AgeSeconds = (DateTime.UtcNow - _reportedAtUtc).TotalSeconds,
            IsAudioMonitorRunning = _player.Watchdog.IsAudioMonitorRunning,
            SecondsSinceAudioHeard = secondsSinceAudioHeard,
        };
    }

    /// <summary>Explains a discard for the log, so suppressed errors stay diagnosable.</summary>
    private static string DescribeSuppression(in PlaybackErrorSignals signals)
    {
        if (signals.StreamChangedSinceReport)
            return "the stream changed";

        if (PlaybackErrorPolicy.IsPlaybackHealthy(in signals))
            return "playback is running normally";

        return $"it went unseen for {signals.AgeSeconds:F0}s";
    }

    private void Clear()
    {
        _message = null;
        _reportedStreamUrl = null;
        _isPresented = false;
        _reviewTimer?.Stop();
    }

    private void EnsureReviewTimer()
    {
        if (_reviewTimer is not null || _uiQueue is null)
            return;

        _reviewTimer = _uiQueue.CreateTimer();
        _reviewTimer.Interval = ReviewInterval;
        _reviewTimer.IsRepeating = true;
        _reviewTimer.Tick += (_, _) => ReviewCore();
    }

    /// <summary>
    /// Stops the review timer so it cannot tick again, called from App.ShutdownAndExit()
    /// before RadioPlayerService.Instance is disposed. Runs regardless of _isShutDown/RunOnUi
    /// so the stop itself is never skipped by the guard it is setting up.
    /// </summary>
    public void Shutdown()
    {
        _isShutDown = true;

        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            _reviewTimer?.Stop();
        }
        else
        {
            _uiQueue.TryEnqueue(() => _reviewTimer?.Stop());
        }
    }

    private void RunOnUi(Action action)
    {
        if (_isShutDown)
        {
            return;
        }

        if (_uiQueue is null || _uiQueue.HasThreadAccess)
        {
            action();
            return;
        }

        // _isShutDown is re-checked inside the enqueued action, not just on entry above:
        // Shutdown() can run in the gap between this enqueue and the queue invoking it.
        _uiQueue.TryEnqueue(() =>
        {
            if (!_isShutDown)
            {
                action();
            }
        });
    }
}
