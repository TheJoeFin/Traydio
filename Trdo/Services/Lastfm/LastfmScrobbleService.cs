using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Turns playback events into Last.fm now-playing updates and scrobbles. Modeled on
/// <see cref="PlaylistHistoryService"/>: a lazy singleton whose constructor subscribes to
/// <see cref="RadioPlayerService"/>'s gated events, so it sees exactly the track the listener can
/// actually hear rather than metadata still running ahead of the audio.
/// </summary>
internal sealed class LastfmScrobbleService
{
    private static readonly Lazy<LastfmScrobbleService> _instance = new(() => new LastfmScrobbleService());
    public static LastfmScrobbleService Instance => _instance.Value;

    private readonly RadioPlayerService _player = RadioPlayerService.Instance;
    private readonly ScrobbleTimingTracker _tracker = new();
    private readonly object _lock = new();

    private LastfmApiClient? _client;

    // The candidate currently being timed. Guarded by _lock.
    private string? _candidateArtist;
    private string? _candidateTrack;
    private string? _candidateAlbum;
    private string? _candidateAlbumArtist;
    private TimeSpan? _candidateDuration;
    private long _candidateStartedAtUnix;
    private bool _candidateChosenByUser;
    private bool _hasCandidate;

    // The token ScrobbleTimingTracker.Start returned for the current candidate. Compared against
    // whatever token a BecameEligible event carries before trusting it, in case that event's
    // delivery was delayed (blocked on _lock) past a subsequent track change - see
    // ScrobbleTimingTracker.BecameEligible for why this is necessary.
    private long _candidateToken;

    /// <summary>Ensures the service is constructed - and so subscribed - early in app startup.</summary>
    public static void EnsureInitialized() => _ = Instance;

    private LastfmScrobbleService()
    {
        _tracker.BecameEligible += OnTrackerBecameEligible;

        _player.StreamMetadataChanged += OnStreamMetadataChanged;
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.BufferingStateChanged += OnBufferingStateChanged;
        _player.LocalTrackChanged += OnLocalTrackChanged;

        LastfmScrobbleQueueService.Instance.SessionInvalidated += (_, _) => LastfmAccountStore.ClearSession();

        LogService.Info("Lastfm", "LastfmScrobbleService initialized");
    }

    private static bool IsEnabled => SettingsService.IsLastfmScrobblingEnabled;

    private LastfmApiClient? GetClient()
    {
        if (_client is not null)
            return _client;

        if (!LastfmCredentials.TryGetCredentials(out string apiKey, out string apiSecret))
            return null;

        _client = new LastfmApiClient(apiKey, apiSecret);
        return _client;
    }

    /// <summary>Radio-stream metadata. Local files are handled by <see cref="OnLocalTrackChanged"/> instead,
    /// which has access to the exact duration/album TagLib provides and stream metadata never does.</summary>
    private void OnStreamMetadataChanged(object? sender, StreamMetadata metadata)
    {
        if (_player.ActiveSourceKind != AudioSourceKind.Radio)
            return;

        bool hasTrack = metadata?.HasMetadata == true &&
            !string.IsNullOrWhiteSpace(metadata.Artist) &&
            !string.IsNullOrWhiteSpace(metadata.Title);

        HandleTrackChanged(
            hasTrack ? metadata!.Artist : null,
            hasTrack ? metadata!.Title : null,
            album: null,
            albumArtist: null,
            knownDuration: null,
            chosenByUser: false);
    }

    private void OnLocalTrackChanged(object? sender, EventArgs e)
    {
        StreamMetadata metadata = _player.CurrentMetadata;
        string? artist = metadata.Artist;
        string? title = metadata.Title;
        string? album = null;
        string? albumArtist = null;

        IReadOnlyList<string> tracks = _player.CurrentLocalTrackList;
        int index = _player.CurrentLocalTrackIndex;

        if (index >= 0 && index < tracks.Count)
        {
            string path = tracks[index];
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(path);

                if (string.IsNullOrWhiteSpace(artist))
                    artist = tagFile.Tag.FirstPerformer;
                if (string.IsNullOrWhiteSpace(title))
                    title = tagFile.Tag.Title;

                album = string.IsNullOrWhiteSpace(tagFile.Tag.Album) ? null : tagFile.Tag.Album;
                albumArtist = string.IsNullOrWhiteSpace(tagFile.Tag.FirstAlbumArtist) ? null : tagFile.Tag.FirstAlbumArtist;
            }
            catch (Exception ex)
            {
                LogService.Warn("Lastfm", $"Could not read tags for local track: {ex.Message}");
            }
        }

