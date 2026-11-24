namespace Trdo.Models;

/// <summary>
/// What turning the mouse wheel over the tray icon does, chosen in Settings.
/// <para>
/// Values are explicit because this is persisted as an integer, following the same pattern as
/// <see cref="TrayClickAction"/>.
/// </para>
/// </summary>
public enum TrayWheelAction
{
    /// <summary>The wheel is left alone and passes through to the taskbar as usual.</summary>
    None = 0,

    /// <summary>Raises or lowers the volume.</summary>
    Volume = 1,

    /// <summary>Moves to the next or previous station (or local track).</summary>
    SwitchStation = 2
}
