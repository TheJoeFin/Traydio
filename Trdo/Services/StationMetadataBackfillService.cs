using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// Looks up genre, country and language for stations that do not have them.
/// <para>
/// Only ever runs because the user asked. Stations added from the directory already carry their
/// details; this exists for the ones that do not - added by hand, imported from a playlist, or
/// saved before the app kept any of it.
/// </para>
/// <para>
/// Threading: every await here deliberately resumes on the calling thread, so the merge - which
/// raises change notification on models the list is bound to - happens on the UI thread. Adding
/// <c>ConfigureAwait(false)</c> anywhere in this file would move that onto a thread pool thread
/// and the bindings would throw.
/// </para>
/// </summary>
public sealed class StationMetadataBackfillService
{
    /// <summary>How long a station's details are considered current.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// Gap between requests, giving roughly three per second. Radio Browser is a free service
    /// run on donated hardware and asks callers not to hammer it.
    /// </summary>
    private const int RequestSpacingMs = 300;

    /// <summary>
    /// Ceiling on one run. A very large list is better done in a few passes than in one that
    /// leaves the app unusable for several minutes.
    /// </summary>
    private const int MaxStationsPerRun = 200;

    /// <summary>
    /// After this many transport failures in a row, the directory is down. Grinding through
    /// another hundred ten-second timeouts helps nobody.
    /// </summary>
    private const int ConsecutiveFailureLimit = 3;

    /// <summary>Stations updated between saves, so a cancel does not discard completed work.</summary>
    private const int SaveEvery = 10;

    private static readonly Lazy<StationMetadataBackfillService> _instance = new(() => new StationMetadataBackfillService());
    public static StationMetadataBackfillService Instance => _instance.Value;

    // The service holds no per-instance state beyond its HttpClient, which is static.
    private readonly RadioBrowserService _radioBrowser = new();

    /// <summary>Keeps requests strictly one at a time, whatever the caller does.</summary>
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    /// <summary>The outcome of a batch run.</summary>
    /// <param name="Updated">Stations whose details changed.</param>
    /// <param name="Attempted">Stations looked up.</param>
    /// <param name="NotFound">Stations the directory had no entry for.</param>
    /// <param name="Ambiguous">Stations where several entries shared the stream URL.</param>
    /// <param name="Skipped">Stations left out because the run hit its ceiling.</param>
    /// <param name="AbortedUnreachable">True if the run stopped early because the directory stopped responding.</param>
    /// <param name="Cancelled">True if the user stopped the run.</param>
    public readonly record struct BackfillResult(
        int Updated,
        int Attempted,
        int NotFound,
        int Ambiguous,
        int Skipped,
        bool AbortedUnreachable,
        bool Cancelled);

    /// <summary>
    /// The stations a run would look at, in list order.
    /// </summary>
    /// <param name="overwriteExisting">
    /// When false, only stations with no directory details at all are considered.
    /// </param>
    public static List<RadioStation> SelectCandidates(
        IEnumerable<RadioStation> stations,
        bool overwriteExisting)
    {
        List<RadioStation> candidates = [];
        if (stations is null)
            return candidates;

        DateTimeOffset staleBefore = DateTimeOffset.UtcNow - RefreshInterval;

        foreach (RadioStation station in stations)
        {
            if (station is null || string.IsNullOrWhiteSpace(station.StreamUrl))
                continue;

            if (!overwriteExisting)
            {
                // Already knows what it is.
                if (!string.IsNullOrWhiteSpace(station.StationUuid))
                    continue;
                if (!string.IsNullOrWhiteSpace(station.Tags) || !string.IsNullOrWhiteSpace(station.Country))
                    continue;
            }

            // Looked up recently enough that asking again would just be noise.
            if (station.MetadataRefreshedUtc is DateTimeOffset refreshed && refreshed > staleBefore)
                continue;

            candidates.Add(station);
        }

        return candidates;
    }

