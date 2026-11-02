using System;
using System.Collections.Generic;
using System.Linq;

namespace Trdo.Services.Playback;

/// <summary>
/// Names a user has given to Sonos groups. Sonos itself has no group names (a group is
/// just its rooms joined up), so these live in the app, keyed by *which rooms* are in the
/// group rather than by its coordinator: the name follows the same set of rooms even if a
/// different one ends up leading it, and stops applying once the membership changes.
/// Storage lives in SonosGroupNames.Storage.cs so this part stays unit-testable.
/// </summary>
public static partial class SonosGroupNames
{
    public const int MaxLength = 40;

    /// <summary>A stable key for a set of rooms, whatever order they are listed in.</summary>
    public static string KeyFor(IEnumerable<string> roomUuids) =>
        string.Join("|", roomUuids
            .Where(uuid => !string.IsNullOrWhiteSpace(uuid))
            .Select(uuid => uuid.Trim().ToUpperInvariant())
            .Distinct()
            .OrderBy(uuid => uuid, StringComparer.Ordinal));
}
