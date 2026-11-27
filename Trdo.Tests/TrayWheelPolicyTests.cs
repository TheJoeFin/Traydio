using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Models;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// Covers how raw wheel deltas from the tray icon become volume and station steps.
/// </summary>
[TestClass]
public sealed class TrayWheelPolicyTests
{
    [TestMethod]
    public void TakeNotches_WholeNotch_ReturnsOne()
    {
        int pending = 0;

        Assert.AreEqual(1, TrayWheelPolicy.TakeNotches(ref pending, 120));
        Assert.AreEqual(0, pending);
    }

    [TestMethod]
    public void TakeNotches_FractionsAccumulateUntilANotchIsComplete()
    {
        int pending = 0;

        Assert.AreEqual(0, TrayWheelPolicy.TakeNotches(ref pending, 40));
        Assert.AreEqual(0, TrayWheelPolicy.TakeNotches(ref pending, 40));
        Assert.AreEqual(1, TrayWheelPolicy.TakeNotches(ref pending, 40));
        Assert.AreEqual(0, pending);
    }

    [TestMethod]
    public void TakeNotches_NegativeDeltaScrollsTheOtherWay()
    {
        int pending = 0;

        Assert.AreEqual(-2, TrayWheelPolicy.TakeNotches(ref pending, -240));
    }

    [TestMethod]
    public void TakeNotches_LargeDeltaReturnsSeveralNotchesAndKeepsTheRemainder()
    {
        int pending = 0;

        Assert.AreEqual(3, TrayWheelPolicy.TakeNotches(ref pending, 400));
        Assert.AreEqual(40, pending);
    }

    [TestMethod]
    public void TakeNotches_ReversingDiscardsTheOldRemainder()
    {
        int pending = 0;
        TrayWheelPolicy.TakeNotches(ref pending, 100);

        // 100 pending up, then 60 down: without the reset this would net to -40 and need more
        // scrolling before it counted. The reversal starts fresh instead.
        Assert.AreEqual(0, TrayWheelPolicy.TakeNotches(ref pending, -60));
        Assert.AreEqual(-60, pending);
    }

    [TestMethod]
    public void TakeNotches_ZeroDelta_ChangesNothing()
    {
        int pending = 30;

        Assert.AreEqual(0, TrayWheelPolicy.TakeNotches(ref pending, 0));
        Assert.AreEqual(30, pending);
    }

    [TestMethod]
    public void StepVolumePercent_ScrollingUpRaisesByTheStep()
    {
        Assert.AreEqual(55, TrayWheelPolicy.StepVolumePercent(50, 1));
        Assert.AreEqual(65, TrayWheelPolicy.StepVolumePercent(50, 3));
    }

    [TestMethod]
    public void StepVolumePercent_ScrollingDownLowersByTheStep()
    {
        Assert.AreEqual(45, TrayWheelPolicy.StepVolumePercent(50, -1));
    }

    [TestMethod]
    public void StepVolumePercent_ClampsToTheVolumeRange()
    {
        Assert.AreEqual(200, TrayWheelPolicy.StepVolumePercent(198, 5));
        Assert.AreEqual(0, TrayWheelPolicy.StepVolumePercent(3, -5));
    }

    [TestMethod]
    public void StationSteps_ScrollingDownIsNextAndUpIsPrevious()
    {
        Assert.AreEqual(1, TrayWheelPolicy.StationSteps(-1));
        Assert.AreEqual(-2, TrayWheelPolicy.StationSteps(2));
    }

    [TestMethod]
    public void Parse_DefinedValue_IsReturned()
    {
        Assert.AreEqual(TrayWheelAction.SwitchStation, TrayWheelPolicy.Parse(2, TrayWheelAction.None));
    }

    [TestMethod]
    public void Parse_UndefinedValue_FallsBack()
    {
        Assert.AreEqual(TrayWheelAction.Volume, TrayWheelPolicy.Parse(99, TrayWheelAction.Volume));
    }

    [TestMethod]
    public void DefaultAction_LeavesTheWheelAlone()
    {
        Assert.AreEqual(TrayWheelAction.None, TrayWheelPolicy.DefaultAction);
    }
}
