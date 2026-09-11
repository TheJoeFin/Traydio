using System;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>Determines the selected track and whether playback continues after a local track ends.</summary>
public static class LocalMusicPlaybackPolicy
{
    public static LocalMusicTrackEndAction ResolveTrackEnd(
        int currentTrackIndex,
        int trackCount,
        LocalMusicLoopMode loopMode)
    {
        if (trackCount <= 0)
        {
            return new LocalMusicTrackEndAction(-1, ShouldPlay: false);
        }

        if (currentTrackIndex < 0 || currentTrackIndex >= trackCount)
        {
            throw new ArgumentOutOfRangeException(nameof(currentTrackIndex));
        }

        if (loopMode == LocalMusicLoopMode.Track)
        {
            return new LocalMusicTrackEndAction(currentTrackIndex, ShouldPlay: true);
        }

        if (currentTrackIndex < trackCount - 1)
        {
            return new LocalMusicTrackEndAction(currentTrackIndex + 1, ShouldPlay: true);
        }

        return new LocalMusicTrackEndAction(
            SelectedTrackIndex: 0,
            ShouldPlay: loopMode == LocalMusicLoopMode.Album);
    }
}

/// <summary>The selection and playback action to apply after a local track ends.</summary>
public readonly record struct LocalMusicTrackEndAction(int SelectedTrackIndex, bool ShouldPlay);
