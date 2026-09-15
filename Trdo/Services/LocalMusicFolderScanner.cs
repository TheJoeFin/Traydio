using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Trdo.Services;

/// <summary>
/// Scans a folder for audio files for a <see cref="Models.AudioSourceKind.Files"/> station.
/// Stateless: called fresh every time the folder's contents need to be known, so renamed,
/// added, or removed files are always reflected rather than going stale against a cached list.
/// </summary>
internal static class LocalMusicFolderScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".wma",
    };

    // Checked in this order - "cover" and "folder" are by far the most common names left by
    // rips and downloads, "front"/"album" cover the rest seen in the wild.
    private static readonly string[] CoverFileBaseNames = ["cover", "folder", "front", "album"];
    private static readonly string[] CoverFileExtensionList = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly HashSet<string> CoverFileExtensions = new(CoverFileExtensionList, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the audio files directly inside <paramref name="folderPath"/> (not recursive -
    /// "a local folder of music" is one flat folder), ordered by filename so playback order is
    /// stable and predictable across scans. Returns an empty list if the folder doesn't exist
    /// or can't be read rather than throwing, since this runs on paths persisted from disk that
    /// may have moved or been deleted since the station was added.
    /// </summary>
    public static IReadOnlyList<string> ScanTracks(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return [];

        try
        {
            return [.. Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The immediate subfolders of <paramref name="parentFolderPath"/> that themselves contain
    /// at least one playable track directly inside them (one layer deep only - a subfolder's
    /// own subfolders are not examined), ordered by folder name. Used to detect an "artist
    /// folder" of album subfolders when a picked folder has no tracks of its own.
    /// </summary>
    public static IReadOnlyList<string> GetImmediateSubfoldersWithTracks(string? parentFolderPath)
    {
        if (string.IsNullOrWhiteSpace(parentFolderPath) || !Directory.Exists(parentFolderPath))
            return [];

        try
        {
            return [.. Directory.EnumerateDirectories(parentFolderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => ScanTracks(path).Count > 0)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The best guess at a cover-art image file directly inside <paramref name="folderPath"/>,
    /// or <c>null</c> if nothing plausible is there. Tried in order, each step falling through to
    /// the next only if it finds nothing:
    /// <list type="number">
    /// <item>An exact "cover.jpg"-style name (see <see cref="CoverFileBaseNames"/>) wins outright -
    /// that's a deliberate cover file, not a guess.</item>
    /// <item>Otherwise, the largest image whose filename contains one of those same words, e.g.
    /// "cover2.jpg" or "Front-large.png" - real-world rips number or suffix the cover when there's
    /// more than one image, and the biggest is taken as the full-size art rather than a thumbnail
    /// sitting next to it.</item>
    /// <item>Otherwise, if the folder has exactly one image file at all, it's almost certainly the
    /// album's cover sitting next to the tracks even though nothing about its name says so.</item>
    /// </list>
    /// </summary>
    public static string? FindCoverImage(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return null;

        try
        {
            foreach (string baseName in CoverFileBaseNames)
            {
                foreach (string extension in CoverFileExtensionList)
                {
                    string candidate = Path.Combine(folderPath, baseName + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            List<string> images = [.. Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => CoverFileExtensions.Contains(Path.GetExtension(path)))];

            string? bestKeywordMatch = images
                .Where(path => CoverFileBaseNames.Any(baseName =>
                    Path.GetFileNameWithoutExtension(path).Contains(baseName, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(SafeFileLength)
                .FirstOrDefault();
            if (bestKeywordMatch is not null)
                return bestKeywordMatch;

            return images.Count == 1 ? images[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
