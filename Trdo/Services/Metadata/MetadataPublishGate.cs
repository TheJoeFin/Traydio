using System;
using System.Threading;
using Trdo.Models;

namespace Trdo.Services.Metadata;

/// <summary>
/// Schedules a single deferred callback. Exists so <see cref="MetadataPublishGate"/> can be
/// driven by a fake clock in tests: the gate sits on a background thread ahead of the UI
/// marshal, so it cannot use a dispatcher timer, and a real timer would make its tests
/// depend on wall-clock sleeps.
/// </summary>
public interface IDelayScheduler
{
    /// <summary>Runs <paramref name="callback"/> after <paramref name="delay"/>, replacing any pending callback.</summary>
    void Schedule(TimeSpan delay, Action callback);

    /// <summary>Drops a callback that has not run yet. A callback already dispatched may still run.</summary>
    void Cancel();
}

/// <summary>
/// The production <see cref="IDelayScheduler"/>: a single reusable thread-pool timer.
/// </summary>
public sealed partial class ThreadPoolDelayScheduler : IDelayScheduler, IDisposable
{
    private readonly Timer _timer;
    private readonly object _callbackLock = new();
    private Action? _callback;

    public ThreadPoolDelayScheduler()
    {
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Schedule(TimeSpan delay, Action callback)
    {
        lock (_callbackLock)
        {
            _callback = callback;
        }

        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public void Cancel()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Fire()
    {
        Action? callback;
        lock (_callbackLock)
        {
            callback = _callback;
        }

        callback?.Invoke();
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}

/// <summary>
/// Holds newly observed track metadata back so it reaches the app at the moment the audio
/// describing it does.
/// </summary>
/// <remarks>
/// Stations send metadata for the track they are about to play, while the listener is still
/// hearing whatever is in the buffer, so without this every surface names a song several
/// seconds before it can be heard. The gate sits between the metadata orchestrator (which
/// decides <em>what</em> is new) and the app-wide event (which decides <em>who</em> hears about
/// it), so a single held value feeds the window, the mini player, the media transport controls,
/// the tray tooltip, the playlist history and the song-change popup alike - they cannot
/// disagree with each other, because there is only one publication.
/// <para>
/// Two arrivals are deliberately not held. Blank metadata means playback stopped or the station
/// cleared its title: there is nothing to line up with the audio, and holding it would leave a
/// finished track on screen. And the first track after <see cref="Reset"/> - a station start -
/// was already playing before the listener tuned in, so its metadata describes the audio
/// arriving right now rather than audio still in the buffer.
/// </para>
/// </remarks>
public sealed partial class MetadataPublishGate : IDisposable
{
    private readonly IDelayScheduler _scheduler;
    private readonly bool _ownsScheduler;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();

    private volatile StreamMetadata _current = StreamMetadata.Empty;
    private StreamMetadata? _pending;
    private DateTimeOffset _pendingArrivedAtUtc;
    private double _delaySeconds;

    /// <summary>
    /// True while nothing has been published since the last <see cref="Reset"/>, which makes the
    /// next real track a station's opening one.
    /// </summary>
    private bool _publishNextImmediately = true;

    /// <summary>
    /// Bumped whenever a scheduled publication stops being the current intent. A thread-pool
    /// timer callback that is already on its way cannot be called back, so it carries the
    /// generation it was scheduled under and does nothing when that no longer matches - without
    /// this, a superseded track could still be published after a newer one replaced it.
    /// </summary>
    private long _generation;

    public MetadataPublishGate(IDelayScheduler? scheduler = null, Func<DateTimeOffset>? clock = null)
    {
        _ownsScheduler = scheduler is null;
        _scheduler = scheduler ?? new ThreadPoolDelayScheduler();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised when metadata becomes the app's current track. May fire on a background thread.</summary>
    public event EventHandler<StreamMetadata>? MetadataPublished;

    /// <summary>
    /// The track the app is currently showing. Never the track being held: everything that reads
    /// "what is playing" reads this, so during a hold it deliberately still reports the previous
    /// track.
    /// </summary>
    public StreamMetadata Current => _current;

    /// <summary>Optional diagnostic sink, so the gate itself stays free of platform dependencies.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Asked, when a held track's wait runs out, whether audio is still running. A held track
    /// only makes sense as a description of something the listener is hearing, so if playback
    /// stopped during the wait the track is dropped rather than published into silence.
    /// </summary>
    /// <remarks>
    /// This is the backstop rather than the mechanism: a stop is expected to reach
    /// <see cref="Reset"/> at the moment it happens. But "audio stopped" arrives by several
    /// routes - the user pausing, a hardware button, a backend reporting Stopped or EndReached,
    /// a stream failing - and a route that forgets to reset would otherwise announce a track
    /// over silence. Checking here means no such route can. Buffering counts as active: a
    /// stream stuttering mid-track is still playing that track.
    /// </remarks>
    public Func<bool>? IsPlaybackActive { get; set; }

    /// <summary>
    /// How long to hold a mid-stream track change. Changing this re-times a track that is
    /// already being held, measured from when it arrived, so adjusting the delay affects the
    /// track in hand rather than only the next one.
    /// </summary>
    public double DelaySeconds
    {
        get
        {
            lock (_lock)
            {
                return _delaySeconds;
            }
        }
        set
        {
            double clamped = SongChangeAnnouncementPolicy.ClampDelay(value);
            StreamMetadata? publishNow = null;

            lock (_lock)
            {
                if (Math.Abs(_delaySeconds - clamped) < 0.0001)
                    return;

                _delaySeconds = clamped;

                if (_pending is null)
                    return;

                TimeSpan remaining = TimeSpan.FromSeconds(clamped) - (_clock() - _pendingArrivedAtUtc);
                if (remaining <= TimeSpan.Zero)
                {
                    publishNow = TakePendingForImmediatePublish();
                }
                else
                {
                    Reschedule(remaining);
                }
            }

            if (publishNow is not null)
            {
                Log?.Invoke($"Delay now {clamped}s; publishing held '{publishNow.DisplayText}'");
                Raise(publishNow);
            }
        }
    }

    /// <summary>
    /// Offers newly observed metadata, which is either published straight away or held for
    /// <see cref="DelaySeconds"/>. A newer track arriving during a hold replaces the held one and
    /// restarts the wait from its own arrival: publishing the superseded track would name a song
    /// that is already over.
    /// </summary>
    public void Submit(StreamMetadata metadata)
    {
        if (metadata is null)
            return;

        StreamMetadata? publishNow = null;
        string? logLine = null;

        lock (_lock)
        {
            DropPending();

            bool isBlank = !metadata.HasMetadata;
            if (isBlank || _publishNextImmediately || _delaySeconds <= 0)
            {
                // A blank arrival must not consume the station-start flag: a station that clears
                // its title between tracks would otherwise spend the rest of the session
                // publishing every track immediately.
                if (!isBlank)
                    _publishNextImmediately = false;

                _current = metadata;
                publishNow = metadata;
            }
            else
            {
                _pending = metadata;
                _pendingArrivedAtUtc = _clock();
                Reschedule(TimeSpan.FromSeconds(_delaySeconds));
                logLine = $"Holding '{metadata.DisplayText}' for {_delaySeconds}s";
            }
        }

        // A blank passes straight through and is not worth a line: the orchestrator already
        // logs the change of state it represents, and stopping a stream drives one here anyway.
        if (publishNow is not null && publishNow.HasMetadata)
            logLine = $"Publishing '{publishNow.DisplayText}' immediately";

        if (logLine is not null)
            Log?.Invoke(logLine);

        if (publishNow is not null)
            Raise(publishNow);
    }

    /// <summary>
    /// Drops anything being held and treats the next track as a station start. Called whenever
    /// the stream the held track belongs to goes away - a station switch, a pause, a pipeline
    /// rebuild - because publishing it afterwards would name a song the user is no longer
    /// listening to.
    /// </summary>
    /// <remarks>
    /// Deliberately does not publish <see cref="StreamMetadata.Empty"/>: the metadata
    /// orchestrator already drives a blank through the gate when it stops its providers, and
    /// that has to happen before the flag is armed so the blank cannot consume it.
    /// </remarks>
    public void Reset()
    {
        lock (_lock)
        {
            DropPending();
            _publishNextImmediately = true;
        }
    }

    /// <summary>Cancels a held track. Caller must hold <see cref="_lock"/>.</summary>
    private void DropPending()
    {
        _pending = null;
        _generation++;
        _scheduler.Cancel();
    }

    /// <summary>Arms the timer for the currently held track. Caller must hold <see cref="_lock"/>.</summary>
    private void Reschedule(TimeSpan delay)
    {
        long generation = ++_generation;
        _scheduler.Schedule(delay, () => PublishPending(generation));
    }

    /// <summary>Promotes the held track to current. Caller must hold <see cref="_lock"/>.</summary>
    private StreamMetadata? TakePendingForImmediatePublish()
    {
        StreamMetadata? pending = _pending;
        if (pending is null)
            return null;

        _pending = null;
        _generation++;
        _scheduler.Cancel();
        _publishNextImmediately = false;
        _current = pending;
        return pending;
    }

    private void PublishPending(long generation)
    {
        // Evaluated before taking the lock: the caller reads player state, which has no
        // business running underneath the gate's own lock.
        bool playbackActive = IsPlaybackActive?.Invoke() ?? true;

        StreamMetadata publishNow;

        lock (_lock)
        {
            if (generation != _generation || _pending is null)
                return;

            if (!playbackActive)
            {
                StreamMetadata dropped = _pending;
                _pending = null;

                // Whatever comes next is the opening track of whatever the listener starts
                // playing, so it should appear as soon as it arrives.
                _publishNextImmediately = true;

                Log?.Invoke($"Delay elapsed for '{dropped.DisplayText}' but playback had stopped; dropping");
                return;
            }

            publishNow = _pending;
            _pending = null;
            _publishNextImmediately = false;
            _current = publishNow;
        }

        Log?.Invoke($"Delay elapsed; publishing '{publishNow.DisplayText}'");
        Raise(publishNow);
    }

    private void Raise(StreamMetadata metadata)
    {
        MetadataPublished?.Invoke(this, metadata);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DropPending();
        }

        if (_ownsScheduler && _scheduler is IDisposable disposable)
            disposable.Dispose();
    }
}
