using System;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// Pure decision logic for the tray icon's mouse wheel. Kept free of WinRT/WinUI dependencies
/// so it can be unit tested directly (see Trdo.Tests).
/// </summary>
public static class TrayWheelPolicy
{
    /// <summary>Wheel units in one notch of a conventional wheel (<c>WHEEL_DELTA</c>).</summary>
    public const int NotchDelta = 120;

    /// <summary>Out-of-the-box: the wheel does nothing, so the taskbar keeps every scroll.</summary>
    public const TrayWheelAction DefaultAction = TrayWheelAction.None;

    /// <summary>Volume change per notch, in percentage points of the 0-200 volume scale.</summary>
    public const double VolumeStepPercent = 5;

    public const double MinVolumePercent = 0;
    public const double MaxVolumePercent = 200;

    /// <summary>
    /// Reads a stored integer back as an action, falling back to <paramref name="fallback"/>
    /// for anything that is not a defined value.
    /// </summary>
    public static TrayWheelAction Parse(int stored, TrayWheelAction fallback) =>
        Enum.IsDefined(typeof(TrayWheelAction), stored) ? (TrayWheelAction)stored : fallback;

    /// <summary>
    /// Folds a wheel delta into <paramref name="pending"/> and returns the whole notches that
    /// have now accumulated (positive is scrolling up, away from the user). High-resolution
    /// wheels and touchpads report fractions of a notch, which would otherwise be thrown away
    /// or each count as a full step. Turning the other way discards what was pending, so a
    /// reversal responds at once instead of first working off the old direction's remainder.
    /// </summary>
    public static int TakeNotches(ref int pending, int delta)
    {
        if (delta == 0)
            return 0;

        if (pending != 0 && Math.Sign(pending) != Math.Sign(delta))
            pending = 0;

        pending += delta;
        int notches = pending / NotchDelta; // truncates toward zero
        pending -= notches * NotchDelta;
        return notches;
    }

    /// <summary>
    /// The new volume (percent, 0-200) after <paramref name="notches"/> of scrolling: up raises it.
    /// </summary>
    public static double StepVolumePercent(double currentPercent, int notches) =>
        Math.Clamp(currentPercent + (notches * VolumeStepPercent), MinVolumePercent, MaxVolumePercent);

    /// <summary>
    /// How many stations to move by for <paramref name="notches"/> of scrolling. Scrolling down
    /// moves forward through the list, as it does in the station list itself, so down is next
    /// and up is previous; the result is positive for next, negative for previous.
    /// </summary>
    public static int StationSteps(int notches) => -notches;
}
