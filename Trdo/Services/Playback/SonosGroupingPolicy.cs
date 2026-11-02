using System;
using System.Collections.Generic;
using System.Linq;

namespace Trdo.Services.Playback;

public enum SonosGroupingAction
{
    /// <summary>Make the room play along with another room's group.</summary>
    Join,

    /// <summary>Take the room out of its group so it plays on its own.</summary>
    Leave,
}

/// <param name="Room">The room the action is sent to.</param>
/// <param name="CoordinatorUuid">For <see cref="SonosGroupingAction.Join"/>, the room to follow.</param>
public sealed record SonosGroupingStep(SonosGroupingAction Action, SonosRoom Room, string? CoordinatorUuid);

/// <summary>
/// Works out which joins and leaves turn the household's current grouping into "these rooms
/// play together". Pure, so the choices are unit tested rather than discovered on a speaker.
/// </summary>
public static class SonosGroupingPolicy
{
    /// <summary>
    /// Plans the steps that put <paramref name="selectedUuids"/> in one group and take anything
    /// else out of that group. Selecting a single room therefore dissolves its group.
    /// </summary>
    /// <param name="household">Every group in the household, as reported by a topology query.</param>
    /// <param name="selectedUuids">The rooms that should end up together.</param>
    /// <param name="castCoordinatorUuid">
    /// The group coordinator playback is currently being cast to, if any. It is kept as the
    /// coordinator whenever it is selected, so regrouping never pulls the rug out from under the
    /// player's own connection to it.
    /// </param>
    public static IReadOnlyList<SonosGroupingStep> Plan(
        IReadOnlyList<SonosZoneGroup> household,
        IReadOnlyCollection<string> selectedUuids,
        string? castCoordinatorUuid)
    {
        List<(SonosRoom Room, SonosZoneGroup Group)> rooms = [];
        foreach (SonosZoneGroup group in household)
        {
            foreach (SonosRoom room in group.Members)
            {
                if (!string.IsNullOrWhiteSpace(room.Uuid))
                    rooms.Add((room, group));
            }
        }

        bool IsSelected(string uuid) => selectedUuids.Contains(uuid, StringComparer.OrdinalIgnoreCase);
        bool IsCoordinator((SonosRoom Room, SonosZoneGroup Group) entry) =>
            string.Equals(entry.Room.Uuid, entry.Group.CoordinatorUuid, StringComparison.OrdinalIgnoreCase);

        List<(SonosRoom Room, SonosZoneGroup Group)> selected = rooms.FindAll(entry => IsSelected(entry.Room.Uuid));
        if (selected.Count == 0)
            return [];

        // The coordinator: the one being cast to, else the selected coordinator already leading
        // the most selected rooms (least to move), else just the first selected room.
        (SonosRoom Room, SonosZoneGroup Group) chosen = selected[0];
        int cast = selected.FindIndex(entry =>
            string.Equals(entry.Room.Uuid, castCoordinatorUuid, StringComparison.OrdinalIgnoreCase));
        if (cast >= 0)
        {
            chosen = selected[cast];
        }
        else
        {
            int bestShare = -1;
            foreach ((SonosRoom Room, SonosZoneGroup Group) entry in selected)
            {
                if (!IsCoordinator(entry))
                    continue;

                int share = entry.Group.Members.Count(member => IsSelected(member.Uuid));
                if (share > bestShare)
                {
                    bestShare = share;
                    chosen = entry;
                }
            }
        }

        List<SonosGroupingStep> steps = [];
        List<string> inChosenGroup;
        if (IsCoordinator(chosen))
        {
            inChosenGroup = [.. chosen.Group.Members.Select(member => member.Uuid)];
        }
        else
        {
            // A room that is only following someone has to leave before others can follow it.
            steps.Add(new SonosGroupingStep(SonosGroupingAction.Leave, chosen.Room, null));
            inChosenGroup = [chosen.Room.Uuid];
        }

        if (IsCoordinator(chosen))
        {
            foreach (SonosRoom member in chosen.Group.Members)
            {
                if (!IsSelected(member.Uuid) &&
                    !string.Equals(member.Uuid, chosen.Room.Uuid, StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new SonosGroupingStep(SonosGroupingAction.Leave, member, null));
                }
            }
        }

        foreach ((SonosRoom room, _) in selected)
        {
            if (string.Equals(room.Uuid, chosen.Room.Uuid, StringComparison.OrdinalIgnoreCase) ||
                inChosenGroup.Contains(room.Uuid, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            steps.Add(new SonosGroupingStep(SonosGroupingAction.Join, room, chosen.Room.Uuid));
        }

        return steps;
    }
}
