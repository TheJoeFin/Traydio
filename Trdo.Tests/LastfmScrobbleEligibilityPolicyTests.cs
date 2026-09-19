using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Trdo.Services.Lastfm;

namespace Trdo.Tests;

/// <summary>
/// Covers Last.fm's scrobble-eligibility rule (30s floor, half-duration-or-4-minutes ceiling)
/// and the radio proxy used when a track's real duration is not known.
/// </summary>
[TestClass]
public sealed class LastfmScrobbleEligibilityPolicyTests
{
    [TestMethod]
    public void ComputeEligibilityDelay_ShortKnownDuration_NeverEligible()
    {
        TimeSpan? delay = LastfmScrobbleEligibilityPolicy.ComputeEligibilityDelay(TimeSpan.FromSeconds(30));

        Assert.IsNull(delay);
    }

    [TestMethod]
    public void ComputeEligibilityDelay_ModerateKnownDuration_UsesHalfDuration()
    {
        TimeSpan? delay = LastfmScrobbleEligibilityPolicy.ComputeEligibilityDelay(TimeSpan.FromSeconds(200));

        Assert.AreEqual(TimeSpan.FromSeconds(100), delay);
    }

    [TestMethod]
    public void ComputeEligibilityDelay_LongKnownDuration_CapsAtFourMinutes()
    {
        TimeSpan? delay = LastfmScrobbleEligibilityPolicy.ComputeEligibilityDelay(TimeSpan.FromMinutes(20));

        Assert.AreEqual(TimeSpan.FromSeconds(240), delay);
    }

    [TestMethod]
    public void ComputeEligibilityDelay_UnknownDuration_UsesProactiveCeiling()
    {
        TimeSpan? delay = LastfmScrobbleEligibilityPolicy.ComputeEligibilityDelay(null);

        Assert.AreEqual(TimeSpan.FromSeconds(240), delay);
    }

    [TestMethod]
    public void IsRetroactivelyEligible_KnownDuration_MatchesExactThreshold()
    {
        TimeSpan duration = TimeSpan.FromSeconds(200);

        Assert.IsFalse(LastfmScrobbleEligibilityPolicy.IsRetroactivelyEligible(TimeSpan.FromSeconds(99), duration));
        Assert.IsTrue(LastfmScrobbleEligibilityPolicy.IsRetroactivelyEligible(TimeSpan.FromSeconds(100), duration));
    }

    [TestMethod]
    public void IsRetroactivelyEligible_ShortKnownDuration_NeverEligible()
    {
        TimeSpan duration = TimeSpan.FromSeconds(20);

        Assert.IsFalse(LastfmScrobbleEligibilityPolicy.IsRetroactivelyEligible(TimeSpan.FromMinutes(10), duration));
    }

    [TestMethod]
    public void IsRetroactivelyEligible_UnknownDuration_UsesThirtySecondFloor()
    {
        Assert.IsFalse(LastfmScrobbleEligibilityPolicy.IsRetroactivelyEligible(TimeSpan.FromSeconds(29), null));
        Assert.IsTrue(LastfmScrobbleEligibilityPolicy.IsRetroactivelyEligible(TimeSpan.FromSeconds(30), null));
    }
}
