using System;

namespace Trdo.Helpers;

/// <summary>
/// Sniffs an image's format from its leading bytes. Used to label embedded ID3 art with a
/// MIME type and, in the album-art logging, to make a payload that is <i>not</i> an image
/// visible at a glance - a decoder handed garbage just shows nothing, with no error anywhere.
/// </summary>
internal static class ImageFormat
{
    /// <summary>The MIME type the leading bytes match, or null when they match no image format we know.</summary>
    public static string? DetectMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return "image/bmp";
        return null;
    }

    /// <summary>
    /// A one-line description for logs: byte count, the detected format (or "unrecognized"),
    /// and the first few bytes in hex so a mis-sliced payload shows exactly what got in front
    /// of the image.
    /// </summary>
    public static string Describe(ReadOnlySpan<byte> bytes)
    {
        string format = DetectMime(bytes) ?? "unrecognized";
        string head = Convert.ToHexString(bytes[..Math.Min(bytes.Length, 8)]);
        return $"{bytes.Length} bytes, {format}, header {head}";
    }

    /// <summary>
    /// An image URL fit for a log line: a <c>data:</c> URI is mostly base64, so only its kind
    /// and size are reported; anything else is short enough to log as-is.
    /// </summary>
    public static string DescribeUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return url;

        int semicolon = url.IndexOf(';');
        string mime = semicolon > 5 ? url[5..semicolon] : "?";
        return $"embedded {mime}, {url.Length} chars";
    }
}
