using System;
using System.Collections.Generic;
using Trdo.Models;
using Trdo.Services.Playback;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Trdo.Services;

/// <summary>
/// Centralized service for managing application settings
/// </summary>
public static class SettingsService
{
    private const string IsFirstRunKey = "IsFirstRun";
    private const string IsVolumeSliderVisibleKey = "IsVolumeSliderVisible";
    private const string AutoPlayOnStartupKey = "AutoPlayOnStartup";
    private const string AppLanguageKey = "AppLanguage";
    private const string IsSpotifyEnabledKey = "IsSpotifyEnabled";
    private const string IsDiscogsEnabledKey = "IsDiscogsEnabled";
    private const string IsAppleMusicEnabledKey = "IsAppleMusicEnabled";
    private const string IsYouTubeMusicEnabledKey = "IsYouTubeMusicEnabled";
    private const string IsBandcampEnabledKey = "IsBandcampEnabled";
    private const string IsLastfmScrobblingEnabledKey = "IsLastfmScrobblingEnabled";
    private const string LastfmUsernameKey = "LastfmUsername";
    private const string TrayClickBehaviorKey = "TrayClickBehavior";
    private const string TrayLeftClickActionKey = "TrayLeftClickAction";
    private const string TrayRightClickActionKey = "TrayRightClickAction";
    private const string TrayLeftDoubleClickActionKey = "TrayLeftDoubleClickAction";
    private const string TrayRightDoubleClickActionKey = "TrayRightDoubleClickAction";
    private const string PlaybackEngineModeKey = "PlaybackEngineMode";
    private const string IsMiniPlayerVisualizerEnabledKey = "IsMiniPlayerVisualizerEnabled";
    private const string IsMiniPlayerTopmostKey = "IsMiniPlayerTopmost";
    private const string IsMiniPlayerTitleBarHiddenKey = "IsMiniPlayerTitleBarHidden";
    private const string AllowSleepWhilePlayingKey = "AllowSleepWhilePlaying";
    private const string IsSongChangePopupEnabledKey = "IsSongChangePopupEnabled";
    private const string SongChangePopupDelaySecondsKey = "SongChangePopupDelaySeconds";
    private const string SongChangePopupDwellSecondsKey = "SongChangePopupDwellSeconds";
    private const string StationSortModeKey = "StationSortMode";
    private const string StationGroupByModeKey = "StationGroupByMode";
    private const string IsRadioStaticEnabledKey = "IsRadioStaticEnabled";
    private const string LocalMusicLoopModeKey = "LocalMusicLoopMode";
    private const string RecentStationSearchesKey = "RecentStationSearches";

    /// <summary>
    /// Separates entries in <see cref="RecentStationSearches"/>. A control character rather than
    /// a punctuation mark, so it can't collide with anything a search term could contain.
    /// </summary>
    private const char RecentStationSearchesSeparator = '\u001f';

    public static event EventHandler? MusicSearchServicesChanged;

    /// <summary>
    /// Raised when <see cref="IsSongChangePopupEnabled"/> changes, so a popup
    /// that is currently on screen can be dismissed the moment the user turns
    /// the feature off.
    /// </summary>
    public static event EventHandler? SongChangePopupEnabledChanged;

    /// <summary>
    /// Raised when <see cref="TrackInfoDelaySeconds"/> changes, so the player can re-time a
    /// track it is already holding rather than only the next one.
    /// </summary>
    public static event EventHandler? TrackInfoDelayChanged;

    /// <summary>
    /// Raised when <see cref="IsRadioStaticEnabled"/> changes, so static that is already playing
    /// can be faded out the moment the user turns the feature off.
    /// </summary>
    public static event EventHandler? RadioStaticEnabledChanged;

    /// <summary>
    /// Raised whenever the connected Last.fm account (<see cref="LastfmUsername"/>) changes -
    /// connected, disconnected, or a session Last.fm rejected was cleared - so Settings can
    /// refresh its connection status without polling.
    /// </summary>
    public static event EventHandler? LastfmConnectionChanged;

