using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using Trdo.Models;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// Covers the station list's arrangement: turning the saved files into a tree, projecting that
/// tree into the rows on screen, and putting it back together after a drag.
/// <para>
/// The cases that matter most are the ones where the rows on screen are not the whole truth. A
/// collapsed folder's contents are not drawn at all, so anything that rebuilds the arrangement
/// from what is visible has to be handed the previous arrangement as well or it silently
/// deletes them.
/// </para>
/// </summary>
[TestClass]
public sealed class StationLayoutPolicyTests
{
    private static RadioStation Station(string name) =>
        new() { Id = name, Name = name, StreamUrl = $"http://example.com/{name}" };

    private static StationGroup Group(string name, bool expanded, params object[] children)
    {
        StationGroup group = new() { Id = name, Name = name, IsExpanded = expanded };
        foreach (object child in children)
        {
            group.Children.Add(child);

            // Mirrors what a real Flatten pass already did before any drag: GroupId is view
            // state stamped on a node while it is drawn, and ApplyReorder reads it back to
            // tell an established member of this group from one merely passing through.
            switch (child)
            {
                case RadioStation station:
                    station.GroupId = name;
                    break;
                case StationDivider divider:
                    divider.GroupId = name;
                    break;
            }
        }
        return group;
    }

    private static StationDivider Divider(string id, string? label = null) =>
        new() { Id = id, Label = label };

    // ---------------------------------------------------------------- Flatten

    [TestMethod]
    public void Flatten_WithNoFoldersOrDividers_IsTheStationsUnchanged()
    {
        // The default experience: whatever else this feature adds, a user who never makes a
        // folder must get exactly the list they had before.
        RadioStation a = Station("A"), b = Station("B"), c = Station("C");

        List<object> rows = StationLayoutPolicy.Flatten([a, b, c], StationSortMode.Manual);

        Assert.AreSequenceEqual([a, b, c], rows);
    }

    [TestMethod]
    public void Flatten_ExpandedGroup_EmitsHeaderThenChildrenTaggedWithTheGroup()
    {
        RadioStation inside = Station("Inside");
        RadioStation outside = Station("Outside");
        StationGroup group = Group("Jazz", expanded: true, inside);

        List<object> rows = StationLayoutPolicy.Flatten([group, outside], StationSortMode.Manual);

        Assert.AreSequenceEqual([group, inside, outside], rows);
        Assert.AreEqual("Jazz", inside.GroupId, "GroupId drives the indentation rail.");
        Assert.IsNull(outside.GroupId);
    }

    [TestMethod]
    public void Flatten_CollapsedGroup_EmitsOnlyTheHeaderButKeepsItsContents()
    {
        RadioStation hidden = Station("Hidden");
        StationGroup group = Group("News", expanded: false, hidden);

        List<object> rows = StationLayoutPolicy.Flatten([group], StationSortMode.Manual);

        Assert.AreSequenceEqual([group], rows);
        Assert.HasCount(1, group.Children, "Collapsing hides contents; it must not discard them.");
    }

    [TestMethod]
    public void Flatten_DoesNotMutateTheTree()
    {
        // The whole non-destructive guarantee rests on this: sorting changes what is drawn and
        // nothing else.
        RadioStation inside = Station("Inside");
        StationGroup group = Group("Jazz", expanded: true, inside);
        List<object> tree = [group, Station("Loose")];

        StationLayoutPolicy.Flatten(tree, StationSortMode.Name);
        StationLayoutPolicy.Flatten(tree, StationSortMode.Manual);

        Assert.HasCount(2, tree);
        Assert.AreSame(group, tree[0]);
        Assert.HasCount(1, group.Children);
        Assert.AreSame(inside, group.Children[0]);
    }

    [TestMethod]
    public void Flatten_UnderAViewSort_DropsFoldersAndDividersAndShowsEveryStationOnce()
    {
        RadioStation zulu = Station("Zulu");
        RadioStation alpha = Station("Alpha");
        RadioStation mike = Station("Mike");
        StationGroup group = Group("Folder", expanded: false, mike);

        List<object> rows = StationLayoutPolicy.Flatten(
            [zulu, group, Divider("d1"), alpha],
            StationSortMode.Name);

        // Including a station hidden inside a collapsed folder: a sorted list that silently
        // omitted stations would be worse than useless for finding one.
        Assert.AreSequenceEqual([alpha, mike, zulu], rows);
        Assert.IsNull(mike.GroupId, "Nothing is indented when there are no folders on screen.");
    }

