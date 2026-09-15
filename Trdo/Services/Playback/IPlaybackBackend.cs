using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;

namespace Trdo.Services.Playback;

public interface IPlaybackBackend : IDisposable
{
    PlaybackBackendKind Kind { get; }

    bool IsPlaying { get; }
    bool IsBuffering { get; }
    double BufferingProgress { get; }
    TimeSpan Position { get; }

    /// <summary>
    /// The current item's total duration, or <c>null</c> when there isn't one (a live radio
    /// stream, or nothing prepared yet).
    /// </summary>
    TimeSpan? Duration { get; }

    event EventHandler<bool>? PlaybackStateChanged;
    event EventHandler<bool>? BufferingStateChanged;
    event EventHandler<PlaybackFailureEventArgs>? PlaybackFailed;

    /// <summary>
    /// Raised when playback reaches the end of the current item on its own - distinct from
    /// <see cref="PlaybackStateChanged"/> going false, which also fires on pause/stop/error.
    /// Used to auto-advance to the next track in a local music folder.
    /// </summary>
    event EventHandler? PlaybackEnded;

    IReadOnlyList<MediaTimeRange> GetBufferedRanges();
    void SetVolume(double volume);
    Task<PlaybackPrepareResult> PrepareAsync(string streamUrl, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Seek(TimeSpan position);
    void ClearSource();
    MediaPlaybackItem? CurrentPlaybackItem { get; }
}