    /// <summary>
    /// Gets or sets whether the app should automatically start playing the last selected station on startup.
    /// Defaults to false when no saved value exists.
    /// </summary>
    public static bool AutoPlayOnStartup
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(AutoPlayOnStartupKey, out object? value))
                {
                    return value switch
                    {
                        bool b => b,
                        string s when bool.TryParse(s, out bool b2) => b2,
                        _ => false
                    };
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[AutoPlayOnStartupKey] = value;
            }
            catch
            {
                // Silently fail if unable to save
            }
        }
    }

    public static string AppLanguage
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(AppLanguageKey, out object? value))
                {
                    if (value is string language && !string.IsNullOrWhiteSpace(language))
                    {
                        return language;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("Localization", $"Failed to read saved language, falling back to system: {ex.Message}");
            }

            return LocalizationService.SystemLanguage;
        }
        set
        {
            try
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? LocalizationService.SystemLanguage : value;
                ApplicationData.Current.LocalSettings.Values[AppLanguageKey] = normalized;
                LogService.Info("Localization", $"Saved language setting: '{normalized}'");
                LocalizationService.ApplyLanguage(normalized);
            }
            catch (Exception ex)
            {
                LogService.Warn("Localization", $"Failed to save language setting '{value}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the volume slider is visible on the playing page.
    /// Defaults to true when no saved value exists.
    /// </summary>
    public static bool IsVolumeSliderVisible
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(IsVolumeSliderVisibleKey, out object? value))
                {
                    return value switch
                    {
                        bool b => b,
                        string s when bool.TryParse(s, out bool b2) => b2,
                        _ => true
                    };
                }
                return true;
            }
            catch
            {
                return true;
            }
        }
        set
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[IsVolumeSliderVisibleKey] = value;
            }
            catch
            {
                // Silently fail if unable to save
            }
        }
    }

    /// <summary>
    /// Gets or sets whether Spotify search links are shown.
    /// Defaults to true when no saved value exists.
    /// </summary>
    public static bool IsSpotifyEnabled
    {
        get => GetBoolSetting(IsSpotifyEnabledKey, defaultValue: true);
        set => SetBoolSetting(IsSpotifyEnabledKey, value);
    }

    /// <summary>
    /// Gets or sets whether Discogs search links are shown.
    /// Defaults to true when no saved value exists.
    /// </summary>
    public static bool IsDiscogsEnabled
    {
        get => GetBoolSetting(IsDiscogsEnabledKey, defaultValue: true);
        set => SetBoolSetting(IsDiscogsEnabledKey, value);
    }

    /// <summary>
    /// Gets or sets whether Apple Music search links are shown.
    /// Defaults to true when no saved value exists.
    /// </summary>
    public static bool IsAppleMusicEnabled
    {
        get => GetBoolSetting(IsAppleMusicEnabledKey, defaultValue: false);
        set => SetBoolSetting(IsAppleMusicEnabledKey, value);
    }

    /// <summary>
    /// Gets or sets whether YouTube Music search links are shown.
    /// Defaults to true when no saved value exists.
    /// </summary>
    public static bool IsYouTubeMusicEnabled
    {
        get => GetBoolSetting(IsYouTubeMusicEnabledKey, defaultValue: false);
        set => SetBoolSetting(IsYouTubeMusicEnabledKey, value);
    }

    /// <summary>
    /// Gets or sets whether Bandcamp search links are shown.
    /// Defaults to false when no saved value exists.
    /// </summary>
    public static bool IsBandcampEnabled
    {
        get => GetBoolSetting(IsBandcampEnabledKey, defaultValue: false);
        set => SetBoolSetting(IsBandcampEnabledKey, value);
    }

    /// <summary>
    /// Gets or sets whether the app reports what is playing to the connected Last.fm account.
    /// Defaults to false when no saved value exists: scrobbling must never start silently for a
    /// user who has not opted in, even if they have connected an account.
    /// </summary>
    public static bool IsLastfmScrobblingEnabled
    {
        get => GetBoolSetting(IsLastfmScrobblingEnabledKey, defaultValue: false);
        set => SetBoolSetting(IsLastfmScrobblingEnabledKey, value);
    }

    /// <summary>
    /// The connected Last.fm account's display name, or <see langword="null"/> when not
    /// connected. Not secret - the session key itself lives in Windows Credential Locker via
    /// <see cref="Lastfm.LastfmAccountStore"/> - so this stays a plain setting for cheap,
    /// synchronous reads from bindings.
    /// </summary>
    public static string? LastfmUsername
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(LastfmUsernameKey, out object? value) &&
                    value is string username && !string.IsNullOrWhiteSpace(username))
                {
                    return username;
                }
            }
            catch
            {
                // Fall through to "not connected"
            }

            return null;
        }
        set
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                    ApplicationData.Current.LocalSettings.Values.Remove(LastfmUsernameKey);
                else
                    ApplicationData.Current.LocalSettings.Values[LastfmUsernameKey] = value;
            }
            catch
            {
                // Silently fail if unable to save
            }

            LastfmConnectionChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets the preferred playback engine mode.
    /// 0 = Auto (LibVLC first), 1 = Native only, 3 = Native preferred.
    /// The legacy value 2 (LibVLC preferred) reads as Auto, which now has
    /// the same behavior plus a native fallback.
    /// </summary>
    public static PlaybackEngineMode PlaybackEngineMode
    {
        get
        {
            try
            {
                PlaybackEngineMode mode = PlaybackEngineMode.Auto;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(PlaybackEngineModeKey, out object? value))
                {
                    mode = value switch
                    {
                        int i when Enum.IsDefined(typeof(PlaybackEngineMode), i) => (PlaybackEngineMode)i,
                        string s when int.TryParse(s, out int parsed) && Enum.IsDefined(typeof(PlaybackEngineMode), parsed)
                            => (PlaybackEngineMode)parsed,
                        _ => PlaybackEngineMode.Auto
                    };
                }

                return mode == PlaybackEngineMode.LibVlcPreferred ? PlaybackEngineMode.Auto : mode;
            }
            catch
            {
                return PlaybackEngineMode.Auto;
            }
        }
        set
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[PlaybackEngineModeKey] = (int)value;
            }
            catch
            {
                // Silently fail if unable to save
            }
        }
    }

    /// <summary>
    /// Raised when <see cref="StationSortMode"/> changes, so the station list can re-render
    /// and switch dragging on or off.
    /// </summary>
    public static event EventHandler? StationSortModeChanged;

    /// <summary>
    /// Gets or sets how the station list is ordered on screen.
    /// <para>
    /// A view setting, not a data one: anything other than
    /// <see cref="Models.StationSortMode.Manual"/> changes what is drawn and leaves the saved
    /// order, folders and dividers untouched.
    /// </para>
    /// </summary>
    public static Models.StationSortMode StationSortMode
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(StationSortModeKey, out object? value))
                {
                    return value switch
                    {
                        int i when Enum.IsDefined(typeof(Models.StationSortMode), i) => (Models.StationSortMode)i,
                        string s when int.TryParse(s, out int parsed) && Enum.IsDefined(typeof(Models.StationSortMode), parsed)
                            => (Models.StationSortMode)parsed,
                        _ => Models.StationSortMode.Manual
                    };
                }
            }
            catch
            {
                // Fall through to the default
            }

            return Models.StationSortMode.Manual;
        }
        set
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[StationSortModeKey] = (int)value;
            }
            catch
            {
                // Silently fail if unable to save
            }

            StationSortModeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets how the station list is grouped into folders on screen.
    /// <para>
    /// A view setting, not a data one, following the same reasoning as
    /// <see cref="StationSortMode"/>: anything other than
    /// <see cref="Models.StationGroupByMode.None"/> changes what is drawn and leaves the saved
    /// folders, dividers and order untouched.
    /// </para>
    /// </summary>
    public static Models.StationGroupByMode StationGroupByMode
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(StationGroupByModeKey, out object? value))
                {
                    return value switch
                    {
                        int i when Enum.IsDefined(typeof(Models.StationGroupByMode), i) => (Models.StationGroupByMode)i,
                        string s when int.TryParse(s, out int parsed) && Enum.IsDefined(typeof(Models.StationGroupByMode), parsed)
                            => (Models.StationGroupByMode)parsed,
                        _ => Models.StationGroupByMode.None
                    };
                }
            }
            catch
            {
                // Fall through to the default
            }

            return Models.StationGroupByMode.None;
        }
        set
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[StationGroupByModeKey] = (int)value;
            }
            catch
            {
                // Silently fail if unable to save
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the spectrum visualizer is shown in the mini player.
    /// Defaults to true when no saved value exists.
    /// </summary>
    public static bool IsMiniPlayerVisualizerEnabled
    {
        get => GetBoolSetting(IsMiniPlayerVisualizerEnabledKey, defaultValue: true);
        set => SetBoolSetting(IsMiniPlayerVisualizerEnabledKey, value);
    }

    /// <summary>
    /// Gets or sets whether the mini player window stays on top of other windows.
    /// Defaults to true when no saved value exists.
    /// </summary>
    public static bool IsMiniPlayerTopmost
    {
        get => GetBoolSetting(IsMiniPlayerTopmostKey, defaultValue: true);
        set => SetBoolSetting(IsMiniPlayerTopmostKey, value);
    }

    /// <summary>
    /// Gets or sets whether the mini player's title/subtitle bar is hidden. The window's native
    /// close button stays put either way - only the title text and icon go away.
    /// Defaults to false when no saved value exists.
    /// </summary>
    public static bool IsMiniPlayerTitleBarHidden
    {
        get => GetBoolSetting(IsMiniPlayerTitleBarHiddenKey, defaultValue: false);
        set => SetBoolSetting(IsMiniPlayerTitleBarHiddenKey, value);
    }

    /// <summary>
    /// Raised when either tray click assignment changes, so the tray tooltip can re-describe
    /// which button plays and pauses.
    /// </summary>
    public static event EventHandler? TrayClickActionsChanged;

    /// <summary>
    /// Gets or sets what a left click on the tray icon does.
    /// </summary>
    /// <remarks>
    /// Before 2.1 both buttons were governed by a single two-way <c>TrayClickBehavior</c>
    /// switch. <see cref="MigrateTrayClickActions"/> turns that into per-button values once at
    /// startup; the legacy-derived fallback here only matters if that could not run.
    /// </remarks>
    public static TrayClickAction TrayLeftClickAction
    {
        get => GetTrayClickAction(TrayLeftClickActionKey, LegacyTrayClickActions().Left);
        set => SetTrayClickAction(TrayLeftClickActionKey, value);
    }

    /// <summary>
    /// Gets or sets what a right click on the tray icon does.
    /// </summary>
    /// <remarks><inheritdoc cref="TrayLeftClickAction" path="/remarks"/></remarks>
    public static TrayClickAction TrayRightClickAction
    {
        get => GetTrayClickAction(TrayRightClickActionKey, LegacyTrayClickActions().Right);
        set => SetTrayClickAction(TrayRightClickActionKey, value);
    }

    /// <summary>
    /// Gets or sets what a left double-click on the tray icon does. Off by default: assigning
    /// it makes that button's single click wait out the double-click interval before acting,
    /// and users who never asked for that keep instant clicks. New in 2.1, so no migration.
    /// </summary>
    public static TrayClickAction TrayLeftDoubleClickAction
    {
        get => GetTrayClickAction(TrayLeftDoubleClickActionKey, TrayClickPolicy.DefaultDoubleClickAction);
        set => SetTrayClickAction(TrayLeftDoubleClickActionKey, value);
    }

    /// <summary>Gets or sets what a right double-click on the tray icon does. See <see cref="TrayLeftDoubleClickAction"/>.</summary>
    public static TrayClickAction TrayRightDoubleClickAction
    {
        get => GetTrayClickAction(TrayRightDoubleClickActionKey, TrayClickPolicy.DefaultDoubleClickAction);
        set => SetTrayClickAction(TrayRightDoubleClickActionKey, value);
    }

    /// <summary>
    /// One-shot upgrade of the pre-2.1 <c>TrayClickBehavior</c> switch into the per-button
    /// keys. Runs at startup, before anything reads the tray settings.
    /// </summary>
    /// <remarks>
    /// Both per-button keys are written together so the pair never half-exists: a user who
    /// later changes only one button must not have the other silently fall back to a default
    /// if the old key is ever cleaned up. A per-button key that already exists is kept - it is
    /// newer than anything the old switch can say. The old key itself is left in place, and
    /// <see cref="SetTrayClickAction"/> keeps it current, so a downgrade to a pre-2.1 build
    /// still sees the nearest two-way equivalent of the current layout.
    /// </remarks>
    public static void MigrateTrayClickActions()
    {
        try
        {
            IPropertySet values = ApplicationData.Current.LocalSettings.Values;
            bool hasLeft = values.ContainsKey(TrayLeftClickActionKey);
            bool hasRight = values.ContainsKey(TrayRightClickActionKey);
            if (hasLeft && hasRight)
                return;

            (TrayClickAction legacyLeft, TrayClickAction legacyRight) = LegacyTrayClickActions();
            TrayClickAction left = hasLeft ? GetTrayClickAction(TrayLeftClickActionKey, legacyLeft) : legacyLeft;
            TrayClickAction right = hasRight ? GetTrayClickAction(TrayRightClickActionKey, legacyRight) : legacyRight;

            // The old switch could only ever express layouts with a flyout on one side, so a
            // migrated pair always passes this; it is here for the half-written case. The
            // double-click slots are new in 2.1 and read as unassigned here, so only the two
            // single-click buttons can satisfy or receive the flyout.
            TrayClickAssignments migrated = TrayClickPolicy.EnsureFlyoutReachable(
                TrayClickButton.Left,
                new TrayClickAssignments(left, right, TrayClickPolicy.DefaultDoubleClickAction, TrayClickPolicy.DefaultDoubleClickAction));
            (left, right) = (migrated.Left, migrated.Right);

            values[TrayLeftClickActionKey] = (int)left;
            values[TrayRightClickActionKey] = (int)right;

            LogService.Info("Settings",
                $"Migrated tray click behavior (legacy={ReadLegacyTrayClickBehavior()?.ToString() ?? "<unset>"}) " +
                $"-> left={left}, right={right}");
        }
        catch (Exception ex)
        {
            // Nothing is lost: the getters keep deriving from the old key until this succeeds.
            LogService.Warn("Settings", $"Tray click migration failed; using legacy fallback: {ex.Message}");
        }
    }

    /// <summary>
    /// The raw pre-2.1 value, or <see langword="null"/> when it was never set. Anything that
    /// cannot be read as an integer counts as unset, matching how the old getter defaulted.
    /// </summary>
    private static int? ReadLegacyTrayClickBehavior()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(TrayClickBehaviorKey, out object? value))
            {
                return value switch
                {
                    int i => i,
                    string s when int.TryParse(s, out int parsed) => parsed,
                    _ => null
                };
            }
        }
        catch
        {
            // Fall through to unset
        }

        return null;
    }

    private static (TrayClickAction Left, TrayClickAction Right) LegacyTrayClickActions() =>
        TrayClickPolicy.FromLegacyBehavior(ReadLegacyTrayClickBehavior() ?? 0);

    private static TrayClickAction GetTrayClickAction(string key, TrayClickAction fallback)
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value))
            {
                return value switch
                {
                    int i => TrayClickPolicy.Parse(i, fallback),
                    string s when int.TryParse(s, out int parsed) => TrayClickPolicy.Parse(parsed, fallback),
                    _ => fallback
                };
            }
        }
        catch
        {
            // Fall through to the default
        }

        return fallback;
    }

    private static void SetTrayClickAction(string key, TrayClickAction value)
    {
        try
        {
            IPropertySet values = ApplicationData.Current.LocalSettings.Values;
            values[key] = (int)value;

            // Keep the pre-2.1 key describing the nearest two-way layout, so a downgrade does
            // not land on a layout the user never chose.
            values[TrayClickBehaviorKey] = TrayClickPolicy.ToLegacyBehavior(
                TrayLeftClickAction,
                TrayRightClickAction);
        }
        catch
        {
            // Silently fail if unable to save
        }

        TrayClickActionsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets whether the PC may go to sleep while radio is playing.
    /// Defaults to false (keep the PC awake), matching pre-2.0 behavior.
    /// </summary>
    public static bool AllowSleepWhilePlaying
    {
        get => GetBoolSetting(AllowSleepWhilePlayingKey, defaultValue: false);
        set => SetBoolSetting(AllowSleepWhilePlayingKey, value);
    }

    /// <summary>
    /// Gets or sets whether a brief on-screen popup appears near the taskbar
    /// whenever the playing song changes. Opt-in; defaults to false so
    /// existing users see no new UI until they enable it.
    /// </summary>
    public static bool IsSongChangePopupEnabled
    {
        get => GetBoolSetting(IsSongChangePopupEnabledKey, defaultValue: false);
        set => SetBoolSetting(IsSongChangePopupEnabledKey, value);
    }

    /// <summary>
    /// Gets or sets how long the app holds a song change back before showing it anywhere - the
    /// window, the mini player, the media transport controls, the playlist history and the
    /// popup all wait this out together. Defaults to no delay. Stations whose metadata runs
    /// ahead of the audio can override this individually via
    /// <see cref="Models.RadioStation.SongPopupDelaySeconds"/>.
    /// </summary>
    /// <remarks>
    /// The stored key still says "popup" because the setting began life as a popup-only delay;
    /// renaming it would silently reset the value for everyone who had already chosen one.
    /// </remarks>
    public static double TrackInfoDelaySeconds
    {
        get => SongChangeAnnouncementPolicy.ClampDelay(
            GetDoubleSetting(SongChangePopupDelaySecondsKey, defaultValue: 0));
        set
        {
            SetDoubleSetting(
                SongChangePopupDelaySecondsKey,
                SongChangeAnnouncementPolicy.ClampDelay(value));
            TrackInfoDelayChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets how long the popup stays on screen once shown, in seconds. Unlike the
    /// delay this is app-wide only: it is about how fast the user reads, not about anything
    /// a particular station does.
    /// </summary>
    public static double SongChangePopupDwellSeconds
    {
        get => SongChangeAnnouncementPolicy.ClampDwell(
            GetDoubleSetting(
                SongChangePopupDwellSecondsKey,
                defaultValue: SongChangeAnnouncementPolicy.DefaultDwellSeconds));
        set => SetDoubleSetting(
            SongChangePopupDwellSecondsKey,
            SongChangeAnnouncementPolicy.ClampDwell(value));
    }

    private static double GetDoubleSetting(string key, double defaultValue)
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value))
            {
                return value switch
                {
                    double d => d,
                    float f => f,
                    int i => i,
                    string s when double.TryParse(
                        s,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double parsed) => parsed,
                    _ => defaultValue
                };
            }

            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void SetDoubleSetting(string key, double value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            // Silently fail if unable to save
        }
    }

    private static bool GetBoolSetting(string key, bool defaultValue)
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value))
            {
                return value switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out bool b2) => b2,
                    _ => defaultValue
                };
            }
            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void SetBoolSetting(string key, bool value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
            if (key is IsSpotifyEnabledKey or IsDiscogsEnabledKey or IsAppleMusicEnabledKey or IsYouTubeMusicEnabledKey or IsBandcampEnabledKey)
            {
                MusicSearchServicesChanged?.Invoke(null, EventArgs.Empty);
            }
            else if (key is IsSongChangePopupEnabledKey)
            {
                SongChangePopupEnabledChanged?.Invoke(null, EventArgs.Empty);
            }
            else if (key is IsRadioStaticEnabledKey)
            {
                RadioStaticEnabledChanged?.Invoke(null, EventArgs.Empty);
            }
        }
        catch
        {
            // Silently fail if unable to save
        }
    }

    /// <summary>
    /// Gets or sets whether generated FM radio static plays while a stream is buffering.
    /// Defaults to false when no saved value exists - existing listeners should not suddenly
    /// start hearing noise from an app they already had installed.
    /// </summary>
    public static bool IsRadioStaticEnabled
    {
        get => GetBoolSetting(IsRadioStaticEnabledKey, defaultValue: false);
        set => SetBoolSetting(IsRadioStaticEnabledKey, value);
    }

    /// <summary>Controls whether local music stops, repeats the track, or repeats the album.</summary>
    public static LocalMusicLoopMode LocalMusicLoopMode
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(LocalMusicLoopModeKey, out object? value) &&
                    value is string savedMode &&
                    Enum.TryParse(savedMode, ignoreCase: true, out LocalMusicLoopMode mode))
                {
                    return mode;
                }
            }
            catch
            {
                // Fall through to the compatibility-safe default.
            }

            return LocalMusicLoopMode.None;
        }
        set
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[LocalMusicLoopModeKey] = value.ToString();
            }
            catch
            {
                // Match the rest of the settings service when local settings are unavailable.
            }
        }
    }

    /// <summary>
    /// Gets whether this is the first run of the application
    /// </summary>
    public static bool IsFirstRun
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(IsFirstRunKey, out object? value))
                {
                    return value switch
                    {
                        bool b => b,
                        string s when bool.TryParse(s, out bool b2) => b2,
                        _ => true // Default to true if value is unexpected
                    };
                }
                // If key doesn't exist, it's the first run
                return true;
            }
            catch
            {
                // If any error occurs, default to true
                return true;
            }
        }
    }

    /// <summary>
    /// Marks that the first run has been completed
    /// </summary>
    public static void MarkFirstRunComplete()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[IsFirstRunKey] = false;
        }
        catch
        {
            // Silently fail if unable to save
        }
    }

    /// <summary>
    /// The station search terms the user has typed, most recent first - what the search page's
    /// "recent searches" chips are built from.
    /// </summary>
    public static IReadOnlyList<string> RecentStationSearches
    {
        get
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(RecentStationSearchesKey, out object? value) &&
                    value is string raw &&
                    raw.Length > 0)
                {
                    return raw.Split(RecentStationSearchesSeparator, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch
            {
                // Fall through to an empty list.
            }

            return [];
        }
    }

    /// <summary>
    /// Records a search term as the most recent one, de-duplicating and capping the list per
    /// <see cref="RecentSearchesPolicy"/>.
    /// </summary>
    public static void AddRecentStationSearch(string term)
    {
        try
        {
            IReadOnlyList<string> updated = RecentSearchesPolicy.Add(RecentStationSearches, term);
            ApplicationData.Current.LocalSettings.Values[RecentStationSearchesKey] =
                string.Join(RecentStationSearchesSeparator, updated);
        }
        catch
        {
            // Silently fail if unable to save
        }
    }
}
