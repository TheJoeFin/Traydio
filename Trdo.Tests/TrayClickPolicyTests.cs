using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Models;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// Covers the tray icon's per-button click assignments.
/// <para>
/// Three things here are easy to break without noticing. The pre-2.1 two-way switch has to
/// map onto the new pair so nobody's layout flips on upgrade. The flyout must stay reachable
/// from at least one button, because a tray-only app with no way back to Settings is stuck.
/// And a click that needs a station has to degrade to the flyout, not to silence, while there
/// is none.
/// </para>
/// </summary>
[TestClass]
public sealed class TrayClickPolicyTests
{
    [TestMethod]
    public void LegacyDefault_MapsToPlayPauseLeftFlyoutRight()
    {
        (TrayClickAction left, TrayClickAction right) = TrayClickPolicy.FromLegacyBehavior(0);

        Assert.AreEqual(TrayClickAction.PlayPause, left);
        Assert.AreEqual(TrayClickAction.ShowFlyout, right);
    }

    [TestMethod]
    public void LegacySwapped_MapsToFlyoutLeftPlayPauseRight()
    {
        (TrayClickAction left, TrayClickAction right) = TrayClickPolicy.FromLegacyBehavior(1);

        Assert.AreEqual(TrayClickAction.ShowFlyout, left);
        Assert.AreEqual(TrayClickAction.PlayPause, right);
    }

    [TestMethod]
    public void LegacyOutOfRange_ReadsAsDefault()
    {
        // The old setter clamped anything outside 0..1 to 0; a stray value must not invent
        // a third layout.
        Assert.AreEqual(TrayClickPolicy.FromLegacyBehavior(0), TrayClickPolicy.FromLegacyBehavior(7));
        Assert.AreEqual(TrayClickPolicy.FromLegacyBehavior(0), TrayClickPolicy.FromLegacyBehavior(-1));
    }

    [TestMethod]
    public void LegacyRoundTrip_IsLossless()
    {
        // What the migration writes must read back as the same legacy value on a downgrade,
        // for both layouts the old switch could express.
        foreach (int legacy in new[] { 0, 1 })
        {
            (TrayClickAction left, TrayClickAction right) = TrayClickPolicy.FromLegacyBehavior(legacy);
            Assert.AreEqual(legacy, TrayClickPolicy.ToLegacyBehavior(left, right));
        }
    }

    [TestMethod]
    public void ToLegacyBehavior_NewLayouts_MapToNearestTwoWayForm()
    {
        // Flyout on the left is the swapped layout's defining half, whatever the right does.
        Assert.AreEqual(1, TrayClickPolicy.ToLegacyBehavior(TrayClickAction.ShowFlyout, TrayClickAction.MuteUnmute));
        Assert.AreEqual(1, TrayClickPolicy.ToLegacyBehavior(TrayClickAction.ShowFlyout, TrayClickAction.None));

        // Flyout on the right, or on both sides, is the default layout's shape.
        Assert.AreEqual(0, TrayClickPolicy.ToLegacyBehavior(TrayClickAction.MuteUnmute, TrayClickAction.ShowFlyout));
        Assert.AreEqual(0, TrayClickPolicy.ToLegacyBehavior(TrayClickAction.None, TrayClickAction.ShowFlyout));
        Assert.AreEqual(0, TrayClickPolicy.ToLegacyBehavior(TrayClickAction.ShowFlyout, TrayClickAction.ShowFlyout));
    }

    [TestMethod]
    public void MigratedPair_AlwaysKeepsFlyoutReachable()
    {
        // Whatever the old switch held, the migrated pair must already satisfy the guard, so
        // migration never has to alter a layout the user chose.
        foreach (int legacy in new[] { -1, 0, 1, 2 })
        {
            (TrayClickAction left, TrayClickAction right) = TrayClickPolicy.FromLegacyBehavior(legacy);
            TrayClickAssignments migrated = Assignments(left, right);
            Assert.AreEqual(migrated, TrayClickPolicy.EnsureFlyoutReachable(TrayClickButton.Left, migrated));
        }
    }

    [TestMethod]
    public void Parse_UndefinedValue_FallsBack()
    {
        Assert.AreEqual(TrayClickAction.MuteUnmute, TrayClickPolicy.Parse(3, TrayClickAction.None));
        Assert.AreEqual(TrayClickAction.ShowFlyout, TrayClickPolicy.Parse(99, TrayClickAction.ShowFlyout));
        Assert.AreEqual(TrayClickAction.ShowFlyout, TrayClickPolicy.Parse(-1, TrayClickAction.ShowFlyout));
    }

