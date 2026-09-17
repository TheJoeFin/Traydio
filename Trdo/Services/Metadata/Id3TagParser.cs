using System;
using System.Text;
using Trdo.Helpers;
using Trdo.Models;

namespace Trdo.Services.Metadata;

/// <summary>
/// Lightweight ID3v2.3/v2.4 parser for HLS timed metadata payloads.
/// </summary>
internal static class Id3TagParser
{
    private const string LogComponent = "AlbumArt";
    private static string? _lastLoggedApicSummary;
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    public static StreamMetadata Parse(byte[] id3Data)
    {
        StreamMetadata metadata = new();

        if (id3Data.Length < 10)
        {
            return metadata;
        }

        string header = Latin1.GetString(id3Data, 0, 3);
        if (!header.Equals("ID3", StringComparison.Ordinal))
        {
            return metadata;
        }

        bool isV4 = id3Data[3] == 4;
        int tagSize = ReadSyncSafeInt(id3Data.AsSpan(6, 4));
        int offset = 10;
        int end = Math.Min(id3Data.Length, 10 + tagSize);

        while (offset + 10 <= end)
        {
            string frameId = Latin1.GetString(id3Data, offset, 4);
            if (string.IsNullOrWhiteSpace(frameId) || frameId == "\0\0\0\0")
            {
                break;
            }

            int frameSize = isV4
                ? ReadSyncSafeInt(id3Data.AsSpan(offset + 4, 4))
                : (id3Data[offset + 4] << 24) |
                  (id3Data[offset + 5] << 16) |
                  (id3Data[offset + 6] << 8) |
                  id3Data[offset + 7];

            int frameHeaderSize = isV4 ? 10 : 10;
            int frameDataOffset = offset + frameHeaderSize;
            if (frameSize <= 0 || frameDataOffset + frameSize > id3Data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> frameData = id3Data.AsSpan(frameDataOffset, frameSize);
            ApplyFrame(frameId, frameData, metadata);
            offset = frameDataOffset + frameSize;
        }

        if (metadata.HasMetadata && string.IsNullOrWhiteSpace(metadata.StreamTitle))
        {
            metadata.StreamTitle = metadata.DisplayText;
        }

        return metadata;
    }

    private static void ApplyFrame(string frameId, ReadOnlySpan<byte> frameData, StreamMetadata metadata)
    {
        switch (frameId)
        {
            case "TIT2":
                metadata.Title = ReadTextFrame(frameData);
                break;
            case "TPE1":
                metadata.Artist = ReadTextFrame(frameData);
                break;
            case "TALB":
                if (string.IsNullOrWhiteSpace(metadata.StreamTitle))
                {
                    metadata.StreamTitle = ReadTextFrame(frameData);
                }

                break;
            case "WXXX":
            case "WCOM":
            case "WOAF":
                string? url = ReadUrlFrame(frameData);
                if (!string.IsNullOrWhiteSpace(url) && LooksLikeImageUrl(url))
                {
                    metadata.AlbumArtUrl = url;
                }

                break;
            case "TXXX":
                ParseTxxxFrame(frameData, metadata);
                break;
            case "APIC":
                ParseApicFrame(frameData, metadata);
                break;
        }
    }

    private static void ParseTxxxFrame(ReadOnlySpan<byte> frameData, StreamMetadata metadata)
    {
        if (frameData.Length < 2)
        {
            return;
        }

        // The description is in the frame's declared text encoding, same as APIC's.
        int index = 1;
        string description = ReadNullTerminatedString(frameData, frameData[0], ref index);
        string value = ReadRemainingUtf8OrLatin1(frameData, index);

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (description.Contains("artist", StringComparison.OrdinalIgnoreCase))
        {
            metadata.Artist = value;
        }
        else if (description.Contains("title", StringComparison.OrdinalIgnoreCase))
        {
            metadata.Title = value;
        }
        else if (description.Contains("art", StringComparison.OrdinalIgnoreCase) && LooksLikeImageUrl(value))
        {
            metadata.AlbumArtUrl = value;
        }
    }

    /// <summary>
    /// APIC layout: text encoding (1 byte), MIME type (null-terminated Latin-1), picture type
    /// (1 byte), description (in the frame's text encoding, so a UTF-16 one ends in a
    /// double-null), then the raw image bytes. Every field before the image has to be walked
    /// exactly: a taggers' UTF-16 "cover" description read as single-null Latin-1 stops at the
    /// first zero byte inside it and leaves half the description glued to the front of the
    /// JPEG, which then decodes as nothing at all - in the app and in SMTC alike.
    /// </summary>
    private static void ParseApicFrame(ReadOnlySpan<byte> frameData, StreamMetadata metadata)
    {
        if (frameData.Length < 5)
        {
            return;
        }

        int index = 0;
        byte encoding = frameData[index++];
        string declaredMime = ReadNullTerminatedLatin1(frameData, ref index);

        if (index >= frameData.Length)
        {
            return;
        }

        byte pictureType = frameData[index++];
        string description = ReadNullTerminatedString(frameData, encoding, ref index);

        if (index >= frameData.Length)
        {
            LogService.Warn(LogComponent, $"APIC frame (mime '{declaredMime}', type {pictureType}, description '{description}') has no image bytes after its header");
            return;
        }

        ReadOnlySpan<byte> imageSpan = frameData[index..];
        string? mime = ImageFormat.DetectMime(imageSpan);
        if (mime is null)
        {
            // Better no art (the station favicon then stands in) than a payload every decoder
            // silently rejects, which would block that fallback.
            LogService.Warn(LogComponent, $"APIC frame (encoding {encoding}, mime '{declaredMime}', type {pictureType}, description '{description}') skipped: {ImageFormat.Describe(imageSpan)}");
            return;
        }

        // Logged once per distinct picture, not per parse: an HLS stream re-sends the same
        // tag (art included) with every segment.
        string summary = $"encoding {encoding}, mime '{declaredMime}', type {pictureType}, description '{description}', {ImageFormat.Describe(imageSpan)}";
        if (summary != _lastLoggedApicSummary)
        {
            _lastLoggedApicSummary = summary;
            LogService.Info(LogComponent, $"Embedded APIC art: {summary}");
        }

        metadata.AlbumArtUrl = $"data:{mime};base64,{Convert.ToBase64String(imageSpan)}";
    }

    private static string ReadTextFrame(ReadOnlySpan<byte> frameData)
    {
        if (frameData.IsEmpty)
        {
            return string.Empty;
        }

        int index = 0;
        return ReadEncodedString(frameData, ref index);
    }

    private static string? ReadUrlFrame(ReadOnlySpan<byte> frameData)
    {
        if (frameData.Length < 2)
        {
            return null;
        }

        int index = 1;
        return ReadRemainingUtf8OrLatin1(frameData, index).Trim();
    }

    private static string ReadEncodedString(ReadOnlySpan<byte> data, ref int index)
    {
        if (index >= data.Length)
        {
            return string.Empty;
        }

        byte encoding = data[index++];
        ReadOnlySpan<byte> textSpan = data[index..];

        string text = encoding switch
        {
            0 => Latin1.GetString(textSpan),
            1 => Encoding.Unicode.GetString(textSpan),
            2 => Encoding.BigEndianUnicode.GetString(textSpan),
            3 => Encoding.UTF8.GetString(textSpan),
            _ => Latin1.GetString(textSpan)
        };

        // Encoding.Unicode keeps the BOM as U+FEFF, which then rode along as an invisible
        // first character of every UTF-16 title and artist.
        return text.TrimEnd('\0').Trim('\uFEFF').Trim();
    }

    private static string ReadNullTerminatedLatin1(ReadOnlySpan<byte> data, ref int index)
    {
        int start = index;
        while (index < data.Length && data[index] != 0)
        {
            index++;
        }

        string value = Latin1.GetString(data.Slice(start, index - start));
        if (index < data.Length)
        {
            index++;
        }

        return value;
    }

    /// <summary>
    /// Reads a string terminated the way its ID3 text encoding demands: a single zero byte for
    /// Latin-1/UTF-8, a zero <i>pair</i> on a 2-byte boundary for either UTF-16 flavour.
    /// Leaves <paramref name="index"/> just past the terminator.
    /// </summary>
    private static string ReadNullTerminatedString(ReadOnlySpan<byte> data, byte encoding, ref int index)
    {
        bool wide = encoding is 1 or 2;
        int start = index;
        if (wide)
        {
            while (index + 1 < data.Length && (data[index] != 0 || data[index + 1] != 0))
            {
                index += 2;
            }
        }
        else
        {
            while (index < data.Length && data[index] != 0)
            {
                index++;
            }
        }

        ReadOnlySpan<byte> textSpan = data[start..Math.Min(index, data.Length)];
        string text = encoding switch
        {
            1 => Encoding.Unicode.GetString(textSpan),
            2 => Encoding.BigEndianUnicode.GetString(textSpan),
            3 => Encoding.UTF8.GetString(textSpan),
            _ => Latin1.GetString(textSpan)
        };

        index = Math.Min(index + (wide ? 2 : 1), data.Length);
        return text.Trim('\uFEFF').Trim();
    }

    private static string ReadRemainingUtf8OrLatin1(ReadOnlySpan<byte> data, int index)
    {
        if (index >= data.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> span = data[index..];
        string utf8 = Encoding.UTF8.GetString(span).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(utf8) ? Latin1.GetString(span).TrimEnd('\0') : utf8;
    }

    private static int ReadSyncSafeInt(ReadOnlySpan<byte> data)
    {
        return (data[0] << 21) | (data[1] << 14) | (data[2] << 7) | data[3];
    }

    private static bool LooksLikeImageUrl(string url)
    {
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string lower = url.ToLowerInvariant();
        return lower.Contains(".jpg") ||
               lower.Contains(".jpeg") ||
               lower.Contains(".png") ||
               lower.Contains(".webp") ||
               lower.Contains(".gif");
    }
}
