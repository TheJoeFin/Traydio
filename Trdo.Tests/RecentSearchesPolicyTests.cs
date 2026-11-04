using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using Trdo.Services;

namespace Trdo.Tests;

/// <summary>
/// Covers the small "recent searches" list the search page offers as chips - most recent first,
/// de-duplicated, and capped so it can't grow without bound.
/// </summary>
[TestClass]
public sealed class RecentSearchesPolicyTests
{
    [TestMethod]
    public void Add_ToEmptyList_AddsTheTerm()
    {
        IReadOnlyList<string> result = RecentSearchesPolicy.Add([], "jazz");

        CollectionAssert.AreEqual(new[] { "jazz" }, (List<string>)result);
    }

    [TestMethod]
    public void Add_NewTerm_GoesToTheFront()
    {
        IReadOnlyList<string> result = RecentSearchesPolicy.Add(["rock", "jazz"], "blues");

        CollectionAssert.AreEqual(new[] { "blues", "rock", "jazz" }, (List<string>)result);
    }

    [TestMethod]
    public void Add_RepeatedTerm_MovesToFrontInsteadOfDuplicating()
    {
        IReadOnlyList<string> result = RecentSearchesPolicy.Add(["rock", "jazz", "blues"], "jazz");

        CollectionAssert.AreEqual(new[] { "jazz", "rock", "blues" }, (List<string>)result);
    }

    [TestMethod]
    public void Add_RepeatedTerm_IsCaseInsensitive()
    {
        IReadOnlyList<string> result = RecentSearchesPolicy.Add(["Jazz", "rock"], "JAZZ");

        CollectionAssert.AreEqual(new[] { "JAZZ", "rock" }, (List<string>)result);
    }

    [TestMethod]
    public void Add_BlankTerm_LeavesTheListUnchanged()
    {
        IReadOnlyList<string> existing = ["rock", "jazz"];

        IReadOnlyList<string> result = RecentSearchesPolicy.Add(existing, "   ");

        Assert.AreSame(existing, result);
    }

    [TestMethod]
    public void Add_TrimsWhitespaceAroundTheTerm()
    {
        IReadOnlyList<string> result = RecentSearchesPolicy.Add([], "  jazz  ");

        CollectionAssert.AreEqual(new[] { "jazz" }, (List<string>)result);
    }

    [TestMethod]
    public void Add_BeyondTheCap_DropsTheOldestEntry()
    {
        IReadOnlyList<string> result = RecentSearchesPolicy.Add(["a", "b", "c"], "d", maxEntries: 3);

        CollectionAssert.AreEqual(new[] { "d", "a", "b" }, (List<string>)result);
    }
}