    private static TrayClickAssignments Assignments(
        TrayClickAction left,
        TrayClickAction right,
        TrayClickAction leftDouble = TrayClickAction.None,
        TrayClickAction rightDouble = TrayClickAction.None) => new(left, right, leftDouble, rightDouble);

    [TestMethod]
    public void EnsureFlyoutReachable_LeavesAssignmentsAloneWhenASingleClickOpensFlyout()
    {
        TrayClickAssignments input = Assignments(TrayClickAction.MuteUnmute, TrayClickAction.ShowFlyout);

        Assert.AreEqual(input, TrayClickPolicy.EnsureFlyoutReachable(TrayClickButton.Left, input));
    }

    [TestMethod]
    public void EnsureFlyoutReachable_ADoubleClickFlyoutSatisfiesTheGuard()
    {
        // The user has deliberately put the flyout on a double-click; both single clicks are
        // free for other things and must not be overridden.
        TrayClickAssignments input = Assignments(
            TrayClickAction.PlayPause, TrayClickAction.MuteUnmute,
            leftDouble: TrayClickAction.ShowFlyout);

        Assert.AreEqual(input, TrayClickPolicy.EnsureFlyoutReachable(TrayClickButton.Right, input));

        input = Assignments(
            TrayClickAction.PlayPause, TrayClickAction.MuteUnmute,
            rightDouble: TrayClickAction.ShowFlyout);

        Assert.AreEqual(input, TrayClickPolicy.EnsureFlyoutReachable(TrayClickButton.Left, input));
    }

    [TestMethod]
    public void EnsureFlyoutReachable_ChangingLeftAwayFromFlyout_MovesFlyoutToRight()
    {
        // Left was the flyout and the user gave it to play/pause; right was something else.
        TrayClickAssignments result = TrayClickPolicy.EnsureFlyoutReachable(
            TrayClickButton.Left,
            Assignments(TrayClickAction.PlayPause, TrayClickAction.ToggleMiniPlayer));

        Assert.AreEqual(TrayClickAction.PlayPause, result.Left, "the slot the user changed keeps their choice");
        Assert.AreEqual(TrayClickAction.ShowFlyout, result.Right);
    }

    [TestMethod]
    public void EnsureFlyoutReachable_ChangingRightAwayFromFlyout_MovesFlyoutToLeft()
    {
        TrayClickAssignments result = TrayClickPolicy.EnsureFlyoutReachable(
            TrayClickButton.Right,
            Assignments(TrayClickAction.PlayPause, TrayClickAction.FavoriteTrack));

        Assert.AreEqual(TrayClickAction.ShowFlyout, result.Left);
        Assert.AreEqual(TrayClickAction.FavoriteTrack, result.Right, "the slot the user changed keeps their choice");
    }

    [TestMethod]
    public void EnsureFlyoutReachable_TakingFlyoutOffADoubleClick_MovesItToASingleClick()
    {
        // The flyout lived on the left double-click and the user reassigned that slot. Repair
        // goes to a single-click button (right first), never to the other double-click: a
        // flyout only reachable by double-click is hidden and adds a delay the user did not ask
        // for.
        TrayClickAssignments result = TrayClickPolicy.EnsureFlyoutReachable(
            TrayClickButton.LeftDouble,
            Assignments(TrayClickAction.PlayPause, TrayClickAction.MuteUnmute, leftDouble: TrayClickAction.FavoriteTrack));

        Assert.AreEqual(TrayClickAction.FavoriteTrack, result.LeftDouble, "the slot the user changed keeps their choice");
        Assert.AreEqual(TrayClickAction.ShowFlyout, result.Right);
        Assert.AreEqual(TrayClickAction.PlayPause, result.Left);
        Assert.AreEqual(TrayClickAction.None, result.RightDouble);
    }

    [TestMethod]
    public void EnsureFlyoutReachable_NeverPicksADoubleClickAsTheRepair()
    {
        // Even with both double-click slots free, a single-click button is overridden instead.
        TrayClickAssignments result = TrayClickPolicy.EnsureFlyoutReachable(
            TrayClickButton.Right,
            Assignments(TrayClickAction.PlayPause, TrayClickAction.MuteUnmute));

        Assert.AreEqual(TrayClickAction.ShowFlyout, result.Left);
        Assert.AreEqual(TrayClickAction.None, result.LeftDouble);
        Assert.AreEqual(TrayClickAction.None, result.RightDouble);
    }

