using System;
using Trdo.Services.Metadata;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Accumulates how long the current scrobble candidate has actually been playing - excluding
/// paused/buffering time - and fires <see cref="BecameEligible"/> once it crosses the threshold
/// from <see cref="LastfmScrobbleEligibilityPolicy.ComputeEligibilityDelay"/>. A track that
/// changes or ends before that is read via <see cref="Elapsed"/> instead, for the retroactive
/// check.
/// </summary>
/// <remarks>
/// Reuses <see cref="IDelayScheduler"/>/<see cref="ThreadPoolDelayScheduler"/> from
/// <see cref="MetadataPublishGate"/> so this can be driven by a fake clock in tests the same way.
/// </remarks>
internal sealed class ScrobbleTimingTracker : IDisposable
{
    private readonly IDelayScheduler _scheduler;
    private readonly bool _ownsScheduler;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();

    private DateTimeOffset? _segmentStartedAtUtc;
    private TimeSpan _accumulated = TimeSpan.Zero;
    private TimeSpan? _eligibilityDelay;

    // Bumped by every reschedule (Start/Pause/Resume/Stop) so a timer callback already in flight
    // when one of those runs can tell it is no longer current - see OnTimerFired.
    private long _generation;

    // Bumped only by Start/Stop, i.e. only when the candidate itself changes - a Pause/Resume
    // cycle reschedules the timer (bumping _generation above) without this changing, since it is
    // still the same candidate being timed. This is the token Start returns and BecameEligible
    // carries, precisely so a Pause/Resume in between does not make a legitimate event look stale.
    private long _candidateId;

    public ScrobbleTimingTracker(IDelayScheduler? scheduler = null, Func<DateTimeOffset>? clock = null)
    {
        _ownsScheduler = scheduler is null;
        _scheduler = scheduler ?? new ThreadPoolDelayScheduler();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Raised once the current candidate crosses its eligibility threshold while playing. Carries
    /// the token <see cref="Start"/> returned for that candidate, so a caller whose candidate
    /// changed in the (rare, racy) window between the timer firing and this event actually being
    /// delivered can tell this notification is now stale rather than trust it blindly - a fresh
    /// call to <see cref="Start"/> always returns a token this event has not carried before.
    /// </summary>
    public event Action<long>? BecameEligible;

    /// <summary>Time actually spent playing the current candidate so far, including any segment in progress.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return _accumulated + RunningSegment();
            }
        }
    }

    /// <summary>
    /// Starts timing a new candidate, discarding whatever was being timed before. Assumes
    /// playback is active - the caller (<see cref="LastfmScrobbleService"/>) only starts a new
    /// candidate on a metadata change, which only happens while audio is flowing.
    /// </summary>
    /// <returns>
    /// A token identifying this candidate. The caller should remember it and compare it against
    /// the token carried by <see cref="BecameEligible"/> before trusting that event, in case its
    /// delivery was delayed past a subsequent <see cref="Start"/>/<see cref="Stop"/> call.
    /// </returns>
    public long Start(TimeSpan? knownDuration)
    {
        lock (_lock)
        {
            CancelLocked();
            _accumulated = TimeSpan.Zero;
            _eligibilityDelay = LastfmScrobbleEligibilityPolicy.ComputeEligibilityDelay(knownDuration);
            _segmentStartedAtUtc = _clock();
            long candidateId = ++_candidateId;

            if (_eligibilityDelay is { } delay)
                ScheduleLocked(delay);

            return candidateId;
        }
    }

    /// <summary>Stops the clock (buffering/paused) without discarding time already accumulated.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_segmentStartedAtUtc is null)
                return;

            _accumulated += RunningSegment();
            _segmentStartedAtUtc = null;
            _scheduler.Cancel();
            _generation++;
        }
    }

    /// <summary>Resumes the clock, re-arming the remaining time toward eligibility.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_segmentStartedAtUtc is not null)
                return;

            _segmentStartedAtUtc = _clock();

            if (_eligibilityDelay is { } delay)
            {
                TimeSpan remaining = delay - _accumulated;
                if (remaining > TimeSpan.Zero)
                    ScheduleLocked(remaining);
            }
        }
    }

    /// <summary>Discards the candidate being timed entirely - nothing further will be reported for it.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            CancelLocked();
            _accumulated = TimeSpan.Zero;
            _eligibilityDelay = null;
            _candidateId++;
        }
    }

    private TimeSpan RunningSegment() =>
        _segmentStartedAtUtc is { } startedAt ? _clock() - startedAt : TimeSpan.Zero;

    private void CancelLocked()
    {
        _segmentStartedAtUtc = null;
        _scheduler.Cancel();
        _generation++;
    }

    private void ScheduleLocked(TimeSpan delay)
    {
        long generation = ++_generation;
        _scheduler.Schedule(delay, () => OnTimerFired(generation));
    }

    private void OnTimerFired(long generation)
    {
        long candidateId;

        lock (_lock)
        {
            if (generation != _generation)
                return;

            // Reached eligibility while still playing this candidate's full window.
            _accumulated += RunningSegment();
            _segmentStartedAtUtc = _clock();
            candidateId = _candidateId;
        }

        BecameEligible?.Invoke(candidateId);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CancelLocked();
        }

        if (_ownsScheduler && _scheduler is IDisposable disposable)
            disposable.Dispose();
    }
}
