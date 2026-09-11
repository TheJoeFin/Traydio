using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Models;
using Trdo.Services;

namespace Trdo.Tests;

[TestClass]
public sealed class LocalMusicPlaybackPolicyTests
{
    [TestMethod]
    public void TrackLoop_ReplaysTheFinishedTrack()
    {
        LocalMusicTrackEndAction action = LocalMusicPlaybackPolicy.ResolveTrackEnd(
            currentTrackIndex: 2,
            trackCount: 3,
            LocalMusicLoopMode.Track);

        Assert.AreEqual(2, action.SelectedTrackIndex);
        Assert.IsTrue(action.ShouldPlay);
    }

    [TestMethod]
    public void NonFinalTrack_AdvancesRegardlessOfAlbumLoopMode()
    {
        LocalMusicTrackEndAction action = LocalMusicPlaybackPolicy.ResolveTrackEnd(
            currentTrackIndex: 1,
            trackCount: 3,
            LocalMusicLoopMode.None);

        Assert.AreEqual(2, action.SelectedTrackIndex);
        Assert.IsTrue(action.ShouldPlay);
    }

    [TestMethod]
    public void FinalTrack_WithNoLoop_SelectsFirstTrackAndStops()
    {
        LocalMusicTrackEndAction action = LocalMusicPlaybackPolicy.ResolveTrackEnd(
            currentTrackIndex: 2,
            trackCount: 3,
            LocalMusicLoopMode.None);

        Assert.AreEqual(0, action.SelectedTrackIndex);
        Assert.IsFalse(action.ShouldPlay);
    }

    [TestMethod]
    public void FinalTrack_WithAlbumLoop_SelectsAndPlaysFirstTrack()
    {
        LocalMusicTrackEndAction action = LocalMusicPlaybackPolicy.ResolveTrackEnd(
            currentTrackIndex: 2,
            trackCount: 3,
            LocalMusicLoopMode.Album);

        Assert.AreEqual(0, action.SelectedTrackIndex);
        Assert.IsTrue(action.ShouldPlay);
    }
}
