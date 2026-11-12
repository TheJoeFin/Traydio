using System;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Services;
using Windows.Storage;

namespace Trdo.Helpers;

/// <summary>
/// On-disk cache of small (<see cref="Edge"/> px) copies of album covers and station art, so the
/// app only ever holds, decodes and hands to SMTC a thumbnail - never the 5-10 MB original.
/// <list type="bullet">
/// <item>Local covers are keyed by path plus the file's last-write time and size (in the file name),
/// so the only check is a stat of the source, and a changed file simply misses and is regenerated.</item>
/// <item>Remote station art is keyed by URL alone and is never re-validated or re-downloaded;
/// <see cref="Invalidate"/> (called when the user edits a station) is the only thing that drops it.</item>
/// </list>
/// </summary>
internal static class ArtThumbnailCache
{
    public const int Edge = 256;

    private const string Extension = ".img";
    private const int MaxFiles = 400;
    private const int TrimToFiles = 300;
    private const int MaxThumbBytes = 1024 * 1024;
    private const int LargeSourceBytes = 1024 * 1024;

    private static readonly Lazy<string> Root = new(() =>
    {
        string dir = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "art");
        Directory.CreateDirectory(dir);
        return dir;
    });

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    /// <summary>File-name prefix shared by every generation of a local cover's thumbnail.</summary>
    public static string LocalPrefix(string path) => Hash(path.ToLowerInvariant()) + "-";

    public static string LocalName(string path, DateTime lastWriteUtc, long length) =>
        $"{LocalPrefix(path)}{lastWriteUtc.Ticks:x}-{length:x}{Extension}";

    private const string RemotePrefix = "r-";

    // Bumped by Invalidate so a download already in flight doesn't write its (now stale) result back.
    private static int _generation;

    private static string RemoteName(string url) => RemotePrefix + Hash(url) + Extension;

    public static byte[]? TryRead(string name)
    {
        try
        {
            string path = Path.Combine(Root.Value, name);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (name.StartsWith(RemotePrefix, StringComparison.Ordinal))
            {
                // Trim evicts by last-write time; touching it on use makes that least-recently-used,
                // so art that is still shown outlives art that was cached once and forgotten.
                try
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return bytes;
        }
        catch (Exception ex)
        {
            LogService.Warn("AlbumArt", $"Could not read cached thumbnail {name}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a thumbnail, then removes older generations that share <paramref name="supersedesPrefix"/>.
    /// A failure only costs the cache entry, so it is logged rather than thrown.
    /// </summary>
    public static async Task WriteAsync(string name, byte[] thumb, string? supersedesPrefix = null)
    {
        if (thumb.Length == 0 || thumb.Length > MaxThumbBytes)
        {
            return;
        }

        try
        {
            string path = Path.Combine(Root.Value, name);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, thumb);
            File.Move(temp, path, overwrite: true);

            if (supersedesPrefix is not null)
            {
                foreach (string old in Directory.EnumerateFiles(Root.Value, supersedesPrefix + "*" + Extension))
                {
                    if (!string.Equals(Path.GetFileName(old), name, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(old);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("AlbumArt", $"Could not cache thumbnail {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Thumbnail for a remote <b>station</b> image: the cached copy if there is one (never
    /// re-checked against the server), otherwise <paramref name="download"/>ed, shrunk and cached.
    /// Null when the download was refused (over the size limit). Only for art whose URL always
    /// serves the same picture; per-track art is often a fixed URL whose content changes, so it
    /// must not come through here.
    /// </summary>
    public static async Task<byte[]?> GetRemoteAsync(string url, Func<Task<byte[]?>> download)
    {
        string name = RemoteName(url);
        byte[]? cached = TryRead(name);
        if (cached is not null)
        {
            return cached;
        }

        int generation = Volatile.Read(ref _generation);
        byte[]? full = await download();
        if (full is null)
        {
            return null;
        }

        int length = full.Length;
        byte[] thumb = await ArtworkResizer.DownscaleAsync(full, Edge);
        full = null;

        // Invalidated while downloading (the user edited the station): hand back what was fetched
        // but don't let it replace the entry that was just dropped.
        if (generation == Volatile.Read(ref _generation))
        {
            await WriteAsync(name, thumb);
            Trim();
        }

        ReleaseLargeBuffers(length);
        return thumb;
    }

    /// <summary>
    /// Drops the cached thumbnail for a remote station-art <paramref name="url"/> so the next load
    /// re-downloads it. Called when the user edits a station. (Local covers: <see cref="InvalidateLocal"/>.)
    /// </summary>
    public static void Invalidate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        try
        {
            TryDelete(Path.Combine(Root.Value, RemoteName(url)));
        }
        catch (Exception ex)
        {
            LogService.Warn("AlbumArt", $"Could not invalidate cached thumbnail for {url}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Drops every generation of a local cover's thumbnail (see <see cref="LocalPrefix"/>).</summary>
    public static void InvalidateLocal(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Root.Value, LocalPrefix(path) + "*" + Extension))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("AlbumArt", $"Could not invalidate cached thumbnail for {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A full-size read lands on the large object heap, which the GC otherwise only reclaims (and
    /// returns to the OS) on some later gen-2 pass. The live heap is a few MB, so one compacting
    /// pass after a large source is shrunk is cheap - but loading a list of covers shrinks many in
    /// a row, so the pass is debounced to run once when the burst goes quiet, not once per cover.
    /// </summary>
    public static void ReleaseLargeBuffers(int sourceLength)
    {
        if (sourceLength < LargeSourceBytes)
        {
            return;
        }

        ReleaseTimer.Change(ReleaseDelayMs, Timeout.Infinite);
    }

    private const int ReleaseDelayMs = 2000;

    private static readonly Timer ReleaseTimer = new(static _ =>
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    });

    // Keep the remote folder bounded: past MaxFiles the least recently used go (reads refresh the
    // timestamp), and anything evicted just re-downloads. Only remote entries are counted; local
    // covers are replaced per file by WriteAsync's supersedesPrefix.
    private static void Trim()
    {
        try
        {
            FileInfo[] files = new DirectoryInfo(Root.Value).GetFiles(RemotePrefix + "*" + Extension);
            if (files.Length <= MaxFiles)
            {
                return;
            }

            foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - TrimToFiles))
            {
                TryDelete(file.FullName);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("AlbumArt", $"Could not trim the thumbnail cache: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
