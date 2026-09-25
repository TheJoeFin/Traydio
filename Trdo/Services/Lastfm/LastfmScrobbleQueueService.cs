using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Windows.Storage;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Persists scrobbles that have been decided as eligible until they are confirmed sent to
/// Last.fm. File-based (like <see cref="FavoritesService"/>) rather than a
/// <c>LocalSettings</c> entry, since this is a growable list of structured records rather than a
/// single preference value.
/// </summary>
internal sealed class LastfmScrobbleQueueService
{
    private const int MaxBatchSize = 50;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private static readonly string _queueFilePath =
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "lastfm_scrobble_queue.json");

    private static readonly Lazy<LastfmScrobbleQueueService> _instance = new(() => new LastfmScrobbleQueueService());
    public static LastfmScrobbleQueueService Instance => _instance.Value;

    private readonly object _lock = new();
    private readonly List<LastfmPendingScrobble> _pending;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private DateTimeOffset? _nextRetryAllowedUtc;
    private int _consecutiveFailedFlushes;

    /// <summary>Raised when a flush fails because the session key Last.fm holds is no longer valid.</summary>
    public event EventHandler? SessionInvalidated;

    public int Count
    {
        get { lock (_lock) return _pending.Count; }
    }

    private LastfmScrobbleQueueService()
    {
        _pending = LoadInternal();
    }

    /// <summary>Adds a scrobble to the queue and persists immediately, so it survives a crash, not just a clean exit.</summary>
    public void Enqueue(LastfmPendingScrobble scrobble)
    {
        lock (_lock)
        {
            _pending.Add(scrobble);
            SaveInternal();
        }

        LogService.Info("Lastfm", $"Queued scrobble '{scrobble.Artist} - {scrobble.Track}' (queue depth {Count})");
    }

    /// <summary>
    /// Sends everything queued, oldest first, in batches of up to <see cref="MaxBatchSize"/>.
    /// A no-op while a flush is already running, the queue is empty, or a prior failure's
    /// backoff window has not elapsed yet.
    /// </summary>
    public async Task FlushAsync(LastfmApiClient client, string sessionKey, CancellationToken cancellationToken = default)
    {
        if (!await _flushGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            if (_nextRetryAllowedUtc is { } next && DateTimeOffset.UtcNow < next)
                return;

            while (true)
            {
                List<LastfmPendingScrobble> batch;
                lock (_lock)
                {
                    if (_pending.Count == 0)
                        return;

                    batch = [.. _pending.Take(MaxBatchSize)];
                }

                List<LastfmScrobbleRequest> requests = [.. batch.Select(ToRequest)];
                LastfmResult<(int Accepted, int Ignored)> result =
                    await client.ScrobbleAsync(requests, sessionKey, cancellationToken);

                if (result.IsSuccess)
                {
                    RemoveBatch(batch);

                    LogService.Info("Lastfm",
                        $"Flushed {batch.Count} scrobble(s) (accepted={result.Value.Accepted}, ignored={result.Value.Ignored}); {Count} remaining");

                    _consecutiveFailedFlushes = 0;
                    _nextRetryAllowedUtc = null;
                    continue;
                }

                if (result.RequiresReauth)
                {
                    LogService.Warn("Lastfm", "Session invalid; stopping flush until the user reconnects");
                    SessionInvalidated?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (!result.IsRetryable)
                {
                    // Not worth retrying (bad request, filtered by Last.fm) - drop this batch
                    // and keep going, rather than let one bad item block everything behind it.
                    RemoveBatch(batch);
                    LogService.Warn("Lastfm",
                        $"Dropped {batch.Count} scrobble(s) after non-retryable error: {result.ErrorMessage}");
                    continue;
                }

                _consecutiveFailedFlushes++;
                double backoffSeconds = Math.Min(
                    InitialBackoff.TotalSeconds * Math.Pow(2, _consecutiveFailedFlushes - 1),
                    MaxBackoff.TotalSeconds);
                _nextRetryAllowedUtc = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds);

                LogService.Warn("Lastfm",
                    $"Scrobble flush failed ({result.ErrorMessage}); retrying in {backoffSeconds:0}s");
                return;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void RemoveBatch(List<LastfmPendingScrobble> batch)
    {
        lock (_lock)
        {
            foreach (LastfmPendingScrobble item in batch)
                _pending.Remove(item);
            SaveInternal();
        }
    }

    private static LastfmScrobbleRequest ToRequest(LastfmPendingScrobble item) => new()
    {
        Artist = item.Artist,
        Track = item.Track,
        TimestampUnix = item.TimestampUnix,
        ChosenByUser = item.ChosenByUser,
        Album = item.Album,
        AlbumArtist = item.AlbumArtist,
        Mbid = item.Mbid,
        DurationSeconds = item.DurationSeconds,
        TrackNumber = item.TrackNumber,
    };

    private void SaveInternal()
    {
        try
        {
            string json = JsonSerializer.Serialize(_pending, LastfmScrobbleQueueJsonContext.Default.ListLastfmPendingScrobble);
            File.WriteAllText(_queueFilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Warn("Lastfm", $"Failed to persist scrobble queue: {ex.Message}");
        }
    }

    private static List<LastfmPendingScrobble> LoadInternal()
    {
        try
        {
            if (File.Exists(_queueFilePath))
            {
                string json = File.ReadAllText(_queueFilePath);
                return JsonSerializer.Deserialize(json, LastfmScrobbleQueueJsonContext.Default.ListLastfmPendingScrobble) ?? [];
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("Lastfm", $"Failed to load scrobble queue: {ex.Message}");
        }

        return [];
    }
}
