using System;
using System.IO;
using System.Threading.Tasks;
using Trdo.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Trdo.Helpers;

/// <summary>
/// Keeps album art from costing memory out of proportion to where it is shown. A 3000x3000
/// cover is only a few hundred pixels on screen, yet decodes to 36 MB.
/// </summary>
internal static class ArtworkResizer
{
    /// <summary>Longest edge, in pixels, an in-app <c>BitmapImage</c> is decoded at.</summary>
    public const int UiDecodeEdge = 768;

    /// <summary>Longest edge, in pixels, of the thumbnail handed to the system media controls.</summary>
    public const int SmtcEdge = 600;

    /// <summary>
    /// Returns <paramref name="bytes"/> unchanged when the image is already within
    /// <paramref name="maxEdge"/> (or its size can't be read cheaply, or it can't be
    /// transcoded); otherwise a re-encoded copy scaled so its longest edge is
    /// <paramref name="maxEdge"/>. The original is never modified.
    /// </summary>
    public static async Task<byte[]> DownscaleAsync(byte[] bytes, int maxEdge)
    {
        if (!ImageFormat.TryGetDimensions(bytes, out int width, out int height) ||
            Math.Max(width, height) <= maxEdge)
        {
            return bytes;
        }

        try
        {
            using IRandomAccessStream source = new MemoryStream(bytes).AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source);

            double scale = (double)maxEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            uint scaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
            uint scaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));

            using InMemoryRandomAccessStream target = new();
            BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(target, decoder);
            encoder.BitmapTransform.ScaledWidth = scaledWidth;
            encoder.BitmapTransform.ScaledHeight = scaledHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            target.Seek(0);
            using MemoryStream result = new(checked((int)target.Size));
            using Stream reader = target.AsStreamForRead();
            await reader.CopyToAsync(result);

            byte[] resized = result.ToArray();
            if (resized.Length == 0 || resized.Length >= bytes.Length)
            {
                return bytes;
            }

            LogService.Info("AlbumArt", $"Downscaled art {width}x{height} ({bytes.Length} bytes) to {scaledWidth}x{scaledHeight} ({resized.Length} bytes)");
            return resized;
        }
        catch (Exception ex)
        {
            LogService.Warn("AlbumArt", $"Could not downscale {width}x{height} art, using it as-is: {ex.GetType().Name}: {ex.Message}");
            return bytes;
        }
    }
}
