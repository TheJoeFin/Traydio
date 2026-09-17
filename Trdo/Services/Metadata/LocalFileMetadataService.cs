using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services.Metadata;

/// <summary>
/// Reads the ICY-equivalent "now playing" metadata for a local music track, used only when
/// <see cref="AudioSourceKind.Files"/> is active. Unlike ICY metadata this isn't polled - a
/// local file's tags don't change mid-playback - so it's read once per track change.
/// </summary>
internal static class LocalFileMetadataService
{
    // Comfortably larger than any front-loaded ID3v2 tag on a typical MP3, without reading
    // the whole file for a large FLAC/WAV that has no ID3v2 tag to find anyway.
    private const int TagReadLength = 200 * 1024;

    public static async Task<StreamMetadata> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        StreamMetadata metadata = await TryReadId3Async(filePath, cancellationToken);
        if (metadata.HasMetadata)
            return metadata;

        return BuildFallbackFromFileName(filePath);
    }

    private static async Task<StreamMetadata> TryReadId3Async(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buffer = new byte[Math.Min(TagReadLength, stream.Length)];
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);

            return Id3TagParser.Parse(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StreamMetadata.Empty;
        }
    }

    /// <summary>
    /// Falls back to the filename when no ID3 tag is present or usable - e.g. FLAC/OGG, which
    /// use Vorbis comments rather than ID3 and so aren't covered by <see cref="Id3TagParser"/>.
    /// A common ripped-filename convention, "Artist - Title", is split apart if present.
    /// </summary>
    private static StreamMetadata BuildFallbackFromFileName(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        string[] parts = name.Split(" - ", 2, StringSplitOptions.TrimEntries);

        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
        {
            return new StreamMetadata { Artist = parts[0], Title = parts[1], StreamTitle = name };
        }

        return new StreamMetadata { Title = name, StreamTitle = name };
    }
}
