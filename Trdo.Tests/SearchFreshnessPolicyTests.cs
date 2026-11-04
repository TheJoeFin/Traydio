using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// Covers when the search page treats a search as stale enough to reset - a quick trip to the
/// edit page and back should not lose the results, but coming back several minutes later should.
/// </summary>
[TestClass]
public sealed class SearchFreshnessPolicyTests
{
    private static readonly DateTimeOffset Left = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void IsStale_JustLeftAndBack_IsFalse()
    {
        DateTimeOffset now = Left.AddSeconds(5);

        Assert.IsFalse(SearchFreshnessPolicy.IsStale(Left, now));
    }

    [TestMethod]
    public void IsStale_RightAtTheBoundary_IsFalse()
    {
        DateTimeOffset now = Left + SearchFreshnessPolicy.StaleAfter;

        Assert.IsFalse(SearchFreshnessPolicy.IsStale(Left, now));
    }

    [TestMethod]
    public void IsStale_PastTheBoundary_IsTrue()
    {
        DateTimeOffset now = Left + SearchFreshnessPolicy.StaleAfter + TimeSpan.FromSeconds(1);

        Assert.IsTrue(SearchFreshnessPolicy.IsStale(Left, now));
    }

    [TestMethod]
    public void IsStale_HonoursACustomWindow()
    {
        DateTimeOffset now = Left.AddMinutes(1);

        Assert.IsTrue(SearchFreshnessPolicy.IsStale(Left, now, TimeSpan.FromSeconds(30)));
        Assert.IsFalse(SearchFreshnessPolicy.IsStale(Left, now, TimeSpan.FromMinutes(2)));
    }
}
