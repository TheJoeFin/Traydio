using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Trdo.Services;
using Windows.Storage.Streams;

namespace Trdo.Helpers;

/// <summary>
/// Turns an image URL into an <see cref="ImageSource"/> the XAML <c>Image</c> control can show.
/// Must be called on the UI thread - the returned <see cref="BitmapImage"/> is a DependencyObject.
/// </summary>
/// <remarks>
/// Nothing is handed to <see cref="BitmapImage"/> as a URI; every source is read into memory here
/// and fed in via <see cref="BitmapImage.SetSourceAsync"/>, because XAML fails to load each of
/// them from a URI in a way this app can neither log nor work around - no exception, and at
/// most an ImageFailed on the bound control that nothing handles - leaving just a blank image:
/// <list type="bullet">
/// <item>A track's embedded ID3 art is a <c>data:</c> URI (see <see cref="Services.Metadata.Id3TagParser"/>),
/// which <see cref="BitmapImage"/> does not support at all.</item>
/// <item>A local album's own cover on disk is a <c>file://</c> URI (see
/// <see cref="LocalMusicFolderScanner"/>). That does work for a plain ASCII path, but a folder
/// with a non-ASCII character in its name ("Ow ∞") is percent-encoded in the URI and XAML's
/// file loader mangles the decoded path, so the cover never loads even though the SMTC
/// thumbnail - which reads the same URI straight off disk - shows it fine.</item>
/// <item>A station's http(s) favicon whose URL redirects - typically a plain-http URL from the
/// station directory that the site now 301s to https. XAML's loader does not follow it, where
/// HttpClient (and so the SMTC thumbnail) does.</item>
/// </list>
/// </remarks>
internal static class ImageSourceFactory
{
    private const string LogComponent = "AlbumArt";

    /// <summary>
    /// How many times a local album cover gets re-read and re-decoded after a failure,
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

    /// <summary>
    /// How many times an http(s) favicon download gets re-tried after a failure, with the same
    /// backoff as a file. The concern is the same as <see cref="MaxFileRetries"/>: a station-list
    /// row gets exactly one <see cref="Create"/> call, so a favicon that fails once because the
    /// network was still coming up at launch would otherwise stay blank for the whole session.
    /// </summary>
    private const int MaxHttpRetries = 2;

    /// <summary>
    /// Follows redirects by default (AllowAutoRedirect), including the http -> https upgrade
    /// that XAML's image loader refuses; see <see cref="Create"/>.
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Downloaded favicon bytes by absolute URL, for the lifetime of the process. Every station
    /// row, the now-playing card, the mini player and the popup each ask for the same URL, and
    /// the station list asks again on every render; BitmapImage(uri) used to lean on XAML's
    /// internal image cache for that, and this stands in for it. A failed download is evicted
    /// (see <see cref="DownloadAsync"/>) so a later request tries again rather than replaying
    /// the failure forever.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> DownloadCache = new(StringComparer.Ordinal);

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

            return LoadFromBytes(() => Task.FromResult<byte[]?>(bytes), "embedded art");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (uri.IsFile)
        {
            // LocalCoverReader handles a cover that has been renamed or removed since the station
            // was scanned; the retry budget here is for one that exists but can't be read yet.
            string path = uri.LocalPath;
            return LoadFromBytes(() => LocalCoverReader.ReadAsync(path), path, MaxFileRetries);
        }

        // Downloaded here rather than handed to BitmapImage as a URI: XAML's own loader does not
        // follow an http -> https redirect, and a station's favicon URL from the directory is
        // frequently the plain-http form that its site now 301s (WYMS, WTMJ). HttpClient follows
        // the redirect, which is why the SMTC thumbnail - which always downloaded - showed the
        // logo while every in-app Image stayed blank. Going through bytes also means a failure
        // is logged instead of silently swallowed, since none of the bound Image controls
        // handle ImageFailed.
        return LoadFromBytes(() => DownloadAsync(uri), uri.AbsoluteUri, MaxHttpRetries);
    }

    /// <summary>
    /// Fetches the image at <paramref name="uri"/>, sharing one in-flight download and one
    /// cached result between every caller asking for the same URL. Only ever called on the UI
    /// thread (see the class remarks), so the get-or-add needs no further coordination.
    /// </summary>
    private static Task<byte[]?> DownloadAsync(Uri uri)
    {
        string key = uri.AbsoluteUri;
        return DownloadCache.GetOrAdd(key, static k => DownloadCoreAsync(k));
    }

    private static async Task<byte[]?> DownloadCoreAsync(string url)
    {
        try
        {
            return await Http.GetByteArrayAsync(url);
        }
        catch
        {
            // Let the caller's retry (and any later caller) start a fresh download rather than
            // awaiting this same faulted task. The exception itself is logged by LoadAsync.
            DownloadCache.TryRemove(url, out _);
            throw;
        }
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
    /// <param name="readBytes">
    /// Produces the image bytes, or null to stop without an image when the reader has already
    /// determined (and logged) that no retry can help.
    /// </param>
    /// <param name="maxRetries">
    /// How many times to re-run <paramref name="readBytes"/> and the decode after a failure,
    /// waiting <see cref="FileRetryDelay"/> between attempts. Zero for sources that can't
    /// change out from under a failed read (an already-decoded <c>data:</c> payload); nonzero
    /// for a file on disk that may still be mid-write.
    /// </param>
    private static BitmapImage LoadFromBytes(Func<Task<byte[]?>> readBytes, string description, int maxRetries = 0)
    {
        BitmapImage image = new();
        _ = LoadAsync(image, readBytes, description, maxRetries);
        return image;
    }

    private static async Task LoadAsync(BitmapImage image, Func<Task<byte[]?>> readBytes, string description, int maxRetries)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                byte[]? bytes = await readBytes();
                if (bytes is null)
                {
                    return;
                }

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
                // A locked/unreadable file, a partially-written one the decoder rejects
                // (COMException), or bytes that are just never going to decode. (A file that is
                // outright missing never gets here: LocalCoverReader handles that itself, moving
                // to a replacement or returning null.) Below the retry budget, readBytes()
                // re-checks after a short wait; a cover renamed in the meantime is picked up
                // then, since the re-read misses the old path and LocalCoverReader re-scans.
                // Re-using the same BitmapImage means a later successful SetSourceAsync still
                // updates whatever the bound Image control is already showing. Past the budget,
                // nothing to surface: the image simply stays empty.
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
