using System;

namespace Trdo.Services;

/// <summary>
/// Decides whether the search page should keep showing what's already there or start fresh,
/// based on how long the user has been away. A quick trip to the edit page and back should keep
/// the results on screen; coming back several minutes later means the search has probably gone
/// stale and the page should look like a new visit.
/// </summary>
public static class SearchFreshnessPolicy
{
    /// <summary>How long a search stays fresh after the user navigates away from the page.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public static bool IsStale(DateTimeOffset leftUtc, DateTimeOffset nowUtc, TimeSpan? staleAfter = null) =>
        nowUtc - leftUtc > (staleAfter ?? StaleAfter);
}