    // ---------------------------------------------------------------- Reconcile

    [TestMethod]
    public void Reconcile_WithNoLayoutFile_IsTheFlatListInFileOrder()
    {
        // The upgrade path: every existing user has no layout file.
        RadioStation a = Station("A"), b = Station("B");

        List<object> nodes = StationLayoutPolicy.Reconcile([a, b], null);

        Assert.AreSequenceEqual([a, b], nodes);
    }

    [TestMethod]
    public void Reconcile_AppendsStationsTheLayoutDoesNotMention()
    {
        // An older build appends new stations to the end of stations.json and knows nothing
        // about the layout, so this is what its additions look like on the way back.
        RadioStation known = Station("Known");
        RadioStation added = Station("AddedElsewhere");
        StationLayoutDocument document = new()
        {
            Rows = [new StationLayoutRow { Kind = StationLayoutKinds.Station, StationId = "Known" }]
        };

        List<object> nodes = StationLayoutPolicy.Reconcile([known, added], document);

        Assert.AreSequenceEqual([known, added], nodes);
    }

    [TestMethod]
    public void Reconcile_DropsRowsForStationsThatNoLongerExist()
    {
        RadioStation surviving = Station("Surviving");
        StationLayoutDocument document = new()
        {
            Rows =
            [
                new StationLayoutRow { Kind = StationLayoutKinds.Station, StationId = "Deleted" },
                new StationLayoutRow { Kind = StationLayoutKinds.Station, StationId = "Surviving" }
            ]
        };

        List<object> nodes = StationLayoutPolicy.Reconcile([surviving], document);

        Assert.AreSequenceEqual([surviving], nodes);
    }

    [TestMethod]
    public void Reconcile_RecoversByStreamUrl_WhenIdsWereStrippedByAnOlderBuild()
    {
        // An older build rewrites stations.json without the id field; the stations come back
        // with freshly stamped ids that match nothing in the layout. Without the URL fallback
        // the user's folders would silently collapse into a flat list.
        RadioStation station = new()
        {
            Id = "freshly-stamped",
            Name = "Jazz FM",
            StreamUrl = "http://example.com/jazz"
        };
        StationLayoutDocument document = new()
        {
            Rows =
            [
                new StationLayoutRow
                {
                    Kind = StationLayoutKinds.Group,
                    Id = "g1",
                    Name = "Jazz",
                    Children =
                    [
                        new StationLayoutRow
                        {
                            Kind = StationLayoutKinds.Station,
                            StationId = "the-old-id",
                            StationUrl = "http://example.com/jazz/"
                        }
                    ]
                }
            ]
        };

        List<object> nodes = StationLayoutPolicy.Reconcile([station], document);

        Assert.HasCount(1, nodes);
        StationGroup group = (StationGroup)nodes[0];
        Assert.AreSequenceEqual([station], group.Children);
    }

    [TestMethod]
    public void Reconcile_PromotesNestedGroups_SoFoldersNeverNest()
    {
        RadioStation inner = Station("Inner");
        StationLayoutDocument document = new()
        {
            Rows =
            [
                new StationLayoutRow
                {
                    Kind = StationLayoutKinds.Group,
                    Id = "outer",
                    Name = "Outer",
                    Children =
                    [
                        new StationLayoutRow
                        {
                            Kind = StationLayoutKinds.Group,
                            Id = "inner-group",
                            Name = "Inner group",
                            Children = [new StationLayoutRow { Kind = StationLayoutKinds.Station, StationId = "Inner" }]
                        }
                    ]
                }
            ]
        };

        List<object> nodes = StationLayoutPolicy.Reconcile([inner], document);

        StationGroup outer = (StationGroup)nodes[0];
        Assert.AreSequenceEqual([inner], outer.Children);
    }

    [TestMethod]
    public void Reconcile_GivesADuplicatedStationToTheFirstRowThatClaimsIt()
    {
        RadioStation station = Station("Only");
        StationLayoutDocument document = new()
        {
            Rows =
            [
                new StationLayoutRow { Kind = StationLayoutKinds.Station, StationId = "Only" },
                new StationLayoutRow { Kind = StationLayoutKinds.Station, StationId = "Only" }
            ]
        };

        List<object> nodes = StationLayoutPolicy.Reconcile([station], document);

        Assert.HasCount(1, nodes, "One station, one row - never two rows sharing an object.");
    }

