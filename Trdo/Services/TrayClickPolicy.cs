using System;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>Which of the tray icon's four click slots a setting is about.</summary>
public enum TrayClickButton
{
    Left,
    Right,
    LeftDouble,
    RightDouble
}

/// <summary>
/// Everything assigned to the tray icon at once, so rules that span the slots - "something
/// must open Traydio" - can look at all of them together.
/// </summary>
public readonly record struct TrayClickAssignments(
    TrayClickAction Left,
    TrayClickAction Right,
    TrayClickAction LeftDouble,
    TrayClickAction RightDouble)
{
    public TrayClickAction this[TrayClickButton button] => button switch
    {
        TrayClickButton.Left => Left,
        TrayClickButton.Right => Right,
        TrayClickButton.LeftDouble => LeftDouble,
        TrayClickButton.RightDouble => RightDouble,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    public TrayClickAssignments With(TrayClickButton button, TrayClickAction action) => button switch
    {
        TrayClickButton.Left => this with { Left = action },
        TrayClickButton.Right => this with { Right = action },
        TrayClickButton.LeftDouble => this with { LeftDouble = action },
        TrayClickButton.RightDouble => this with { RightDouble = action },
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    /// <summary>Whether any slot, single or double, opens the flyout.</summary>
    public bool OpensFlyout =>
        Left == TrayClickAction.ShowFlyout ||
        Right == TrayClickAction.ShowFlyout ||
        LeftDouble == TrayClickAction.ShowFlyout ||
        RightDouble == TrayClickAction.ShowFlyout;
}

/// <summary>
/// Pure decision logic for the tray icon's configurable clicks. Kept free of WinRT/WinUI
/// dependencies so it can be unit tested directly (see Trdo.Tests).
/// </summary>
public static class TrayClickPolicy
{
    /// <summary>Out-of-the-box assignment: left click plays/pauses, right click opens Traydio.</summary>
    public const TrayClickAction DefaultLeftAction = TrayClickAction.PlayPause;

    /// <inheritdoc cref="DefaultLeftAction"/>
    public const TrayClickAction DefaultRightAction = TrayClickAction.ShowFlyout;

    /// <summary>
    /// Double-clicks do nothing until assigned. Windows delivers the single click before the
    /// double-click, so honouring a double-click means holding the single click back for the
    /// double-click interval; that delay is only worth paying once the user has opted in.
    /// </summary>
    public const TrayClickAction DefaultDoubleClickAction = TrayClickAction.None;

    /// <summary>
    /// Whether a button's single click has to wait for a possible double-click before acting.
    /// Only when the double-click slot is actually assigned; otherwise clicks stay instant.
    /// </summary>
    public static bool ShouldDeferSingleClick(TrayClickAction doubleClickAction) =>
        doubleClickAction != TrayClickAction.None;

    /// <summary>
    /// Maps the pre-2.1 two-way <c>TrayClickBehavior</c> setting (0 = default, 1 = swapped) onto
    /// a per-button pair, so an existing choice survives the upgrade to independent buttons.
    /// Anything other than 1 reads as the default, matching how the old setter clamped it.
    /// </summary>
    public static (TrayClickAction Left, TrayClickAction Right) FromLegacyBehavior(int legacyBehavior)
    {
        return legacyBehavior == 1
            ? (DefaultRightAction, DefaultLeftAction)
            : (DefaultLeftAction, DefaultRightAction);
    }

    /// <summary>
    /// The pre-2.1 value that comes closest to a per-button pair, written back so a downgrade
    /// keeps a familiar layout. The old switch only knew "left plays, right opens" (0) and the
    /// reverse (1); the swapped form is chosen only when the pair actually is that layout's
    /// defining half - a flyout on the left - and the default otherwise, which is also what the
    /// old build would have done with any value it did not understand.
    /// </summary>
    public static int ToLegacyBehavior(TrayClickAction left, TrayClickAction right)
    {
        return left == TrayClickAction.ShowFlyout && right != TrayClickAction.ShowFlyout ? 1 : 0;
    }

    /// <summary>
    /// Reads a stored integer back as an action, falling back to <paramref name="fallback"/> for
    /// anything that is not a defined value.
    /// </summary>
    public static TrayClickAction Parse(int stored, TrayClickAction fallback) =>
        Enum.IsDefined(typeof(TrayClickAction), stored) ? (TrayClickAction)stored : fallback;

    /// <summary>
    /// Slots the guard may hand the flyout to, most preferred first. Single clicks come before
    /// double-clicks: a flyout that only opens on a double-click is hard to discover and makes
    /// that button's single click wait, so it is accepted when the user set it up that way but
    /// never chosen for them.
    /// </summary>
    private static readonly TrayClickButton[] FlyoutHomes =
    [
        TrayClickButton.Right,
        TrayClickButton.Left,
        TrayClickButton.RightDouble,
        TrayClickButton.LeftDouble
    ];

    /// <summary>
    /// Keeps the flyout reachable. Traydio lives entirely in the tray: if nothing opened it
    /// there would be no way back to Settings to undo the choice. Any of the four slots counts,
    /// including a double-click. When the slot the user just changed leaves no flyout anywhere,
    /// the flyout is moved to the most preferred <em>other</em> slot (see
    /// <see cref="FlyoutHomes"/>), so the user's own selection is always honoured.
    /// </summary>
    /// <returns>The set to store, which differs from the input only when the guard had to act.</returns>
    public static TrayClickAssignments EnsureFlyoutReachable(
        TrayClickButton changed,
        TrayClickAssignments assignments)
    {
        if (assignments.OpensFlyout)
            return assignments;

        foreach (TrayClickButton home in FlyoutHomes)
        {
            if (home != changed)
                return assignments.With(home, TrayClickAction.ShowFlyout);
        }

        return assignments;
    }

    /// <summary>
    /// The action to actually run for a click. Everything that needs a station to act on
    /// falls back to opening the flyout while there is none, which both explains the silence
    /// and puts the "add a station" UI in front of the user; the mini player and "no action"
    /// stand on their own.
    /// </summary>
    public static TrayClickAction Resolve(TrayClickAction configured, bool canPlay)
    {
        if (canPlay)
            return configured;

        return configured switch
        {
            TrayClickAction.None => TrayClickAction.None,
            TrayClickAction.ToggleMiniPlayer => TrayClickAction.ToggleMiniPlayer,
            _ => TrayClickAction.ShowFlyout
        };
    }

    /// <summary>
    /// Which button the tooltip should tell the user to press to play or pause, or
    /// <see langword="null"/> when neither does. Left wins if both are assigned, since that is
    /// the button people try first.
    /// </summary>
    public static TrayClickButton? PlayPauseButton(TrayClickAction left, TrayClickAction right)
    {
        if (left == TrayClickAction.PlayPause)
            return TrayClickButton.Left;

        if (right == TrayClickAction.PlayPause)
            return TrayClickButton.Right;

        return null;
    }
}
