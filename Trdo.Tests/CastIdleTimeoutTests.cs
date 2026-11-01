using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Services.Playback;

namespace Trdo.Tests;

[TestClass]
public sealed class CastIdleTimeoutTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PausedCast_ExpiresOnlyAfterTimeout()
    {
        CastIdleTimeout timeout = new();
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start));
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start.AddMinutes(45).AddTicks(-1)));
        Assert.IsTrue(timeout.ShouldEndCast(true, false, 45, Start.AddMinutes(45)));
    }

    [TestMethod]
    public void PlayingOrStartingCast_DoesNotExpireAndResetsIdleTime()
    {
        CastIdleTimeout timeout = new();
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start));
        Assert.IsFalse(timeout.ShouldEndCast(true, true, 45, Start.AddHours(1)));
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start.AddHours(2)));
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start.AddHours(2).AddMinutes(44)));
        Assert.IsTrue(timeout.ShouldEndCast(true, false, 45, Start.AddHours(2).AddMinutes(45)));
    }

    [TestMethod]
    public void NewCastTarget_StartsFreshTimeout()
    {
        CastIdleTimeout timeout = new();
        timeout.ShouldEndCast(true, false, 45, Start);
        timeout.Reset();
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start.AddHours(1)));
        Assert.IsTrue(timeout.ShouldEndCast(true, false, 45, Start.AddHours(1).AddMinutes(45)));
    }

    [TestMethod]
    public void ReturningToLocalOutput_CancelsOldTimeout()
    {
        CastIdleTimeout timeout = new();
        timeout.ShouldEndCast(true, false, 45, Start);
        Assert.IsFalse(timeout.ShouldEndCast(false, false, 45, Start.AddHours(1)));
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start.AddHours(2)));
    }

    [TestMethod]
    public void Never_DisablesTimeoutAndReenablingStartsFresh()
    {
        CastIdleTimeout timeout = new();
        timeout.ShouldEndCast(true, false, 45, Start);
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 0, Start.AddDays(1)));
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 45, Start.AddDays(2)));
        Assert.IsTrue(timeout.ShouldEndCast(true, false, 45, Start.AddDays(2).AddMinutes(45)));
    }

    [TestMethod]
    public void ChangingDuration_AppliesToExistingIdlePeriod()
    {
        CastIdleTimeout timeout = new();
        timeout.ShouldEndCast(true, false, 45, Start);
        Assert.IsFalse(timeout.ShouldEndCast(true, false, 60, Start.AddMinutes(45)));
        Assert.IsTrue(timeout.ShouldEndCast(true, false, 30, Start.AddMinutes(45)));
    }
}