    [TestMethod]
    public void Reconcile_IgnoresRowKindsItDoesNotRecognise()
    {
        // Forward compatibility: a newer build may add a row kind, and downgrading must not
        // throw the rest of the layout away.
        RadioStation station = Station("A");
        StationLayoutDocument document = new()
        {
            Rows =
            [
                new StationLayoutRow { Kind = "something-new" },
                new StationLayoutRow { Kind = StationLayoutKinds.Station, StationId = "A" }
            ]
        };

        List<object> nodes = StationLayoutPolicy.Reconcile([station], document);

        Assert.AreSequenceEqual([station], nodes);
    }

    [TestMethod]
    public void ToDocument_RoundTripsThroughReconcile()
    {
        RadioStation loose = Station("Loose");
        RadioStation inside = Station("Inside");
        StationGroup group = Group("Jazz", expanded: false, inside, Divider("d1", "Late night"));
        List<object> original = [loose, Divider("d2"), group];

        List<object> restored = StationLayoutPolicy.Reconcile(
            [loose, inside],
            StationLayoutPolicy.ToDocument(original));

        Assert.HasCount(3, restored);
        Assert.AreSame(loose, restored[0]);
        Assert.AreEqual("d2", ((StationDivider)restored[1]).Id);

        StationGroup restoredGroup = (StationGroup)restored[2];
        Assert.AreEqual("Jazz", restoredGroup.Name);
        Assert.IsFalse(restoredGroup.IsExpanded, "A collapsed folder stays collapsed across a restart.");
        Assert.AreSame(inside, restoredGroup.Children[0]);
        Assert.AreEqual("Late night", ((StationDivider)restoredGroup.Children[1]).Label);
    }

    [TestMethod]
    public void CollectStations_IsDepthFirst_SoTheSavedFileMatchesWhatIsOnScreen()
    {
        // stations.json order is what an older build shows. Depth-first means it still shows
        // the user's folders' contents grouped together, just without the headers.
        RadioStation first = Station("First");
        RadioStation grouped = Station("Grouped");
        RadioStation last = Station("Last");

        List<RadioStation> collected = StationLayoutPolicy.CollectStations(
            [first, Group("G", expanded: false, grouped), last]);

        Assert.AreSequenceEqual([first, grouped, last], collected);
    }

    // ---------------------------------------------------------------- ApplyReorder

    [TestMethod]
    public void ApplyReorder_StationDroppedBetweenTwoChildren_JoinsTheGroup()
    {
        RadioStation one = Station("One"), two = Station("Two"), loose = Station("Loose");
        StationGroup group = Group("Jazz", expanded: true, one, two);
        List<object> previous = [group, loose];

        // The user dragged "Loose" between "One" and "Two".
        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [group, one, loose, two], [loose]);

