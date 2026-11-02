using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using Trdo.Services.Playback;

namespace Trdo.Tests;

[TestClass]
public sealed class SonosGroupingPolicyTests
{
    private static SonosRoom Room(string uuid, string name = "") =>
        new(uuid, string.IsNullOrEmpty(name) ? uuid : name, $"http://192.168.1.10:1400/{uuid}.xml");

    private static SonosZoneGroup Group(string coordinator, params string[] members) =>
        new(coordinator, $"http://192.168.1.10:1400/{coordinator}.xml", members.Select(uuid => Room(uuid)).ToArray());

    private static string Describe(SonosGroupingStep step) =>
        step.Action == SonosGroupingAction.Join
            ? $"join {step.Room.Uuid}->{step.CoordinatorUuid}"
            : $"leave {step.Room.Uuid}";

    private static string[] Plan(IReadOnlyList<SonosZoneGroup> household, string[] selected, string? cast = null) =>
        SonosGroupingPolicy.Plan(household, selected, cast).Select(Describe).ToArray();

    [TestMethod]
    public void Plan_JoinsSeparateRoomsToTheFirstSelected()
    {
        CollectionAssert.AreEqual(new[] { "join B->A" },
            Plan([Group("A", "A"), Group("B", "B"), Group("C", "C")], ["A", "B"]));
    }

    [TestMethod]
    public void Plan_KeepsTheRoomBeingCastToAsCoordinator()
    {
        CollectionAssert.AreEqual(new[] { "join A->B" },
            Plan([Group("A", "A"), Group("B", "B")], ["A", "B"], cast: "B"));
    }

    [TestMethod]
    public void Plan_AddsToAnExistingGroupInsteadOfStartingANewOne()
    {
        CollectionAssert.AreEqual(new[] { "join C->A" },
            Plan([Group("A", "A", "B"), Group("C", "C")], ["A", "B", "C"]));
    }

    [TestMethod]
    public void Plan_LeavesRoomsThatWereUntickedAndIsEmptyWhenNothingChanges()
    {
        IReadOnlyList<SonosZoneGroup> household = [Group("A", "A", "B", "C")];
        CollectionAssert.AreEqual(new[] { "leave C" }, Plan(household, ["A", "B"]));
        Assert.IsEmpty(Plan(household, ["A", "B", "C"], cast: "A"));
    }

    [TestMethod]
    public void Plan_SelectingOneRoomDissolvesItsGroup()
    {
        CollectionAssert.AreEqual(new[] { "leave B" }, Plan([Group("A", "A", "B")], ["A"]));
    }

    [TestMethod]
    public void Plan_PrefersASelectedRoomThatAlreadyLeadsItself()
    {
        // B follows A; the user wants B and C together. C leads itself, so B just moves over.
        CollectionAssert.AreEqual(new[] { "join B->C" },
            Plan([Group("A", "A", "B"), Group("C", "C")], ["B", "C"]));
    }

    [TestMethod]
    public void Plan_MakesAFollowerLeaveBeforeOthersCanFollowIt()
    {
        // B and C both follow A, and the user wants them together without A.
        CollectionAssert.AreEqual(new[] { "leave B", "join C->B" },
            Plan([Group("A", "A", "B", "C")], ["B", "C"]));
    }

    [TestMethod]
    public void GroupNameKey_DependsOnTheSetOfRoomsNotTheirOrderOrCase()
    {
        Assert.AreEqual(
            SonosGroupNames.KeyFor(["RINCON_B", "RINCON_A"]),
            SonosGroupNames.KeyFor(["rincon_a", " RINCON_B ", "RINCON_A"]));
        Assert.AreNotEqual(
            SonosGroupNames.KeyFor(["RINCON_A", "RINCON_B"]),
            SonosGroupNames.KeyFor(["RINCON_A", "RINCON_B", "RINCON_C"]));
        Assert.AreEqual(string.Empty, SonosGroupNames.KeyFor(["", " "]));
    }

    [TestMethod]
    public void Plan_IgnoresUnknownRoomsAndEmptySelections()
    {
        Assert.IsEmpty(Plan([Group("A", "A")], []));
        Assert.IsEmpty(Plan([Group("A", "A")], ["ZZZ"]));
    }
}