    [TestMethod]
    public void EnsureFlyoutReachable_NoneEverywhere_StillRestoresFlyout()
    {
        TrayClickAssignments result = TrayClickPolicy.EnsureFlyoutReachable(
            TrayClickButton.Left,
            Assignments(TrayClickAction.None, TrayClickAction.None));

        Assert.AreEqual(TrayClickAction.None, result.Left);
        Assert.AreEqual(TrayClickAction.ShowFlyout, result.Right);
    }

    [TestMethod]
    public void Assignments_IndexerAndWith_CoverEverySlot()
    {
        TrayClickAssignments a = Assignments(TrayClickAction.None, TrayClickAction.None);

        foreach (TrayClickButton button in System.Enum.GetValues<TrayClickButton>())
        {
            TrayClickAssignments changed = a.With(button, TrayClickAction.MuteUnmute);
            Assert.AreEqual(TrayClickAction.MuteUnmute, changed[button], button.ToString());
            Assert.IsFalse(changed.OpensFlyout);
            Assert.IsTrue(a.With(button, TrayClickAction.ShowFlyout).OpensFlyout, button.ToString());
        }
    }

    [TestMethod]
    public void Resolve_WithStation_RunsConfiguredAction()
    {
        foreach (TrayClickAction action in System.Enum.GetValues<TrayClickAction>())
            Assert.AreEqual(action, TrayClickPolicy.Resolve(action, canPlay: true));
    }

    [TestMethod]
    public void Resolve_WithoutStation_StationActionsFallBackToFlyout()
    {
        Assert.AreEqual(TrayClickAction.ShowFlyout, TrayClickPolicy.Resolve(TrayClickAction.PlayPause, canPlay: false));
        Assert.AreEqual(TrayClickAction.ShowFlyout, TrayClickPolicy.Resolve(TrayClickAction.MuteUnmute, canPlay: false));
        Assert.AreEqual(TrayClickAction.ShowFlyout, TrayClickPolicy.Resolve(TrayClickAction.FavoriteTrack, canPlay: false));
        Assert.AreEqual(TrayClickAction.ShowFlyout, TrayClickPolicy.Resolve(TrayClickAction.ShowTrackInfo, canPlay: false));
        Assert.AreEqual(TrayClickAction.ShowFlyout, TrayClickPolicy.Resolve(TrayClickAction.ShowFlyout, canPlay: false));
    }

    [TestMethod]
    public void Resolve_WithoutStation_NoneAndMiniPlayerStandOnTheirOwn()
    {
        // "Do nothing" must mean nothing even on a fresh install, and the mini player has an
        // idle state of its own to show.
        Assert.AreEqual(TrayClickAction.None, TrayClickPolicy.Resolve(TrayClickAction.None, canPlay: false));
        Assert.AreEqual(TrayClickAction.ToggleMiniPlayer, TrayClickPolicy.Resolve(TrayClickAction.ToggleMiniPlayer, canPlay: false));
    }

    [TestMethod]
    public void PlayPauseButton_NamesTheButtonThatPlays()
    {
        Assert.AreEqual(TrayClickButton.Left, TrayClickPolicy.PlayPauseButton(TrayClickAction.PlayPause, TrayClickAction.ShowFlyout));
        Assert.AreEqual(TrayClickButton.Right, TrayClickPolicy.PlayPauseButton(TrayClickAction.ShowFlyout, TrayClickAction.PlayPause));
        Assert.IsNull(TrayClickPolicy.PlayPauseButton(TrayClickAction.ShowFlyout, TrayClickAction.MuteUnmute));
    }

    [TestMethod]
    public void SingleClick_IsOnlyDeferredWhenDoubleClickIsAssigned()
    {
        // The double-click delay is the price of using double-click on that button, not of
        // the feature existing: the default (unassigned) slot must leave single clicks instant.
        Assert.AreEqual(TrayClickAction.None, TrayClickPolicy.DefaultDoubleClickAction);
        Assert.IsFalse(TrayClickPolicy.ShouldDeferSingleClick(TrayClickPolicy.DefaultDoubleClickAction));

        foreach (TrayClickAction action in System.Enum.GetValues<TrayClickAction>())
        {
            if (action != TrayClickAction.None)
                Assert.IsTrue(TrayClickPolicy.ShouldDeferSingleClick(action), action.ToString());
        }
    }

    [TestMethod]
    public void PlayPauseButton_LeftWinsWhenBothPlay()
    {
        Assert.AreEqual(TrayClickButton.Left, TrayClickPolicy.PlayPauseButton(TrayClickAction.PlayPause, TrayClickAction.PlayPause));
    }
}
