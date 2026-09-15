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

    /// <summary>
    /// How many times a local album cover gets re-scanned-for and re-decoded after a failure,
    /// with exponential backoff (see <see cref="FileRetryDelay"/>) between attempts. A scanner or
    /// downloader can still be writing the file, or Explorer/antivirus/OneDrive can be holding a
    /// transient lock on it, right as this reads it.
    /// <para>
    /// This has to cover more than a quick hiccup: a station-list row binds its image to
    /// <c>FaviconUrl</c>, which for a local album is set once at scan time and never raises
    /// another change notification - so the row gets exactly one <see cref="Create"/> call for
    /// the lifetime of the app, unlike the now-playing surfaces, which recompute their album art
    /// source (and so get a fresh set of attempts) on every metadata refresh. If this budget is
    /// too short, a row whose cover file was still settling when the app started never gets
    /// another chance.
    /// </para>
    /// </summary>
    private const int MaxFileRetries = 6;

    private static TimeSpan FileRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, attempt), 5000));

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

            return LoadFromBytes(() => Task.FromResult(bytes), "embedded art");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (uri.IsFile)
        {
            string path = uri.LocalPath;
            return LoadFromBytes(CreateLocalCoverReader(path), path, MaxFileRetries);
        }

        // Not logged: every http favicon in the station list comes through here on each
        // render, and XAML reports those loads via ImageFailed where it matters anyway.
        return new BitmapImage(uri);
    }

    /// <summary>
    /// Reads the bytes for a local cover image, re-scanning the containing folder for a cover
    /// file on every call after the first instead of hammering the same path. The file this URI
    /// pointed to was resolved by <see cref="LocalMusicFolderScanner.FindCoverImage"/> at scan
    /// time and may since have been renamed, replaced with a different format, or removed
    /// outright - a bare retry of the original path would just keep failing the same way, where
    /// a fresh scan can pick up whatever cover file is actually there now.
    /// </summary>
    private static Func<Task<byte[]>> CreateLocalCoverReader(string path)
    {
        string? folderPath = Path.GetDirectoryName(path);
        bool firstAttempt = true;

        return () =>
        {
            string candidatePath = path;
            if (!firstAttempt)
            {
                candidatePath = LocalMusicFolderScanner.FindCoverImage(folderPath) ?? path;
            }

            firstAttempt = false;
            return File.ReadAllBytesAsync(candidatePath);
        };
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
    /// <param name="maxRetries">
    /// How many times to re-run <paramref name="readBytes"/> and the decode after a failure,
    /// waiting <see cref="FileRetryDelay"/> between attempts. Zero for sources that can't
    /// change out from under a failed read (an already-decoded <c>data:</c> payload); nonzero
    /// for a file on disk that may still be mid-write.
    /// </param>
    private static BitmapImage LoadFromBytes(Func<Task<byte[]>> readBytes, string description, int maxRetries = 0)
    {
        BitmapImage image = new();
        _ = LoadAsync(image, readBytes, description, maxRetries);
        return image;
    }

    private static async Task LoadAsync(BitmapImage image, Func<Task<byte[]>> readBytes, string description, int maxRetries)
    {
        for (int attempt = 0; ; attempt++)
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
                return;
            }
            catch (Exception ex)
            {
                // A missing/unreadable file, a partially-written one the decoder rejects
                // (COMException), or bytes that are just never going to decode. Below the retry
                // budget, readBytes() re-checks after a short wait - for a local cover that means
                // re-scanning the folder rather than hammering the same failed path, since the
                // file may have been renamed, replaced, or removed since it was first resolved.
                // Re-using the same BitmapImage means a later successful SetSourceAsync still
                // updates whatever the bound Image control is already showing. Past the budget,
                // nothing to surface: the image simply stays empty, as it would for a broken
                // http URL.
                if (attempt >= maxRetries)
                {
                    LogService.Warn(LogComponent, $"UI image '{description}' failed to load: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                TimeSpan delay = FileRetryDelay(attempt);
                LogService.Warn(LogComponent, $"UI image '{description}' failed to load (attempt {attempt + 1}/{maxRetries + 1}): {ex.GetType().Name}: {ex.Message}; retrying in {delay.TotalMilliseconds:0}ms");
                await Task.Delay(delay);
            }
        }
    }
}
