using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Trdo.Services.Lastfm;
using Trdo.Services.Metadata;

namespace Trdo.Tests;

/// <summary>
/// Covers the stateful half of scrobble timing: accumulating actual played time across
/// pause/resume and firing once the eligibility window elapses. The threshold math itself is
/// <see cref="LastfmScrobbleEligibilityPolicyTests"/>.
/// </summary>
[TestClass]
public sealed class ScrobbleTimingTrackerTests
{
    /// <summary>
    /// Stands in for the thread-pool timer so time can be stepped forward exactly rather than
    /// slept through. Mirrors MetadataPublishGateTests.FakeScheduler.
    /// </summary>
    private sealed class FakeScheduler : IDelayScheduler
    {
        private Action? _callback;

        public TimeSpan? ScheduledDelay { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            ScheduledDelay = delay;
            _callback = callback;
        }

        public void Cancel()
        {
            ScheduledDelay = null;
            _callback = null;
        }

        public void Fire()
        {
            Action? callback = _callback;
            _callback = null;
            ScheduledDelay = null;
            callback?.Invoke();
        }
    }

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Get() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    [TestMethod]
    public void Start_UnknownDuration_FiresBecameEligibleAfterFourMinutes()
    {
        FakeScheduler scheduler = new();
        FakeClock clock = new();
        using ScrobbleTimingTracker tracker = new(scheduler, clock.Get);
        int fired = 0;
        long? firedWithToken = null;
        tracker.BecameEligible += token => { fired++; firedWithToken = token; };

        long startToken = tracker.Start(knownDuration: null);
        Assert.AreEqual(TimeSpan.FromSeconds(240), scheduler.ScheduledDelay);

        clock.Advance(TimeSpan.FromSeconds(240));
        scheduler.Fire();

        Assert.AreEqual(1, fired);
        Assert.AreEqual(startToken, firedWithToken);
    }

    [TestMethod]
    public void Pause_StopsAccumulatingAndCancelsTimer()
    {
        FakeScheduler scheduler = new();
        FakeClock clock = new();
        using ScrobbleTimingTracker tracker = new(scheduler, clock.Get);

        tracker.Start(knownDuration: TimeSpan.FromSeconds(200)); // eligible at 100s
        clock.Advance(TimeSpan.FromSeconds(40));
        tracker.Pause();

        Assert.IsNull(scheduler.ScheduledDelay);
        Assert.AreEqual(TimeSpan.FromSeconds(40), tracker.Elapsed);

        // Time passing while paused must not count.
        clock.Advance(TimeSpan.FromSeconds(1000));
        Assert.AreEqual(TimeSpan.FromSeconds(40), tracker.Elapsed);
    }

    [TestMethod]
    public void Resume_ReArmsOnlyTheRemainingTime()
    {
        FakeScheduler scheduler = new();
        FakeClock clock = new();
        using ScrobbleTimingTracker tracker = new(scheduler, clock.Get);

        long startToken = tracker.Start(knownDuration: TimeSpan.FromSeconds(200)); // eligible at 100s
        clock.Advance(TimeSpan.FromSeconds(40));
        tracker.Pause();

        tracker.Resume();

        // 100s threshold minus the 40s already accumulated.
        Assert.AreEqual(TimeSpan.FromSeconds(60), scheduler.ScheduledDelay);

        clock.Advance(TimeSpan.FromSeconds(60));
        int fired = 0;
        long? firedWithToken = null;
        tracker.BecameEligible += token => { fired++; firedWithToken = token; };
        scheduler.Fire();

        Assert.AreEqual(1, fired);
        Assert.AreEqual(TimeSpan.FromSeconds(100), tracker.Elapsed);

        // Pause/Resume rescheduled the timer, but this is still the same candidate - the token
        // must not have changed, or a caller would wrongly treat this legitimate event as stale.
        Assert.AreEqual(startToken, firedWithToken);
    }

    [TestMethod]
    public void Start_AfterResume_ReturnsADifferentTokenThanBefore()
    {
        FakeScheduler scheduler = new();
        FakeClock clock = new();
        using ScrobbleTimingTracker tracker = new(scheduler, clock.Get);

        long firstToken = tracker.Start(knownDuration: TimeSpan.FromSeconds(200));
        clock.Advance(TimeSpan.FromSeconds(40));
        tracker.Pause();
        tracker.Resume();

        long secondToken = tracker.Start(knownDuration: TimeSpan.FromSeconds(60));

        Assert.AreNotEqual(firstToken, secondToken);
    }

    [TestMethod]
    public void Start_ReplacesPreviousCandidateAndResetsAccumulation()
    {
        FakeScheduler scheduler = new();
        FakeClock clock = new();
        using ScrobbleTimingTracker tracker = new(scheduler, clock.Get);

        tracker.Start(knownDuration: TimeSpan.FromSeconds(200));
        clock.Advance(TimeSpan.FromSeconds(50));

        tracker.Start(knownDuration: TimeSpan.FromSeconds(60)); // new candidate, eligible at 30s

        Assert.AreEqual(TimeSpan.Zero, tracker.Elapsed);
        Assert.AreEqual(TimeSpan.FromSeconds(30), scheduler.ScheduledDelay);
    }

    [TestMethod]
    public void Stop_DiscardsAccumulatedTimeAndCancelsTimer()
    {
        FakeScheduler scheduler = new();
        FakeClock clock = new();
        using ScrobbleTimingTracker tracker = new(scheduler, clock.Get);

        tracker.Start(knownDuration: null);
        clock.Advance(TimeSpan.FromSeconds(90));
        tracker.Stop();

        Assert.IsNull(scheduler.ScheduledDelay);
        Assert.AreEqual(TimeSpan.Zero, tracker.Elapsed);
    }
}
