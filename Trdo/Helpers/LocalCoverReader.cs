using System;
using System.Collections.Concurrent;
using System.IO;
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

        try
        {
            return await File.ReadAllBytesAsync(currentPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
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

            // If this one is missing too (a rename racing the scan), the exception reaches the
            // caller like any other read failure; its next attempt lands back here and re-scans.
            return await File.ReadAllBytesAsync(replacement);
        }
    }
}
