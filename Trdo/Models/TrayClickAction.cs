namespace Trdo.Models;

/// <summary>
/// What a click on the tray icon does. Left and right click each get their own, chosen in
/// Settings.
/// <para>
/// Values are explicit because this is persisted as an integer, following the same pattern as
/// <see cref="StationSortMode"/>.
/// </para>
/// </summary>
public enum TrayClickAction
{
    /// <summary>The click is ignored.</summary>
    None = 0,

    /// <summary>Toggles playback of the selected station.</summary>
    PlayPause = 1,

    /// <summary>Opens (or closes) the main Traydio flyout.</summary>
    ShowFlyout = 2,

    /// <summary>Silences the output without touching the saved volume.</summary>
    MuteUnmute = 3,

    /// <summary>Adds the current track to Favorite Songs, or removes it again.</summary>
    FavoriteTrack = 4,

    /// <summary>Shows the now-playing pill for the current track.</summary>
    ShowTrackInfo = 5,

    /// <summary>Opens the mini player, or closes it when it is already open.</summary>
    ToggleMiniPlayer = 6
}
