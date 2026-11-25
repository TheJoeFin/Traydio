using System;
using System.Collections.Generic;

namespace Trdo.Services.Playback;

/// <summary>
/// The action the watchdog should take for a given recovery attempt.
/// </summary>
public enum RecoveryAction
{
    /// <summary>Re-prepare the stream on the current backend and play again.</summary>
    SoftRetry,

    /// <summary>Tear the playback pipeline down and rebuild it, recycling the backend.</summary>
    RebuildPipeline,

    /// <summary>Mark the current backend unhealthy for this station and rebuild on the other one.</summary>
    SwitchBackend,

    /// <summary>Stop retrying for a while before re-entering the ladder.</summary>
    BackOff,

    /// <summary>Stop recovering entirely and surface the failure to the user.</summary>
    GiveUp
}

/// <summary>
/// Decides how far to escalate stream recovery, based on how often playback has failed
/// and whether it has held up in between.
/// <para>
/// This deliberately has no dependencies on WinRT, settings storage, or the player, so the
/// escalation rules can be unit tested. The clock is injectable for the same reason.
/// </para>
/// <para>
/// The key property is that the ladder only decays after playback has been confirmed healthy
/// for a sustained interval. Decaying on a single healthy observation is what previously let a
/// flapping stream sit at "recovery attempt 1/3" indefinitely without ever escalating.
/// </para>
/// </summary>
public sealed class RecoveryPolicy
{
    /// <summary>Failures older than this stop counting toward the ladder.</summary>
    public static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromMinutes(2);

    /// <summary>How long playback must hold before the ladder steps back down a rung.</summary>
    public static readonly TimeSpan DefaultStabilityInterval = TimeSpan.FromSeconds(60);

    /// <summary>Highest auto-buffer bump this policy will ask for, in buffer levels.</summary>
    public const int MaxAutoBufferBump = 3;

    /// <summary>Back-offs without an intervening stable interval before we give up.</summary>
    public const int MaxBackOffsBeforeGivingUp = 2;

    // Ladder rungs, by escalation level. Levels at or beyond the last entry back off.
    private const int LevelSoftRetry = 0;
    private const int LevelRebuild = 1;
    private const int LevelSwitchBackend = 2;

    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _failureWindow;
    private readonly TimeSpan _stabilityInterval;
    private readonly Queue<DateTime> _failures = new();

    private int _level;
    private int _backOffsSinceStable;
    private int _autoBufferBump;
    private DateTime? _playingSince;
    // volatile: HasGivenUp is read from the poll and silence-monitor threads outside the
    // watchdog's recovery gate, purely to avoid re-arming silence detection after a give-up.
    private volatile bool _hasGivenUp;

    public RecoveryPolicy(
        Func<DateTime>? clock = null,
        TimeSpan? failureWindow = null,
        TimeSpan? stabilityInterval = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _failureWindow = failureWindow ?? DefaultFailureWindow;
        _stabilityInterval = stabilityInterval ?? DefaultStabilityInterval;
    }

    /// <summary>
    /// Extra buffer levels the policy is asking for on top of the user's configured level.
    /// Transient and per-station: cleared by <see cref="ResetForStation"/>.
    /// </summary>
    public int AutoBufferBump => _autoBufferBump;

    /// <summary>Number of failures currently inside the sliding window.</summary>
    public int FailuresInWindow => _failures.Count;

    /// <summary>The action the most recent <see cref="RecordFailure"/> returned.</summary>
    public RecoveryAction CurrentAction => ActionForLevel(_level);

    /// <summary>True once the policy has stopped recovering and handed off to the user.</summary>
    public bool HasGivenUp => _hasGivenUp;

    /// <summary>
    /// Whether auto-buffer escalation is allowed. Mirrors the user's
    /// "automatically increase buffer" setting; when false the ladder still escalates,
    /// it just stops asking for more buffer.
    /// </summary>
    public bool AutoBufferIncreaseEnabled { get; set; } = true;

    /// <summary>
    /// Records a failed playback attempt and returns the action to take for it.
    /// </summary>
    public RecoveryAction RecordFailure()
    {
        DateTime now = _clock();

        // A failure ends any stability streak.
        _playingSince = null;

        _failures.Enqueue(now);
        TrimExpiredFailures(now);

        RecoveryAction action = ActionForLevel(_level);

        if (action == RecoveryAction.RebuildPipeline &&
            AutoBufferIncreaseEnabled &&
            _autoBufferBump < MaxAutoBufferBump)
        {
            _autoBufferBump++;
        }

        if (action == RecoveryAction.BackOff)
        {
            _backOffsSinceStable++;
            if (_backOffsSinceStable > MaxBackOffsBeforeGivingUp)
            {
                _hasGivenUp = true;
                return RecoveryAction.GiveUp;
            }

            // Re-enter the ladder one rung below the back-off so the next failure
            // retries the backend switch rather than backing off immediately again.
            _level = LevelSwitchBackend;
            return RecoveryAction.BackOff;
        }

        _level++;
        return action;
    }

    /// <summary>
    /// Records that playback is currently confirmed healthy. Call this on every healthy
    /// observation; the ladder steps down only once playback has held for the stability
    /// interval, and a further rung for each interval after that.
    /// </summary>
    public void RecordPlaybackConfirmed()
    {
        DateTime now = _clock();

        if (_playingSince is null)
        {
            _playingSince = now;
            return;
        }

        if (now - _playingSince.Value < _stabilityInterval)
        {
            return;
        }

        // Sustained playback: step the ladder down and restart the stability window.
        _playingSince = now;
        _backOffsSinceStable = 0;

        if (_level > 0)
        {
            _level--;
        }

        if (_autoBufferBump > 0)
        {
            _autoBufferBump--;
        }

        if (_level == 0 && _autoBufferBump == 0)
        {
            _failures.Clear();
        }
    }

    /// <summary>
    /// Returns the policy to a clean slate, equivalent to a freshly constructed instance.
    /// Call this when the user changes station or clears the playback target — a new stream
    /// should not inherit the previous one's escalation state or buffer bump.
    /// </summary>
    public void ResetForStation()
    {
        _failures.Clear();
        _level = 0;
        _backOffsSinceStable = 0;
        _autoBufferBump = 0;
        _playingSince = null;
        _hasGivenUp = false;
    }

    private void TrimExpiredFailures(DateTime now)
    {
        while (_failures.Count > 0 && now - _failures.Peek() > _failureWindow)
        {
            _failures.Dequeue();
        }
    }

    private static RecoveryAction ActionForLevel(int level) => level switch
    {
        LevelSoftRetry => RecoveryAction.SoftRetry,
        LevelRebuild => RecoveryAction.RebuildPipeline,
        LevelSwitchBackend => RecoveryAction.SwitchBackend,
        _ => RecoveryAction.BackOff
    };
}
