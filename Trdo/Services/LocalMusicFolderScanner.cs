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
    private static readonly string[] CoverFileExtensions = [".jpg", ".jpeg", ".png", ".webp"];

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
    /// A cover-art image file directly inside <paramref name="folderPath"/>, checked against
    /// the common names album rips and downloads use, or <c>null</c> if none is found.
    /// </summary>
    public static string? FindCoverImage(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return null;

        try
        {
            foreach (string baseName in CoverFileBaseNames)
            {
                foreach (string extension in CoverFileExtensions)
                {
                    string candidate = Path.Combine(folderPath, baseName + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
