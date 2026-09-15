using System;
using System.Collections.Generic;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// Turns the saved station list and layout into the tree the app works with, and projects that
/// tree into the flat sequence of rows the list control draws.
/// <para>
/// All of it is pure: nothing here reads or writes files, and nothing mutates the tree it is
/// given. That is what makes a view sort non-destructive - sorting changes only what
/// <see cref="Flatten"/> returns, never what gets saved.
/// </para>
/// </summary>
public static class StationLayoutPolicy
{
    /// <summary>
    /// Rebuilds the node tree from the saved stations and layout.
    /// <para>
    /// Tolerant by design. The two files are written separately and can disagree - a station
    /// added by an older build will not appear in the layout, a station removed on another run
    /// will still be referenced by it - so every mismatch has a defined resolution rather than
    /// an error. Stations are never lost: anything the layout does not account for is appended
    /// at the top level.
    /// </para>
    /// </summary>
    /// <param name="stations">The stations as loaded, in file order.</param>
    /// <param name="document">The saved layout, or null when the user has never made a folder.</param>
    /// <returns>The top-level nodes: <see cref="RadioStation"/>, <see cref="StationGroup"/> and <see cref="StationDivider"/>.</returns>
    public static List<object> Reconcile(IReadOnlyList<RadioStation>? stations, StationLayoutDocument? document)
    {
        if (stations is null || stations.Count == 0)
            return [];

        // A corrupt layout file must never stop the app from starting: the view model that
        // calls this is built lazily on first use, so an exception escaping here would take
        // the whole app down. Falling back to the flat list is always safe.
        try
        {
            if (document?.Rows is null || document.Rows.Count == 0)
                return [.. stations];

            Dictionary<string, RadioStation> byId = new(StringComparer.Ordinal);
            Dictionary<string, RadioStation> byUrl = new(StringComparer.OrdinalIgnoreCase);
            foreach (RadioStation station in stations)
            {
                if (!string.IsNullOrWhiteSpace(station.Id))
                    byId.TryAdd(station.Id, station);

                // Secondary index for recovery: a build that does not know about ids strips
                // them on save, which would otherwise orphan every station in the layout.
                string url = NormalizeUrl(station.StreamUrl);
                if (url.Length > 0)
                    byUrl.TryAdd(url, station);
            }

            HashSet<RadioStation> consumed = [];
            List<object> topLevel = [];

            foreach (StationLayoutRow row in document.Rows)
            {
                switch (row?.Kind)
                {
                    case StationLayoutKinds.Station:
                        if (ResolveStation(row, byId, byUrl, consumed) is RadioStation resolved)
                        {
                            resolved.GroupId = null;
                            topLevel.Add(resolved);
                        }
                        break;

                    case StationLayoutKinds.Divider:
                        topLevel.Add(ToDivider(row, groupId: null));
                        break;

                    case StationLayoutKinds.Group:
                        topLevel.Add(ToGroup(row, byId, byUrl, consumed));
                        break;

                        // Anything else came from a newer build. Dropping it is the whole reason
                        // the row type carries a kind string instead of a type discriminator.
                }
            }

            // Stations the layout knows nothing about - added by an older build, or by a
            // crash between the two writes. Older builds append to the end of stations.json,
            // so appending here puts them back where the user last saw them.
            foreach (RadioStation station in stations)
            {
                if (consumed.Add(station))
                {
                    station.GroupId = null;
                    topLevel.Add(station);
                }
            }

            return topLevel;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StationLayoutPolicy] Reconcile failed, falling back to a flat list: {ex.Message}");
            return [.. stations];
        }
    }

    /// <summary>
    /// Projects the node tree into the flat sequence of rows to draw.
    /// <para>
    /// Under <see cref="StationSortMode.Manual"/> this is a depth-first walk: each node in
    /// order, and an expanded folder's contents immediately after its header. A collapsed
    /// folder contributes only its header - its contents stay in the tree but are not drawn,
    /// which is why anything that rebuilds the tree from these rows needs the previous tree
    /// as well.
    /// </para>
    /// <para>
    /// Under any other mode only stations are emitted, sorted and flat. Folders and dividers
    /// are positional, and once the user is not the one choosing positions they have nothing
    /// left to say; keeping them would also split the list into separately sorted islands and
    /// defeat the point of sorting it.
    /// </para>
    /// <para>
    /// Never mutates the tree. The only thing it writes is each item's <c>GroupId</c>, which
    /// is view state describing the row being drawn.
    /// </para>
    /// </summary>
    public static List<object> Flatten(IReadOnlyList<object>? topLevelNodes, StationSortMode sortMode)
    {
        if (topLevelNodes is null || topLevelNodes.Count == 0)
            return [];

        if (sortMode != StationSortMode.Manual)
        {
            List<object> sorted = [];
            foreach (RadioStation station in StationSortPolicy.Sort(CollectStations(topLevelNodes), sortMode))
            {
                station.GroupId = null;
                sorted.Add(station);
            }
            return sorted;
        }

        List<object> rows = [];
        foreach (object node in topLevelNodes)
        {
            switch (node)
            {
                case StationGroup group:
                    rows.Add(group);
                    if (!group.IsExpanded)
                        break;

                    foreach (object child in group.Children)
                    {
                        SetGroupId(child, group.Id);
                        rows.Add(child);
                    }
                    break;

                case RadioStation or StationDivider:
                    SetGroupId(node, null);
                    rows.Add(node);
                    break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Rebuilds the arrangement after the user has dragged a row to a new position.
    /// <para>
    /// The previous tree is a required input, not a convenience. A collapsed folder's contents
    /// never appear in the display list at all, so a rebuild that read only the visible rows
    /// would quietly delete everything inside every collapsed folder.
    /// </para>
    /// <para>
    /// Containment is read off the rows the same way the user reads it off the screen: a folder
    /// header opens a run, and everything below it belongs to that folder until something
    /// signals the run has ended. A row that was not part of this drag (it is not in
    /// <paramref name="movedRows"/>) and was top-level before the drag is exactly that signal -
    /// it closes the folder's run and goes back to being top-level itself, which is what stops
    /// ordinary stations sitting after a folder from being swallowed by it every time some
    /// unrelated row is dragged. A row that <em>was</em> dragged has no such anchor to check
    /// against, so it simply joins whatever run it was dropped into; dropped right after a
    /// folder's last child - the one genuinely ambiguous position, both "last inside" and
    /// "first after" - that resolves as <em>inside</em>, and dropping it past the next
    /// unmoved top-level row is the unambiguous way to carry it back out.
    /// </para>
    /// </summary>
    /// <param name="previousNodes">The arrangement before the drag.</param>
    /// <param name="newDisplayRows">The visible rows after the drop, in their new order.</param>
    /// <param name="movedRows">
    /// The row(s) the user actually dragged, e.g. from <c>DragItemsCompletedEventArgs.Items</c>.
    /// Everything else is treated as having kept its old membership (read off its own
    /// <c>GroupId</c>) unless a group header says otherwise.
    /// </param>
    public static List<object> ApplyReorder(
        IReadOnlyList<object>? previousNodes,
        IReadOnlyList<object>? newDisplayRows,
        IReadOnlyCollection<object>? movedRows = null)
    {
        if (newDisplayRows is null || newDisplayRows.Count == 0)
            return previousNodes is null ? [] : [.. previousNodes];

        HashSet<object> moved = movedRows is null
            ? new HashSet<object>(ReferenceEqualityComparer.Instance)
            : new HashSet<object>(movedRows, ReferenceEqualityComparer.Instance);

        // Contents of folders that were collapsed during the drag, so they can be carried
        // across rather than lost.
        Dictionary<StationGroup, List<object>> hiddenChildren = [];
        if (previousNodes is not null)
        {
            foreach (object node in previousNodes)
            {
                if (node is StationGroup { IsExpanded: false } collapsed)
                    hiddenChildren[collapsed] = [.. collapsed.Children];
            }
        }

        List<object> topLevel = [];
        StationGroup? currentGroup = null;
        // Rows dropped directly beneath a collapsed folder's header go in at the top, which is
        // where the insertion line appeared to point.
        int insertAt = 0;

        foreach (object row in newDisplayRows)
        {
            switch (row)
            {
                case StationGroup group:
                    currentGroup = group;
                    group.Children.Clear();
                    if (hiddenChildren.TryGetValue(group, out List<object>? carried))
                    {
                        foreach (object child in carried)
                            group.Children.Add(child);
                    }
                    insertAt = 0;
                    topLevel.Add(group);
                    break;

                case RadioStation or StationDivider:
                    // An untouched row that used to be top-level closes the run: it was never
                    // part of this folder and dragging something else around it must not pull
                    // it in. A row that was in some *other* folder is left alone here - folders
                    // never nest, so a run it now sits in simply takes it over, same as before.
                    if (currentGroup is not null && !moved.Contains(row) && GetGroupId(row) is null)
                        currentGroup = null;

                    if (currentGroup is not null)
                    {
                        currentGroup.Children.Insert(Math.Min(insertAt, currentGroup.Children.Count), row);
                        insertAt++;
                    }
                    else
                    {
                        topLevel.Add(row);
                    }
                    break;
            }
        }

        foreach (object node in topLevel)
        {
            if (node is StationGroup group)
            {
                foreach (object child in group.Children)
                    SetGroupId(child, group.Id);
                group.NotifyChildrenChanged();
            }
            else
            {
                SetGroupId(node, null);
            }
        }

        return topLevel;
    }

    /// <summary>
    /// Converts the node tree back into its persisted form.
    /// </summary>
    public static StationLayoutDocument ToDocument(IReadOnlyList<object>? topLevelNodes)
    {
        StationLayoutDocument document = new();
        if (topLevelNodes is null)
            return document;

        foreach (object node in topLevelNodes)
        {
            switch (node)
            {
                case RadioStation station:
                    document.Rows.Add(new StationLayoutRow
                    {
                        Kind = StationLayoutKinds.Station,
                        StationId = station.Id,
                        StationUrl = station.StreamUrl
                    });
                    break;

                case StationDivider divider:
                    document.Rows.Add(new StationLayoutRow
                    {
                        Kind = StationLayoutKinds.Divider,
                        Id = divider.Id,
                        Label = divider.Label
                    });
                    break;

                case StationGroup group:
                    StationLayoutRow groupRow = new()
                    {
                        Kind = StationLayoutKinds.Group,
                        Id = group.Id,
                        Name = group.Name,
                        IsExpanded = group.IsExpanded,
                        Children = []
                    };

                    foreach (object child in group.Children)
                    {
                        switch (child)
                        {
                            case RadioStation childStation:
                                groupRow.Children.Add(new StationLayoutRow
                                {
                                    Kind = StationLayoutKinds.Station,
                                    StationId = childStation.Id,
                                    StationUrl = childStation.StreamUrl
                                });
                                break;

                            case StationDivider childDivider:
                                groupRow.Children.Add(new StationLayoutRow
                                {
                                    Kind = StationLayoutKinds.Divider,
                                    Id = childDivider.Id,
                                    Label = childDivider.Label
                                });
                                break;
                        }
                    }

                    document.Rows.Add(groupRow);
                    break;
            }
        }

        return document;
    }

    /// <summary>
    /// The stations in the tree, in depth-first display order. This is the order written to
    /// <c>stations.json</c>, so an older build that ignores the layout still shows the user's
    /// stations grouped together in the arrangement they built.
    /// </summary>
    public static List<RadioStation> CollectStations(IReadOnlyList<object>? topLevelNodes)
    {
        List<RadioStation> stations = [];
        if (topLevelNodes is null)
            return stations;

        foreach (object node in topLevelNodes)
        {
            switch (node)
            {
                case RadioStation station:
                    stations.Add(station);
                    break;

                case StationGroup group:
                    foreach (object child in group.Children)
                    {
                        if (child is RadioStation childStation)
                            stations.Add(childStation);
                    }
                    break;
            }
        }

        return stations;
    }

    /// <summary>
    /// Normalises a stream URL for comparison: trimmed, lower-cased, without a trailing slash.
    /// Shared with metadata matching so both agree on when two URLs are the same stream.
    /// </summary>
    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        string trimmed = url.Trim().TrimEnd('/');
        return trimmed.ToLowerInvariant();
    }

    private static StationGroup ToGroup(
        StationLayoutRow row,
        Dictionary<string, RadioStation> byId,
        Dictionary<string, RadioStation> byUrl,
        HashSet<RadioStation> consumed)
    {
        StationGroup group = new()
        {
            Id = string.IsNullOrWhiteSpace(row.Id) ? StationIdentityPolicy.NewId() : row.Id,
            Name = row.Name ?? string.Empty,
            IsExpanded = row.IsExpanded
        };

        if (row.Children is not null)
        {
            foreach (StationLayoutRow child in row.Children)
            {
                switch (child?.Kind)
                {
                    case StationLayoutKinds.Station:
                        if (ResolveStation(child, byId, byUrl, consumed) is RadioStation station)
                        {
                            station.GroupId = group.Id;
                            group.Children.Add(station);
                        }
                        break;

                    case StationLayoutKinds.Divider:
                        group.Children.Add(ToDivider(child, group.Id));
                        break;

                    case StationLayoutKinds.Group:
                        // Folders never nest. This is the single place that invariant is
                        // enforced: a nested folder written by anything else is flattened
                        // into its parent rather than rejected, so no station is lost.
                        foreach (object promoted in PromoteNestedGroup(child, byId, byUrl, consumed, group.Id))
                            group.Children.Add(promoted);
                        break;
                }
            }
        }

        return group;
    }

    private static List<object> PromoteNestedGroup(
        StationLayoutRow row,
        Dictionary<string, RadioStation> byId,
        Dictionary<string, RadioStation> byUrl,
        HashSet<RadioStation> consumed,
        string parentGroupId)
    {
        List<object> promoted = [];
        if (row.Children is null)
            return promoted;

        foreach (StationLayoutRow child in row.Children)
        {
            switch (child?.Kind)
            {
                case StationLayoutKinds.Station:
                    if (ResolveStation(child, byId, byUrl, consumed) is RadioStation station)
                    {
                        station.GroupId = parentGroupId;
                        promoted.Add(station);
                    }
                    break;

                case StationLayoutKinds.Divider:
                    promoted.Add(ToDivider(child, parentGroupId));
                    break;

                case StationLayoutKinds.Group:
                    foreach (object deeper in PromoteNestedGroup(child, byId, byUrl, consumed, parentGroupId))
                        promoted.Add(deeper);
                    break;
            }
        }

        return promoted;
    }

    private static StationDivider ToDivider(StationLayoutRow row, string? groupId) => new()
    {
        Id = string.IsNullOrWhiteSpace(row.Id) ? StationIdentityPolicy.NewId() : row.Id,
        Label = row.Label,
        GroupId = groupId
    };

    /// <summary>
    /// Resolves a station row to a station, by id and then by stream URL. Returns null when
    /// the station no longer exists, or when it has already been placed - a layout listing the
    /// same station twice gives it to whichever row came first rather than showing it twice.
    /// </summary>
    private static RadioStation? ResolveStation(
        StationLayoutRow row,
        Dictionary<string, RadioStation> byId,
        Dictionary<string, RadioStation> byUrl,
        HashSet<RadioStation> consumed)
    {
        RadioStation? station = null;

        if (!string.IsNullOrWhiteSpace(row.StationId))
            byId.TryGetValue(row.StationId, out station);

        if (station is null)
        {
            string url = NormalizeUrl(row.StationUrl);
            if (url.Length == 0 || !byUrl.TryGetValue(url, out station))
                return null;
        }

        return consumed.Add(station) ? station : null;
    }

    private static void SetGroupId(object node, string? groupId)
    {
        switch (node)
        {
            case RadioStation station:
                station.GroupId = groupId;
                break;
            case StationDivider divider:
                divider.GroupId = groupId;
                break;
        }
    }

    /// <summary>
    /// Reads a row's <c>GroupId</c> as of the last flatten - its membership before this drag,
    /// used by <see cref="ApplyReorder"/> to tell a row that belongs where it landed from one
    /// that is only passing through.
    /// </summary>
    private static string? GetGroupId(object node) => node switch
    {
        RadioStation station => station.GroupId,
        StationDivider divider => divider.GroupId,
        _ => null
    };
}
