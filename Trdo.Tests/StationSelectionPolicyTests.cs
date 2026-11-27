using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using Trdo.Models;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// Covers restoring the selected station. Selection moved from a bare list index to an id
/// because folders, collapsing and view sorts all move stations around; these tests pin down
/// that the id wins whenever it resolves, that the legacy index still works on the first run
/// after upgrading, and that neither ever leaves the user with nothing selected when there
/// are stations to pick from.
/// </summary>
[TestClass]
public sealed class StationSelectionPolicyTests
{
    private static RadioStation Station(string name, string id) =>
        new() { Name = name, StreamUrl = $"http://example.com/{name}", Id = id };

    private static List<RadioStation> ThreeStations() =>
        [Station("First", "id-1"), Station("Second", "id-2"), Station("Third", "id-3")];

    [TestMethod]
    public void SavedId_WinsOverTheLegacyIndex()
    {
        List<RadioStation> stations = ThreeStations();

        RadioStation? resolved = StationSelectionPolicy.Resolve(stations, "id-3", legacyIndex: 0);

        Assert.AreSame(stations[2], resolved,
            "The index is the fallback; once an id is stored it is the authority.");
    }

    [TestMethod]
    public void NoSavedId_FallsBackToTheLegacyIndex()
    {
        // The first run after upgrading: only the pre-2.0 index exists.
        List<RadioStation> stations = ThreeStations();

        Assert.AreSame(stations[1], StationSelectionPolicy.Resolve(stations, null, legacyIndex: 1));
        Assert.AreSame(stations[1], StationSelectionPolicy.Resolve(stations, "", legacyIndex: 1));
    }

    [TestMethod]
    public void UnknownId_FallsBackToTheLegacyIndex()
    {
        // The saved station was removed on another run.
        List<RadioStation> stations = ThreeStations();

        Assert.AreSame(stations[2], StationSelectionPolicy.Resolve(stations, "gone", legacyIndex: 2));
    }

    [TestMethod]
    public void OutOfRangeIndex_FallsBackToTheFirstStation()
    {
        List<RadioStation> stations = ThreeStations();

        Assert.AreSame(stations[0], StationSelectionPolicy.Resolve(stations, null, legacyIndex: 99));
        Assert.AreSame(stations[0], StationSelectionPolicy.Resolve(stations, null, legacyIndex: -1));
    }

    [TestMethod]
    public void EmptyList_ResolvesToNull()
    {
        Assert.IsNull(StationSelectionPolicy.Resolve([], "id-1", legacyIndex: 0));
        Assert.IsNull(StationSelectionPolicy.Resolve(null, "id-1", legacyIndex: 0));
    }

    [TestMethod]
    public void IdMatching_IsCaseSensitive()
    {
        // Ids are generated hex, never typed. A case-insensitive match would only ever
        // succeed by accident.
        List<RadioStation> stations = ThreeStations();

        Assert.AreSame(stations[0], StationSelectionPolicy.Resolve(stations, "ID-2", legacyIndex: 0));
    }

    [TestMethod]
    [DataRow(0, 1, 5, 1)]
    [DataRow(4, 1, 5, 0)]   // next wraps from the last to the first
    [DataRow(0, -1, 5, 4)]  // previous wraps from the first to the last
    [DataRow(1, 3, 5, 4)]   // several steps land in one go
    [DataRow(3, 4, 5, 2)]   // and wrap past the end
    [DataRow(1, -7, 5, 4)]  // more steps than stations, backwards
    [DataRow(-1, 1, 5, 0)]  // nothing selected: next starts at the first
    [DataRow(-1, 2, 5, 1)]
    [DataRow(-1, -1, 5, 4)] // and previous at the last
    [DataRow(-1, -2, 5, 3)]
    public void CycleIndex_StepsAndWraps(int current, int steps, int count, int expected)
    {
        Assert.AreEqual(expected, StationSelectionPolicy.CycleIndex(current, steps, count));
    }

    [TestMethod]
    public void CycleIndex_EmptyList_IsMinusOne()
    {
        Assert.AreEqual(-1, StationSelectionPolicy.CycleIndex(0, 1, 0));
    }
}
