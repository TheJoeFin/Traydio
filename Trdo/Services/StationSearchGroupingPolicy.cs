using System;
using System.Collections.Generic;
using System.Linq;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// Groups Radio Browser search results that share a station name, so mirrors of the same
/// station show up as one card with several streams instead of several near-identical rows.
/// <para>
/// Grouping is on name alone, the same signal a person searching by name is going on. Folding
/// in country or language too would split a station's own relays back apart whenever the
/// directory disagrees with itself about one of those fields.
/// </para>
/// </summary>
public static class StationSearchGroupingPolicy
{
    public static List<StationSearchResultGroup> Group(IReadOnlyList<RadioBrowserStation> stations)
    {
        Dictionary<string, List<RadioBrowserStation>> byName = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];

        foreach (RadioBrowserStation station in stations)
        {
            string key = station.Name.Trim();
            if (!byName.TryGetValue(key, out List<RadioBrowserStation>? bucket))
            {
                bucket = [];
                byName[key] = bucket;
                order.Add(key);
            }

            bucket.Add(station);
        }

        List<StationSearchResultGroup> groups = [];
        foreach (string key in order)
        {
            List<RadioBrowserStation> streams = byName[key]
                .OrderByDescending(station => station.Bitrate)
                .ThenByDescending(station => station.Votes)
                .ToList();

            groups.Add(new StationSearchResultGroup(streams));
        }

        return groups;
    }
}
