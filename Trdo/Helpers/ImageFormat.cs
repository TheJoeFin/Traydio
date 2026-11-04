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
    /// Reads the pixel size straight from the header of a JPEG, PNG, GIF or BMP, without
    /// decoding anything. Lets callers decide whether an image is worth downscaling before
    /// paying for a decode - a 3000x3000 cover is 36 MB once decoded. Returns false for any
    /// other format (including WebP) or a truncated header.
    /// </summary>
    public static bool TryGetDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        switch (DetectMime(bytes))
        {
            case "image/png":
                if (bytes.Length < 24)
                    return false;
                width = ReadInt32BigEndian(bytes[16..]);
                height = ReadInt32BigEndian(bytes[20..]);
                break;
            case "image/gif":
                if (bytes.Length < 10)
                    return false;
                width = bytes[6] | (bytes[7] << 8);
                height = bytes[8] | (bytes[9] << 8);
                break;
            case "image/bmp":
                if (bytes.Length < 26)
                    return false;
                width = Math.Abs(bytes[18] | (bytes[19] << 8) | (bytes[20] << 16) | (bytes[21] << 24));
                height = Math.Abs(bytes[22] | (bytes[23] << 8) | (bytes[24] << 16) | (bytes[25] << 24));
                break;
            case "image/jpeg":
                if (!TryGetJpegDimensions(bytes, out width, out height))
                    return false;
                break;
            default:
                return false;
        }

        return width > 0 && height > 0;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> data) =>
        (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];

    private static bool TryGetJpegDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        int i = 2;
        while (i + 3 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = bytes[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }

            // Markers without a length: TEM, RSTn, SOI, EOI.
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
            {
                i += 2;
                continue;
            }

            // SOFn carries the frame size; DHT (C4), JPG (C8) and DAC (CC) share the range but don't.
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (i + 8 >= bytes.Length)
                    return false;
                height = (bytes[i + 5] << 8) | bytes[i + 6];
                width = (bytes[i + 7] << 8) | bytes[i + 8];
                return true;
            }

            int segmentLength = (bytes[i + 2] << 8) | bytes[i + 3];
            if (segmentLength < 2)
                return false;
            i += 2 + segmentLength;
        }

        return false;
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
