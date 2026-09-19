using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services.Metadata;

/// <summary>
/// Reads the VORBIS_COMMENT metadata block from a FLAC file's header. FLAC tags aren't ID3 -
/// they're a sequence of length-prefixed metadata blocks starting right after the "fLaC" magic,
/// one of which (type 4) holds the Vorbis comments (TITLE, ARTIST, etc.) as "KEY=VALUE" pairs.
/// </summary>
internal static class FlacMetadataReader
{
    // Comments are plain text; anything claiming to be this large is a corrupt length rather
    // than a real tag, so bail out instead of trying to allocate it.
    private const int MaxCommentBlockSize = 5 * 1024 * 1024;
    private const int VorbisCommentBlockType = 4;

    public static async Task<StreamMetadata> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] magic = new byte[4];
        if (!await ReadExactAsync(stream, magic, cancellationToken) || !IsFlacMagic(magic))
            return StreamMetadata.Empty;

        byte[] blockHeader = new byte[4];
        while (await ReadExactAsync(stream, blockHeader, cancellationToken))
        {
            bool isLastBlock = (blockHeader[0] & 0x80) != 0;
            int blockType = blockHeader[0] & 0x7F;
            int blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

            if (blockType == VorbisCommentBlockType && blockLength > 0 && blockLength <= MaxCommentBlockSize)
            {
                byte[] data = new byte[blockLength];
                return await ReadExactAsync(stream, data, cancellationToken)
                    ? ParseVorbisComment(data)
                    : StreamMetadata.Empty;
            }

            if (isLastBlock)
                break;

            // Not the block we want - e.g. a multi-megabyte embedded PICTURE - so skip past its
            // data via Seek rather than reading it into memory.
            stream.Seek(blockLength, SeekOrigin.Current);
        }

        return StreamMetadata.Empty;
    }

    private static bool IsFlacMagic(byte[] bytes) =>
        bytes[0] == 'f' && bytes[1] == 'L' && bytes[2] == 'a' && bytes[3] == 'C';

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
                return false;

            totalRead += read;
        }

        return true;
    }

    /// <summary>
    /// Vorbis comment layout (all integers little-endian, unlike ID3's big-endian sizes):
    /// vendor length + vendor string, comment count, then that many length-prefixed
    /// "KEY=VALUE" UTF-8 strings.
    /// </summary>
    private static StreamMetadata ParseVorbisComment(byte[] data)
    {
        StreamMetadata metadata = new();
        int offset = 0;

        if (!TryReadUInt32LE(data, ref offset, out uint vendorLength))
            return metadata;

        long afterVendor = (long)offset + vendorLength;
        if (afterVendor > data.Length)
            return metadata;

        offset = (int)afterVendor;
        if (!TryReadUInt32LE(data, ref offset, out uint commentCount))
            return metadata;

        for (uint i = 0; i < commentCount; i++)
        {
            if (!TryReadUInt32LE(data, ref offset, out uint commentLength))
                break;

            long commentEnd = (long)offset + commentLength;
            if (commentEnd > data.Length)
                break;

            string comment = Encoding.UTF8.GetString(data, offset, (int)commentLength);
            offset = (int)commentEnd;

            int separatorIndex = comment.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            string key = comment[..separatorIndex];
            string value = comment[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (key.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                metadata.Title = value;
            else if (key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase))
                metadata.Artist = value;
        }

        if (metadata.HasMetadata && string.IsNullOrWhiteSpace(metadata.StreamTitle))
            metadata.StreamTitle = metadata.DisplayText;

        return metadata;
    }

    private static bool TryReadUInt32LE(byte[] data, ref int offset, out uint value)
    {
        if (offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        offset += 4;
        return true;
    }
}