    /// <summary>
    /// Looks up one station and applies whatever the directory knows.
    /// </summary>
    /// <returns>The match that was applied, or null when the directory had no entry for the stream.</returns>
    public async Task<StationMetadataMatchPolicy.MetadataMatch?> RefreshOneAsync(
        RadioStation station,
        bool overwriteExisting,
        CancellationToken cancellationToken = default)
    {
        if (station is null)
            return null;

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            List<RadioBrowserStation> candidates =
                await _radioBrowser.LookupByUrlAsync(station.StreamUrl, cancellationToken);

            if (StationMetadataMatchPolicy.SelectBestMatch(station, candidates) is not
                StationMetadataMatchPolicy.MetadataMatch match)
            {
                return null;
            }

            StationMetadataMergePolicy.Merge(station, match.Station, overwriteExisting);
            return match;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Looks up a batch of stations, reporting progress as it goes.
    /// </summary>
    /// <param name="candidates">The stations to look up, from <see cref="SelectCandidates"/>.</param>
    /// <param name="overwriteExisting">Whether to replace details a station already has.</param>
    /// <param name="onProgress">Called after each station with (completed, total, station name).</param>
    /// <param name="onPartialSave">
    /// Called periodically so the caller can persist what has been done so far. Without this a
    /// cancelled run would throw away every lookup it had already paid for.
    /// </param>
    public async Task<BackfillResult> RefreshManyAsync(
        IReadOnlyList<RadioStation> candidates,
        bool overwriteExisting,
        Action<int, int, string>? onProgress = null,
        Action? onPartialSave = null,
        CancellationToken cancellationToken = default)
    {
        if (candidates is null || candidates.Count == 0)
            return new BackfillResult(0, 0, 0, 0, 0, false, false);

        int total = Math.Min(candidates.Count, MaxStationsPerRun);
        int skipped = candidates.Count - total;

        int updated = 0, attempted = 0, notFound = 0, ambiguous = 0;
        int consecutiveFailures = 0;
        int sinceLastSave = 0;
        bool aborted = false;
        bool cancelled = false;

        for (int i = 0; i < total; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            RadioStation station = candidates[i];
            onProgress?.Invoke(i, total, station.Name);

            try
            {
                StationMetadataMatchPolicy.MetadataMatch? match =
                    await RefreshOneAsync(station, overwriteExisting, cancellationToken);

                attempted++;
                consecutiveFailures = 0;

                if (match is null)
                {
                    notFound++;
                }
                else
                {
                    updated++;
                    sinceLastSave++;
                    if (!match.Value.IsExact)
                        ambiguous++;
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StationMetadataBackfill] '{station.Name}' failed: {ex.Message}");
                attempted++;
                notFound++;

                if (++consecutiveFailures >= ConsecutiveFailureLimit)
                {
                    aborted = true;
                    break;
                }
            }

            if (sinceLastSave >= SaveEvery)
            {
                onPartialSave?.Invoke();
                sinceLastSave = 0;
            }

            if (i < total - 1)
            {
                try
                {
                    await Task.Delay(RequestSpacingMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
            }
        }

        onProgress?.Invoke(total, total, string.Empty);

        if (updated > 0)
            onPartialSave?.Invoke();

        return new BackfillResult(updated, attempted, notFound, ambiguous, skipped, aborted, cancelled);
    }

    /// <summary>
    /// A one-line summary of what a station now knows about itself, for the confirmation shown
    /// after a single-station refresh.
    /// </summary>
    public static string DescribeStation(RadioStation station)
    {
        List<string> parts = [];

        if (station.PrimaryGenre is string genre)
            parts.Add(genre);
        if (!string.IsNullOrWhiteSpace(station.Country))
            parts.Add(station.Country!);
        if (!string.IsNullOrWhiteSpace(station.Language))
            parts.Add(station.Language!);
        if (station.Bitrate is int bitrate and > 0)
            parts.Add(!string.IsNullOrWhiteSpace(station.Codec) ? LocalizedText.Format("Ui_StationMetadataBackfillService_8d05b3d678", "{0} kbps {1}", bitrate, station.Codec!) : LocalizedText.Format("Ui_StationMetadataBackfillService_b6b26412c5", "{0} kbps", bitrate));
        else if (!string.IsNullOrWhiteSpace(station.Codec))
            parts.Add(station.Codec!);

        return parts.Count == 0 ? LocalizedText.Get("Ui_StationMetadataBackfillService_af714df177", "No extra details were available.") : string.Join(" · ", parts);
    }
}
