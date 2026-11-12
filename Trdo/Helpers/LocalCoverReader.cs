using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Services;

namespace Trdo.Helpers;

/// <summary>
/// Reads a local album's cover image off disk, coping with the file no longer being where the
/// station record says it is. A local station's favicon URL is resolved once by
/// <see cref="LocalMusicFolderScanner.FindCoverImage"/> at scan time and then kept as-is, so a
/// cover renamed or replaced afterwards leaves every consumer of that URL - each bound
/// <c>Image</c> in the UI and the SMTC thumbnail - holding a path that no longer exists.
/// <para>
/// Rather than retry a file that is known to be gone, a miss re-scans the folder and moves on
/// to whatever cover is there now. The redirect is remembered process-wide, so the many
/// independent loaders that share one stale URL (a station-list row, the now-playing card, the
/// mini player, the popup, SMTC, each again on every metadata refresh) pay for the failed read
/// and the re-scan once between them and log it once, instead of each rediscovering it.
/// </para>
/// </summary>
internal static class LocalCoverReader
{
    private const string LogComponent = "AlbumArt";

    /// <summary>Stale cover path (as originally requested) to the path it now resolves to.</summary>
    private static readonly ConcurrentDictionary<string, string> Redirects = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxThumbnails = 64;

    /// <summary>
    /// Results bigger than this are handed back but not kept in <see cref="Thumbnails"/>. A real
    /// thumbnail is tens of KB; anything this size is the original cover, returned unchanged
    /// because it could not be shrunk (a format whose size can't be read, or a failed transcode).
    /// Without this cap the map could pin <see cref="MaxThumbnails"/> full-size covers.
    /// </summary>
    private const int MaxInMemoryThumbBytes = 256 * 1024;

    /// <summary>
    /// Thumbnail bytes by cache file name (path + last-write time + size), so concurrent loaders
    /// of one cover share a single read and one disk-cache lookup, and a changed file gets a new name.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ThumbnailEntry> Thumbnails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A shared read, stamped with when it was added so the trim can drop the oldest.</summary>
    private sealed record ThumbnailEntry(long Order, Task<byte[]?> Read);

    private static long _thumbnailOrder;

    /// <summary>Paths already reported as missing with nothing to replace them, so the warning logs once.</summary>
    private static readonly ConcurrentDictionary<string, byte> ReportedMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the cover at <paramref name="path"/>, following a remembered redirect if the file
    /// moved earlier and discovering a new one if it is missing now. Returns null when the file
    /// is gone and its folder has no other cover image; that case is already logged, and no
    /// retry can help it. Other failures (a lock, a partial write) throw as usual so the caller
    /// can apply its own retry policy.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(string path)
    {
        string currentPath = Redirects.TryGetValue(path, out string? redirected) ? redirected : path;

        // What comes back is a small (ArtThumbnailCache.Edge) thumbnail, not the original: every
        // consumer (station rows, now-playing, mini player, popup, SMTC) shows it small, and a
        // full 5-10 MB read per consumer filled the large object heap with garbage. The source
        // file is only stat'ed here; it is read and re-encoded only when its thumbnail is missing
        // or its last-write time / size no longer match the one on disk.
        FileInfo info = new(currentPath);
        if (!info.Exists)
        {
            // Missing: look for a replacement cover in the folder and remember the redirect, so
            // this and every later call is served (and cached) against the replacement.
            string? replacement = TryRedirect(path, currentPath);
            if (replacement is null)
            {
                return null;
            }

            currentPath = replacement;
            info = new FileInfo(currentPath);
            if (!info.Exists)
            {
                // Gone again (a rename racing the scan): fail like any other read so the caller's
                // retry lands back here and re-scans.
                throw new FileNotFoundException("Local cover disappeared while resolving a replacement.", currentPath);
            }
        }

        string name = ArtThumbnailCache.LocalName(currentPath, info.LastWriteTimeUtc, info.Length);
        TrimThumbnails();

        ThumbnailEntry entry = Thumbnails.GetOrAdd(
            name,
            key => new ThumbnailEntry(Interlocked.Increment(ref _thumbnailOrder), BuildThumbnailAsync(currentPath, key)));
        try
        {
            byte[]? result = await entry.Read;
            if (result is null || result.Length > MaxInMemoryThumbBytes)
            {
                // Don't pin "nothing" (the file may be readable on a later call), or a full-size
                // cover that could not be shrunk (see MaxInMemoryThumbBytes).
                Thumbnails.TryRemove(new KeyValuePair<string, ThumbnailEntry>(name, entry));
            }

            return result;
        }
        catch
        {
            // Don't pin a failed read (a lock, a partial write); the caller's retry starts fresh.
            Thumbnails.TryRemove(new KeyValuePair<string, ThumbnailEntry>(name, entry));
            throw;
        }
    }

