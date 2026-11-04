using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Trdo.Helpers;

/// <summary>
/// Holds the bytes of art embedded in a stream or file (ID3 APIC), keyed by a short URL.
/// </summary>
/// <remarks>
/// Embedded art used to travel as a <c>data:</c> URI: a 8.7 MB cover became an 11.6 MB base64
/// string on <see cref="Models.StreamMetadata.AlbumArtUrl"/>, and every reader of that string
/// (each bound Image, the SMTC thumbnail) decoded it back into a fresh 8.7 MB array - all on
/// the large object heap. Here the picture is stored once, identified by a hash of its content,
/// so an HLS stream re-sending the same tag every segment resolves to the same entry, and only
/// a handful of recent pictures are ever retained.
/// </remarks>
internal static class EmbeddedArtStore
{
    public const string UrlPrefix = "trdo-art:";

    /// <summary>Pictures larger than this are ignored; no real cover is this big.</summary>
    public const int MaxImageBytes = 16 * 1024 * 1024;

    internal const int MaxEntries = 4;

    private static readonly object Gate = new();

    // Least recently used first.
    private static readonly List<KeyValuePair<string, byte[]>> Entries = [];

    public static bool IsEmbeddedUrl(string? url) =>
        url is not null && url.StartsWith(UrlPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Stores the image (unless an identical one is already held) and returns the URL that
    /// refers to it, or null when it is empty or larger than <see cref="MaxImageBytes"/>.
    /// </summary>
    public static string? Register(ReadOnlySpan<byte> image)
    {
        if (image.IsEmpty || image.Length > MaxImageBytes)
        {
            return null;
        }

        string url = UrlPrefix + Convert.ToHexString(SHA256.HashData(image));

        lock (Gate)
        {
            if (!Touch(url))
            {
                Entries.Add(new KeyValuePair<string, byte[]>(url, image.ToArray()));
                while (Entries.Count > MaxEntries)
                {
                    Entries.RemoveAt(0);
                }
            }
        }

        return url;
    }

    /// <summary>The stored bytes for <paramref name="url"/>, or null when unknown or since evicted.</summary>
    public static byte[]? TryGet(string url)
    {
        lock (Gate)
        {
            return Touch(url) ? Entries[^1].Value : null;
        }
    }

    // Moves the entry to the most-recently-used end. Caller holds Gate.
    private static bool Touch(string url)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].Key, url, StringComparison.Ordinal))
            {
                KeyValuePair<string, byte[]> entry = Entries[i];
                Entries.RemoveAt(i);
                Entries.Add(entry);
                return true;
            }
        }

        return false;
    }
}
