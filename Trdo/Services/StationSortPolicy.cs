using System;
using System.Collections.Generic;
using System.Linq;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// Orders stations for display.
/// <para>
/// Every sort here is a view sort: it produces a new sequence and never touches the stations,
/// the folders or the stored order. Switching back to <see cref="StationSortMode.Manual"/>
/// therefore restores the user's own arrangement exactly, with no undo bookkeeping.
/// </para>
/// </summary>
public static class StationSortPolicy
{
    /// <summary>
    /// Comparer used for every text key.
    /// <para>
    /// Invariant rather than current culture so the order does not depend on which machine
    /// the app is running on, and culture-aware rather than ordinal so accented names sort
    /// where a reader expects them instead of after "Z".
    /// </para>
    /// </summary>
    private static readonly StringComparer _textComparer = StringComparer.InvariantCultureIgnoreCase;

    /// <summary>
    /// Returns the stations in display order for the given mode.
    /// <para>
    /// Implemented with LINQ's <c>OrderBy</c>, which is a <em>stable</em> sort. That stability
    /// is load-bearing, not incidental: it means stations that tie on the sort key keep the
    /// user's manual order relative to each other, with no secondary key or position index to
    /// maintain.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RadioStation> Sort(IReadOnlyList<RadioStation> stations, StationSortMode mode)
    {
        if (stations is null || stations.Count == 0 || mode == StationSortMode.Manual)
            return stations ?? [];

        return mode switch
        {
            StationSortMode.Name => stations.OrderBy(s => SortKey(s.Name), _textComparer).ToList(),
            StationSortMode.Genre => SortByOptionalText(stations, s => s.PrimaryGenre),
            StationSortMode.Country => SortByOptionalText(stations, s => s.Country),
            StationSortMode.RecentlyAdded => stations
                .OrderBy(s => s.DateAdded is null)
                .ThenByDescending(s => s.DateAdded ?? DateTimeOffset.MinValue)
                .ToList(),
            _ => stations
        };
    }

    /// <summary>The mode's name, as shown in the sort menu.</summary>
    public static string DisplayName(StationSortMode mode) => mode switch
    {
        StationSortMode.Manual => LocalizedText.Get("Ui_StationSortPolicy_b0b9fe24ff", "Manual"),
        StationSortMode.Name => LocalizedText.Get("Ui_StationSortPolicy_dcd1d5223f", "Name"),
        StationSortMode.Genre => LocalizedText.Get("Ui_StationSortPolicy_6da795a866", "Genre"),
        StationSortMode.Country => LocalizedText.Get("Ui_StationSortPolicy_701d021d08", "Country"),
        StationSortMode.RecentlyAdded => LocalizedText.Get("Ui_StationSortPolicy_96ec5d338a", "Recently added"),
        _ => LocalizedText.Get("Ui_StationSortPolicy_b0b9fe24ff", "Manual")
    };

    /// <summary>
    /// The one-line explanation shown above the list while a view sort is active. It names all
    /// three things that are surprising at once - folders vanishing, dividers vanishing, and
    /// dragging being switched off - because discovering them one at a time reads like a bug.
    /// </summary>
    public static string HintText(StationSortMode mode) =>
        mode == StationSortMode.Manual
            ? string.Empty
            : LocalizedText.Format("Ui_StationSortPolicy_724ef7b174", "Sorted by {0} · groups hidden · reordering off", DisplayName(mode));

    /// <summary>
    /// Sorts on a key that is often missing, keeping stations without one at the end. A
    /// station with no genre should not lead the genre view.
    /// </summary>
    private static IReadOnlyList<RadioStation> SortByOptionalText(
        IReadOnlyList<RadioStation> stations,
        Func<RadioStation, string?> keySelector)
    {
        return stations
            .OrderBy(s => string.IsNullOrWhiteSpace(keySelector(s)))
            .ThenBy(s => SortKey(keySelector(s)), _textComparer)
            .ToList();
    }

    private static string SortKey(string? value) => value?.Trim() ?? string.Empty;
}
