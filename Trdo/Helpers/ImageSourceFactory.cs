using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading.Tasks;
using Trdo.Services;
using Windows.Storage.Streams;

namespace Trdo.Helpers;

/// <summary>
/// Turns an image URL into an <see cref="ImageSource"/> the XAML <c>Image</c> control can show.
/// Must be called on the UI thread - the returned <see cref="BitmapImage"/> is a DependencyObject.
/// </summary>
/// <remarks>
/// Only http(s) URIs are handed straight to <see cref="BitmapImage"/>. The two schemes local
/// albums rely on are read into memory here and fed in via <see cref="BitmapImage.SetSourceAsync"/>
/// instead, because XAML silently fails to load either from a URI - no exception, no
/// ImageFailed from a source set in code, just a blank image:
/// <list type="bullet">
/// <item>A track's embedded ID3 art is a <c>data:</c> URI (see <see cref="Services.Metadata.Id3TagParser"/>),
/// which <see cref="BitmapImage"/> does not support at all.</item>
/// <item>A local album's own cover on disk is a <c>file://</c> URI (see
/// <see cref="LocalMusicFolderScanner"/>). That does work for a plain ASCII path, but a folder
/// with a non-ASCII character in its name ("Ow ∞") is percent-encoded in the URI and XAML's
/// file loader mangles the decoded path, so the cover never loads even though the SMTC
/// thumbnail - which reads the same URI straight off disk - shows it fine.</item>
/// </list>
/// </remarks>
internal static class ImageSourceFactory
{
    private const string LogComponent = "AlbumArt";

    public static ImageSource? Create(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Checked before Uri parsing: a base64 payload is not something to run through Uri.
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? bytes = DecodeDataUri(url);
            if (bytes is null)
            {
                return null;
            }

            LogService.Info(LogComponent, $"UI image from embedded art: {ImageFormat.Describe(bytes)}");
            return LoadFromBytes(() => Task.FromResult(bytes), "embedded art");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (uri.IsFile)
        {
            string path = uri.LocalPath;
            LogService.Info(LogComponent, $"UI image from file: {path}");
            return LoadFromBytes(() => File.ReadAllBytesAsync(path), path);
        }

        // Not logged: every http favicon in the station list comes through here on each
        // render, and XAML reports those loads via ImageFailed where it matters anyway.
        return new BitmapImage(uri);
    }

    private static byte[]? DecodeDataUri(string url)
    {
        int commaIndex = url.IndexOf(',');
        if (commaIndex < 0)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(url[(commaIndex + 1)..]);
        }
        catch (FormatException ex)
        {
            LogService.Warn(LogComponent, $"Embedded art is not valid base64: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the bitmap immediately and fills it in once the bytes arrive - the callers are
    /// synchronous bound properties, and an <c>Image</c> whose source is populated a moment
    /// later simply renders when it does. The read itself is awaited off the UI thread; the
    /// continuation lands back on it (DispatcherQueue synchronization context) for SetSourceAsync.
    /// </summary>
    private static BitmapImage LoadFromBytes(Func<Task<byte[]>> readBytes, string description)
    {
        BitmapImage image = new();
        _ = LoadAsync(image, readBytes, description);
        return image;
    }

    private static async Task LoadAsync(BitmapImage image, Func<Task<byte[]>> readBytes, string description)
    {
        try
        {
            byte[] bytes = await readBytes();
            if (ImageFormat.DetectMime(bytes) is null)
            {
                LogService.Warn(LogComponent, $"UI image '{description}' is not a recognized image ({ImageFormat.Describe(bytes)}); the decoder will most likely show nothing");
            }

            // Deliberately not disposed here, matching RadioPlayerService's SMTC thumbnail: the
            // decoder can still be reading the stream after SetSourceAsync hands control back,
            // and closing it under the decoder yields a blank image. It's small and short-lived
            // enough to leave to the GC.
            IRandomAccessStream stream = new MemoryStream(bytes).AsRandomAccessStream();
            await image.SetSourceAsync(stream);
            LogService.Info(LogComponent, $"UI image '{description}' decoded: {image.PixelWidth}x{image.PixelHeight}");
        }
        catch (Exception ex)
        {
            // A missing/unreadable file or bytes the decoder rejects (COMException). Nothing to
            // surface: the image simply stays empty, as it would for a broken http URL.
            LogService.Warn(LogComponent, $"UI image '{description}' failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
