using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Trdo.Services;

namespace Trdo.Helpers;

/// <summary>
/// Downloads an image with a hard size cap, so a hostile or misconfigured URL can't pull an
/// arbitrarily large body into memory the way <c>HttpClient.GetByteArrayAsync</c> would.
/// </summary>
internal static class ImageDownloader
{
    public const int MaxImageBytes = 12 * 1024 * 1024;

    /// <summary>
    /// Returns the response body, or null when it is larger than <paramref name="maxBytes"/>
    /// (nothing is retained in that case). Network and HTTP-status failures throw, as with
    /// <c>GetByteArrayAsync</c>.
    /// </summary>
    public static async Task<byte[]?> GetBytesAsync(HttpClient client, string url, int maxBytes = MaxImageBytes)
    {
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? declared = response.Content.Headers.ContentLength;
        if (declared > maxBytes)
        {
            LogService.Warn("AlbumArt", $"Image at {url} is {declared} bytes, over the {maxBytes} byte limit; skipping it");
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync();

        // Sized from Content-Length when present so the common case is a single allocation
        // with no trailing copy.
        byte[] buffer = new byte[declared is > 0 ? (int)declared.Value : 32 * 1024];
        byte[] probe = new byte[1];
        int count = 0;
        while (true)
        {
            if (count == buffer.Length)
            {
                // Full: either the stream ends exactly here, or there is more to make room for.
                if (await stream.ReadAsync(probe) == 0)
                {
                    break;
                }

                if (buffer.Length >= maxBytes)
                {
                    LogService.Warn("AlbumArt", $"Image at {url} exceeds the {maxBytes} byte limit; skipping it");
                    return null;
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, maxBytes));
                buffer[count++] = probe[0];
            }

            int read = await stream.ReadAsync(buffer.AsMemory(count));
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count != buffer.Length)
        {
            Array.Resize(ref buffer, count);
        }

        return buffer;
    }
}