    /// <summary>
    /// Forgets everything held for the cover at <paramref name="path"/> - its redirect, its in-memory
    /// thumbnails and its on-disk ones (for the redirect target too) - so the next read starts over.
    /// Called when the user edits the station that owns the cover.
    /// </summary>
    public static void Forget(string path)
    {
        List<string> paths = [path];
        if (Redirects.TryRemove(path, out string? redirected))
        {
            paths.Add(redirected);
        }

        ReportedMissing.TryRemove(path, out _);
        foreach (string p in paths)
        {
            string prefix = ArtThumbnailCache.LocalPrefix(p);
            foreach (string key in Thumbnails.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    Thumbnails.TryRemove(key, out _);
                }
            }

            ArtThumbnailCache.InvalidateLocal(p);
        }
    }

    // Keeps the in-memory map bounded by dropping the oldest-inserted entries one at a time
    // rather than emptying it (which would make every cover on screen rebuild at once). The map
    // is at most MaxThumbnails entries, so a scan for the oldest is cheap.
    private static void TrimThumbnails()
    {
        while (Thumbnails.Count >= MaxThumbnails)
        {
            KeyValuePair<string, ThumbnailEntry>? oldest = null;
            foreach (KeyValuePair<string, ThumbnailEntry> pair in Thumbnails)
            {
                if (oldest is null || pair.Value.Order < oldest.Value.Value.Order)
                {
                    oldest = pair;
                }
            }

            if (oldest is null || !Thumbnails.TryRemove(oldest.Value))
            {
                break;
            }
        }
    }

    private static async Task<byte[]?> BuildThumbnailAsync(string currentPath, string name)
    {
        byte[]? cached = ArtThumbnailCache.TryRead(name);
        if (cached is not null)
        {
            return cached;
        }

        byte[] full;
        try
        {
            full = await File.ReadAllBytesAsync(currentPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        int length = full.Length;
        byte[] thumb = await ArtworkResizer.DownscaleAsync(full, ArtThumbnailCache.Edge);
        full = null!;

        await ArtThumbnailCache.WriteAsync(name, thumb, ArtThumbnailCache.LocalPrefix(currentPath));
        ArtThumbnailCache.ReleaseLargeBuffers(length);
        return thumb;
    }

    /// <summary>
    /// Finds a cover to use in place of the missing <paramref name="currentPath"/> and records the
    /// redirect from the originally requested <paramref name="path"/>. Null (logged once) when the
    /// folder has no other cover image.
    /// </summary>
    private static string? TryRedirect(string path, string currentPath)
    {
        string? replacement = LocalMusicFolderScanner.FindCoverImage(Path.GetDirectoryName(currentPath));
        if (replacement is null || string.Equals(replacement, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            if (ReportedMissing.TryAdd(currentPath, 0))
            {
                LogService.Warn(LogComponent, $"Local cover '{currentPath}' is gone and its folder has no other cover image; giving up");
            }

            return null;
        }

        // Only the loader that records (or changes) the redirect logs it; the others were
        // already in flight against the stale path and just follow along.
        ReportedMissing.TryRemove(currentPath, out _);
        if (Redirects.TryAdd(path, replacement))
        {
            LogService.Info(LogComponent, $"Local cover '{currentPath}' is gone; using '{replacement}' instead");
        }
        else if (!string.Equals(Redirects[path], replacement, StringComparison.OrdinalIgnoreCase))
        {
            Redirects[path] = replacement;
            LogService.Info(LogComponent, $"Local cover '{currentPath}' is gone; using '{replacement}' instead");
        }

        return replacement;
    }
}
