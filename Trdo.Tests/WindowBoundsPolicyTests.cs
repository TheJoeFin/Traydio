using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// Covers remembering where windows were left: the saved text form, and the scaling that has to
/// survive being applied on every open without the window drifting in size.
/// </summary>
[TestClass]
public sealed class WindowBoundsPolicyTests
{
    [TestMethod]
    public void SavedBounds_RoundTrip_IncludingAMonitorLeftOfThePrimary()
    {
        SavedWindowBounds saved = new(-1920, 40, 360, 260);

        Assert.IsTrue(SavedWindowBounds.TryParse(saved.Serialize(), out SavedWindowBounds parsed));
        Assert.AreEqual(saved, parsed);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1,2,3")]
    [DataRow("1,2,3,4,5")]
    [DataRow("1,2,wide,4")]
    [DataRow("1,2,0,4")]
    [DataRow("1,2,3,-4")]
    public void SavedBounds_AnythingElse_IsNothingSaved(string text)
    {
        Assert.IsFalse(SavedWindowBounds.TryParse(text, out _));
    }

    [TestMethod]
    public void SavedBounds_Missing_IsNothingSaved()
    {
        Assert.IsFalse(SavedWindowBounds.TryParse(null, out _));
    }

    [TestMethod]
    [DataRow(96u)]
    [DataRow(120u)]
    [DataRow(144u)]
    [DataRow(168u)]
    [DataRow(192u)]
    [DataRow(240u)]
    public void ToLogical_UndoesPlacementRoundingUp_AtEveryScale(uint dpi)
    {
        for (int logical = 100; logical <= 1200; logical++)
        {
            // The same conversion window placement applies.
            int physical = (int)Math.Ceiling(logical * dpi / 96.0);

            Assert.AreEqual(logical, WindowBoundsPolicy.ToLogical(physical, dpi));
        }
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-5d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void RestoreExtent_WithNothingUsableSaved_UsesTheDefault(double saved)
    {
        Assert.AreEqual(500, WindowBoundsPolicy.RestoreExtent(saved, fallback: 500, minimum: 400));
    }

    [TestMethod]
    public void RestoreExtent_KeepsASavedSize_ButNotBelowTheMinimum()
    {
        Assert.AreEqual(640, WindowBoundsPolicy.RestoreExtent(640, fallback: 500, minimum: 400));
        Assert.AreEqual(400, WindowBoundsPolicy.RestoreExtent(120, fallback: 500, minimum: 400));
    }
}