        HandleTrackChanged(
            artist,
            title,
            album,
            albumArtist,
            _player.Duration,
            chosenByUser: true);
    }

    private void HandleTrackChanged(
        string? artist, string? title, string? album, string? albumArtist, TimeSpan? knownDuration, bool chosenByUser)
    {
        lock (_lock)
        {
            // Whatever was being timed is over - it changed a track, switched stations, or
            // stopped entirely. Judged by how long it was actually heard either way: a track
            // played for minutes before a station switch is exactly as scrobble-worthy as one
            // that changed naturally, while a station briefly passed over while scanning never
            // crosses the retroactive floor.
            ResolveOutgoingCandidateLocked();

            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                _hasCandidate = false;
                _tracker.Stop();
                return;
            }

            _candidateArtist = artist;
            _candidateTrack = title;
            _candidateAlbum = album;
            _candidateAlbumArtist = albumArtist;
            _candidateDuration = knownDuration;
            _candidateChosenByUser = chosenByUser;
            _candidateStartedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _hasCandidate = true;

            _candidateToken = _tracker.Start(knownDuration);
        }

        if (IsEnabled)
            _ = SendNowPlayingAsync(artist!, title!, album, albumArtist, knownDuration);
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void ResolveOutgoingCandidateLocked()
    {
        if (!_hasCandidate)
            return;

        _hasCandidate = false;

        if (!IsEnabled)
            return;

        TimeSpan elapsed = _tracker.Elapsed;
        if (!LastfmScrobbleEligibilityPolicy.IsRetroactivelyEligible(elapsed, _candidateDuration))
            return;

        EnqueueScrobble(
            _candidateArtist!, _candidateTrack!, _candidateAlbum, _candidateAlbumArtist,
            _candidateDuration, _candidateStartedAtUnix, _candidateChosenByUser);
    }

    private void OnTrackerBecameEligible(long candidateToken)
    {
        string? artist, track, album, albumArtist;
        TimeSpan? duration;
        long startedAtUnix;
        bool chosenByUser;

        lock (_lock)
        {
            // The candidate may have changed since the tracker's timer fired - it can take a
            // moment to get here if this thread was waiting on _lock - so the token from
            // ScrobbleTimingTracker.Start is checked rather than trusting _hasCandidate alone.
            if (!_hasCandidate || candidateToken != _candidateToken)
                return;

            artist = _candidateArtist;
            track = _candidateTrack;
            album = _candidateAlbum;
            albumArtist = _candidateAlbumArtist;
            duration = _candidateDuration;
            startedAtUnix = _candidateStartedAtUnix;
            chosenByUser = _candidateChosenByUser;

            // Decided for this candidate now - ResolveOutgoingCandidateLocked must not ask again.
            _hasCandidate = false;
        }

        if (artist is null || track is null || !IsEnabled)
            return;

        EnqueueScrobble(artist, track, album, albumArtist, duration, startedAtUnix, chosenByUser);
    }

    private void EnqueueScrobble(
        string artist, string track, string? album, string? albumArtist,
        TimeSpan? duration, long startedAtUnix, bool chosenByUser)
    {
        LastfmPendingScrobble scrobble = new()
        {
            Artist = artist,
            Track = track,
            Album = album,
            AlbumArtist = albumArtist,
            DurationSeconds = duration.HasValue ? (int)duration.Value.TotalSeconds : null,
            TimestampUnix = startedAtUnix,
            ChosenByUser = chosenByUser,
        };

        LastfmScrobbleQueueService.Instance.Enqueue(scrobble);
        FlushPendingQueue();
    }

    private void OnPlaybackStateChanged(object? sender, bool isPlaying)
    {
        if (isPlaying)
            _tracker.Resume();
        else
            _tracker.Pause();
    }

    private void OnBufferingStateChanged(object? sender, bool isBuffering)
    {
        if (isBuffering)
            _tracker.Pause();
        else if (_player.IsPlaying)
            _tracker.Resume();
    }

    private async Task SendNowPlayingAsync(
        string artist, string track, string? album, string? albumArtist, TimeSpan? duration)
    {
        LastfmApiClient? client = GetClient();
        if (client is null)
            return;

        if (!LastfmAccountStore.TryGetSession(out _, out string? sessionKey) || sessionKey is null)
            return;

        LastfmNowPlayingRequest request = new()
        {
            Artist = artist,
            Track = track,
            Album = album,
            AlbumArtist = albumArtist,
            DurationSeconds = duration.HasValue ? (int)duration.Value.TotalSeconds : null,
        };

        LastfmResult<bool> result = await client.UpdateNowPlayingAsync(request, sessionKey);
        if (result.RequiresReauth)
            LastfmAccountStore.ClearSession();
    }

    /// <summary>Sends whatever is queued, if there is a client and an active session. Called after every
    /// enqueue, and worth calling again once a session is (re)established.</summary>
    public void FlushPendingQueue() => _ = FlushQueueAsync();

    private async Task FlushQueueAsync()
    {
        LastfmApiClient? client = GetClient();
        if (client is null)
            return;

        if (!LastfmAccountStore.TryGetSession(out _, out string? sessionKey) || sessionKey is null)
            return;

        await LastfmScrobbleQueueService.Instance.FlushAsync(client, sessionKey);
    }
}
