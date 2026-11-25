using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Trdo.Helpers;

namespace Trdo.Services;

/// <summary>
/// Keeps the app's own copies of the logo images a user picked from their PC, so a station keeps
/// its picture after the original is moved or deleted. Each copy is referred to by an ordinary
/// <c>file://</c> URL in <see cref="Models.RadioStation.FaviconUrl"/>.
/// <para>
/// Every image gets a folder of its own: <c>LocalCoverReader</c> replaces a missing cover with
/// another image from the same folder, which in a shared one would be some other station's logo.
/// </para>
/// </summary>
public sealed class CustomStationArtStore(string rootFolder)
{
    /// <summary>The largest image file accepted.</summary>
    public const int MaxSourceBytes = 20 * 1024 * 1024;

    private const string FileName = "logo";

    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));

    /// <summary>
    /// The file extension for the image format <paramref name="bytes"/> start like, or null when
    /// they are not an image the app can show.
    /// </summary>
    public static string? ExtensionFor(ReadOnlySpan<byte> bytes) => ImageFormat.DetectMime(bytes) switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ImageFormat.IsIcon(bytes) ? ".ico" : null,
    };

    /// <summary>Writes a new copy of an image and returns its <c>file://</c> URL.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> are not an image.</exception>
    public async Task<string> SaveAsync(byte[] bytes)
    {
        string extension = ExtensionFor(bytes)
            ?? throw new ArgumentException("Not a supported image.", nameof(bytes));

        string folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, FileName + extension);
        await File.WriteAllBytesAsync(path, bytes);
        return new Uri(path).AbsoluteUri;
    }

    /// <summary>Whether <paramref name="url"/> points at a copy this store made.</summary>
    public bool Owns(string? url) => OwnedPath(url) is not null;

    /// <summary>
    /// Deletes the copy behind <paramref name="url"/> unless one of <paramref name="urlsInUse"/>
    /// still points at it. Anything the store does not own, such as the user's own files, is never
    /// touched. Returns true when a file was deleted.
    /// </summary>
    public bool DeleteIfUnused(string? url, IEnumerable<string?> urlsInUse)
    {
        string? path = OwnedPath(url);
        if (path is null)
        {
            return false;
        }

        foreach (string? other in urlsInUse)
        {
            if (string.Equals(OwnedPath(other), path, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Costs only disk space; the file may already be gone with its folder left behind.
            return !File.Exists(path);
        }
    }

    // The full path when the URL is a file exactly one folder below the root, otherwise null.
    private string? OwnedPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
            !uri.IsFile)
        {
            return null;
        }

        string path = Path.GetFullPath(uri.LocalPath);
        string? parent = Path.GetDirectoryName(Path.GetDirectoryName(path));
        return string.Equals(parent, _root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}