        Assert.HasCount(1, nodes);
        Assert.AreSequenceEqual([one, loose, two], ((StationGroup)nodes[0]).Children);
        Assert.AreEqual("Jazz", loose.GroupId);
    }

    [TestMethod]
    public void ApplyReorder_StationDroppedAfterTheLastChild_JoinsTheGroup()
    {
        // The one genuinely ambiguous drop: that slot is both "last inside the folder" and
        // "first after it". It resolves as inside; "Move to group ▸ (None)" is the way out.
        RadioStation one = Station("One"), loose = Station("Loose");
        StationGroup group = Group("Jazz", expanded: true, one);
        List<object> previous = [group, loose];

        // The user dragged "Loose" to the bottom of the folder.
        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [group, one, loose], [loose]);

        Assert.HasCount(1, nodes);
        Assert.AreSequenceEqual([one, loose], ((StationGroup)nodes[0]).Children);
    }

    [TestMethod]
    public void ApplyReorder_StationDroppedUnderACollapsedHeader_GoesInFirstAndKeepsTheHiddenContents()
    {
        // The case a rebuild driven only by the visible rows gets catastrophically wrong: the
        // folder's existing contents are not on screen and would simply disappear.
        RadioStation hiddenA = Station("HiddenA"), hiddenB = Station("HiddenB");
        RadioStation dropped = Station("Dropped");
        StationGroup group = Group("News", expanded: false, hiddenA, hiddenB);
        List<object> previous = [group, dropped];

        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [group, dropped], [dropped]);

        Assert.HasCount(1, nodes);
        Assert.AreSequenceEqual(
            [dropped, hiddenA, hiddenB], ((StationGroup)nodes[0]).Children, "The dropped station goes in at the top, and nothing already inside is lost.");
    }

    [TestMethod]
    public void ApplyReorder_CollapsedGroupMoved_TakesItsContentsWithIt()
    {
        RadioStation hidden = Station("Hidden");
        RadioStation loose = Station("Loose");
        StationGroup group = Group("News", expanded: false, hidden);
        List<object> previous = [group, loose];

        // The group was dragged below the loose station.
        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [loose, group], [group]);

        Assert.AreSequenceEqual([loose, group], nodes);
        Assert.AreSequenceEqual([hidden], group.Children);
    }

    [TestMethod]
    public void ApplyReorder_GroupDraggedIntoAnotherGroupsRun_LandsAtTopLevel()
    {
        RadioStation child = Station("Child");
        StationGroup first = Group("First", expanded: true, child);
        StationGroup second = Group("Second", expanded: true);
        List<object> previous = [first, second];

        // "Second" was dropped between "First" and its child.
        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [first, second, child], [second]);

        Assert.AreSequenceEqual([first, second], nodes);
        Assert.IsEmpty(first.Children);
        Assert.AreSequenceEqual([child], second.Children, "Folders never nest, so the run simply passes to the folder that opened last.");
    }

    [TestMethod]
    public void ApplyReorder_DividerDraggedToTheTop_BecomesTheFirstTopLevelRow()
    {
        RadioStation a = Station("A"), b = Station("B");
        StationDivider divider = Divider("d1");
        List<object> previous = [a, b, divider];

        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [divider, a, b], [divider]);

        Assert.AreSequenceEqual([divider, a, b], nodes);
        Assert.IsNull(divider.GroupId);
    }

    [TestMethod]
    public void ApplyReorder_StationDraggedOutOfAGroupAboveIt_LandsAtTopLevel()
    {
        RadioStation escaping = Station("Escaping"), staying = Station("Staying");
        StationGroup group = Group("Jazz", expanded: true, escaping, staying);
        List<object> previous = [group];

        // Dragged above the folder header.
        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [escaping, group, staying], [escaping]);

        Assert.AreSequenceEqual([escaping, group], nodes);
        Assert.AreSequenceEqual([staying], group.Children);
        Assert.IsNull(escaping.GroupId);
    }

    [TestMethod]
    public void ApplyReorder_LeavesUntouchedRowsInTheirRelativeOrder()
    {
        RadioStation a = Station("A"), b = Station("B"), c = Station("C"), d = Station("D");
        List<object> previous = [a, b, c, d];

        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [d, a, b, c], [d]);

        Assert.AreSequenceEqual([d, a, b, c], nodes);
    }

    [TestMethod]
    public void ApplyReorder_UnrelatedDragElsewhere_LeavesTrailingTopLevelStationsAlone()
    {
        // Regression test for the bug where completing any drag - even one nowhere near a
        // folder - re-derived the whole arrangement from the display rows and, since nothing
        // marked where a folder's run of rows ended, swallowed every ordinary station sitting
        // below the bottom-most folder into it.
        RadioStation inside = Station("Inside");
        StationGroup group = Group("Jazz", expanded: true, inside);
        RadioStation trailingA = Station("TrailingA"), trailingB = Station("TrailingB");
        List<object> previous = [group, trailingA, trailingB];

        // Nothing here moved; the display rows come back exactly as they went in, the way
        // they would for a drag that only reordered rows elsewhere in a longer list.
        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [group, inside, trailingA, trailingB], []);

        Assert.AreSequenceEqual([group, trailingA, trailingB], nodes);
        Assert.AreSequenceEqual([inside], group.Children);
        Assert.IsNull(trailingA.GroupId);
        Assert.IsNull(trailingB.GroupId);
    }

    [TestMethod]
    public void ApplyReorder_StationDraggedPastATrailingTopLevelStation_EscapesTheFolder()
    {
        // There was previously no way to drag a station out of a folder at all - only
        // "Move to group ▸ (None)" could do it. Dropping it past a station that is already
        // unambiguously top-level is now the drag equivalent.
        RadioStation escaping = Station("Escaping"), stays = Station("Stays");
        RadioStation anchor = Station("Anchor");
        StationGroup group = Group("Jazz", expanded: true, escaping, stays);
        List<object> previous = [group, anchor];

        List<object> nodes = StationLayoutPolicy.ApplyReorder(previous, [group, stays, anchor, escaping], [escaping]);

        Assert.AreSequenceEqual([group, anchor, escaping], nodes);
        Assert.AreSequenceEqual([stays], group.Children);
        Assert.IsNull(escaping.GroupId);
    }
}
