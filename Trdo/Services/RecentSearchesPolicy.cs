using System;
using System.Collections.Generic;

namespace Trdo.Services;

/// <summary>
/// Maintains the small list of free-text terms shown as "recent searches" chips - most recent
/// first, capped, and de-duplicated case-insensitively so re-running the same search moves it to
/// the front instead of listing it twice.
/// </summary>
public static class RecentSearchesPolicy
{
    public const int MaxEntries = 8;

    /// <summary>Returns <paramref name="existing"/> with <paramref name="term"/> moved to the front.</summary>
    public static IReadOnlyList<string> Add(IReadOnlyList<string> existing, string term, int maxEntries = MaxEntries)
    {
        string trimmed = term?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return existing;
        }

        List<string> updated = [trimmed];
        foreach (string entry in existing)
        {
            if (!string.Equals(entry, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                updated.Add(entry);
            }
        }

        return updated.Count > maxEntries ? updated.GetRange(0, maxEntries) : updated;
    }
}
