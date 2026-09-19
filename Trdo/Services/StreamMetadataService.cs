using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// Service for extracting metadata from internet radio streams using the ICY (Icecast/Shoutcast) protocol.
/// </summary>
public sealed partial class StreamMetadataService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly object _pollingLock = new();
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private string? _currentStreamUrl;
    private StreamMetadata _currentMetadata = StreamMetadata.Empty;
    private bool _isDisposed;

    /// <summary>
    /// Polling interval for metadata updates.
    /// Using a longer interval (30s) to reduce network load and minimize potential interference with the main stream.
    /// </summary>
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Event raised when stream metadata changes.
    /// </summary>
    public event EventHandler<StreamMetadata>? MetadataChanged;

    /// <summary>
    /// Gets the current stream metadata.
    /// </summary>
    public StreamMetadata CurrentMetadata => _currentMetadata;

    public StreamMetadataService()
    {
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            // Enable connection pooling and reuse to reduce overhead
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            // Shorter timeout for metadata requests since we only read a small amount of data
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        _httpClient = new HttpClient(handler)
        {
            // Reduced timeout since we abort after reading metadata
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Request ICY metadata by adding the required header
        _httpClient.DefaultRequestHeaders.Add("Icy-MetaData", "1");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Trdo/1.0");
        _httpClient.DefaultRequestHeaders.Connection.Add("keep-alive");
    }

    /// <summary>
    /// Starts polling for metadata from the specified stream URL.
    /// </summary>
    public void StartPolling(string streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            Debug.WriteLine("[StreamMetadataService] Cannot start polling with empty URL");
            return;
        }

        lock (_pollingLock)
        {
            // Stop any existing polling without clearing metadata we may still be displaying.
            StopPollingCore(clearMetadata: false);

            _currentStreamUrl = streamUrl;
            _pollingCts = new CancellationTokenSource();

            Debug.WriteLine($"[StreamMetadataService] Starting metadata polling for: {streamUrl}");

            CancellationToken token = _pollingCts.Token;
            _pollingTask = Task.Run(async () =>
            {
                try
                {
                    // Fetch initial metadata immediately
                    await FetchMetadataAsync(token);

                    // Then poll periodically
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(_pollingInterval, token);
                            await FetchMetadataAsync(token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[StreamMetadataService] Polling error: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Task was cancelled, this is expected during disposal
                    Debug.WriteLine("[StreamMetadataService] Polling task cancelled");
                }
                catch (Exception ex)
                {
                    // Log any unhandled exceptions to prevent unobserved task exceptions
                    Debug.WriteLine($"[StreamMetadataService] Unhandled polling error: {ex.Message}");
                }
            });
        }
    }

    /// <summary>
    /// Stops polling for metadata.
    /// </summary>
    public void StopPolling(bool clearMetadata = true)
    {
        lock (_pollingLock)
        {
            StopPollingCore(clearMetadata);
        }
    }

    /// <summary>
    /// Core stop logic - must be called under lock.
    /// </summary>
    private void StopPollingCore(bool clearMetadata = true)
    {
        Debug.WriteLine("[StreamMetadataService] Stopping metadata polling");
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
        _pollingTask = null;
        _currentStreamUrl = null;

        if (clearMetadata)
        {
            UpdateMetadata(StreamMetadata.Empty);
        }
    }

    /// <summary>
    /// Fetches metadata immediately for the given stream URL (on-demand refresh).
    /// </summary>
    public async Task RefreshAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return;
        }

        await FetchMetadataAsync(streamUrl, cancellationToken);
    }

    /// <summary>
    /// Fetches metadata from the current stream URL.
    /// </summary>
    private async Task FetchMetadataAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_currentStreamUrl))
        {
            return;
        }

        await FetchMetadataAsync(_currentStreamUrl, cancellationToken);
    }

    private async Task FetchMetadataAsync(string streamUrl, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, streamUrl);

            // Request only partial content to minimize bandwidth
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[StreamMetadataService] HTTP {(int)response.StatusCode} from stream");
                return;
            }

            // Check for ICY metadata interval header in response headers and content headers
            int metaInterval = GetIcyMetaInterval(response);
            Debug.WriteLine($"[StreamMetadataService] icy-metaint: {metaInterval}");

            if (metaInterval <= 0)
            {
                // No ICY metadata support
                Debug.WriteLine("[StreamMetadataService] Stream does not support ICY metadata");
                return;
            }

            // Read stream data to find metadata
            // Using ResponseHeadersRead allows us to start processing the stream immediately
            // The 'using' statement ensures the stream is disposed after reading just enough data
            // to extract the first metadata block, minimizing bandwidth usage
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            StreamMetadata? metadata = await ReadIcyMetadataAsync(stream, metaInterval, cancellationToken);

            if (metadata is not null && metadata.HasMetadata)
            {
                UpdateMetadata(metadata);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw cancellation
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[StreamMetadataService] HTTP error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamMetadataService] Error fetching metadata: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads ICY metadata from the stream.
    /// </summary>
    private static async Task<StreamMetadata?> ReadIcyMetadataAsync(
        Stream stream,
        int metaInterval,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read and discard audio data in chunks until we reach the metadata
            // Using a fixed buffer size reduces memory allocation for large metaInterval values
            const int chunkSize = 8192;
            byte[] discardBuffer = new byte[Math.Min(chunkSize, metaInterval)];
            int totalRead = 0;

            while (totalRead < metaInterval)
            {
                int bytesToRead = Math.Min(discardBuffer.Length, metaInterval - totalRead);
                int bytesRead = await stream.ReadAsync(
                    discardBuffer.AsMemory(0, bytesToRead),
                    cancellationToken);

                if (bytesRead == 0)
                {
                    Debug.WriteLine("[StreamMetadataService] Stream ended before metadata");
                    return null;
                }

                totalRead += bytesRead;
            }

            // Read metadata length byte (multiply by 16 to get actual length)
            // Reuse the first byte of the discard buffer for efficiency
            int lengthBytesRead = await stream.ReadAsync(discardBuffer.AsMemory(0, 1), cancellationToken);
            if (lengthBytesRead == 0)
            {
                Debug.WriteLine("[StreamMetadataService] Could not read metadata length");
                return null;
            }

            int metaLength = discardBuffer[0] * 16;
            if (metaLength == 0)
            {
                // No metadata in this block
                Debug.WriteLine("[StreamMetadataService] No metadata in current block");
                return null;
            }

            // Read metadata
            byte[] metaBuffer = new byte[metaLength];
            totalRead = 0;

            while (totalRead < metaLength)
            {
                int bytesRead = await stream.ReadAsync(
                    metaBuffer.AsMemory(totalRead, metaLength - totalRead),
                    cancellationToken);

                if (bytesRead == 0)
                {
                    Debug.WriteLine("[StreamMetadataService] Stream ended before full metadata");
                    return null;
                }

                totalRead += bytesRead;
            }

            // Parse metadata string (null-terminated, padded with zeros).
            // ICY metadata has no charset signaling. Most stations send UTF-8, but many
            // legacy Shoutcast/Icecast sources send whatever local codepage the broadcast
            // software used, which is not valid UTF-8. Decoding those bytes as UTF-8 replaces
            // every invalid sequence with U+FFFD, permanently destroying the original text.
            // Falling back to Latin-1 (a 1:1 byte-to-codepoint mapping) at least preserves the
            // original bytes losslessly instead of mangling them into '�'.
            string metadataStr = DecodeIcyMetadata(metaBuffer).TrimEnd('\0');
            Debug.WriteLine($"[StreamMetadataService] Raw metadata: {metadataStr}");

            return ParseIcyMetadata(metadataStr);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamMetadataService] Error reading ICY metadata: {ex.Message}");
            return null;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes a raw ICY metadata block. Tries strict UTF-8 first (the common case); if the
    /// bytes aren't valid UTF-8, falls back to Latin-1 so the original bytes are preserved
    /// rather than replaced with U+FFFD.
    /// </summary>
    private static string DecodeIcyMetadata(byte[] metaBuffer)
    {
        try
        {
            return StrictUtf8.GetString(metaBuffer);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(metaBuffer);
        }
    }

    // ICY servers typically re-send the same metadata block on every poll until the track
    // changes. Without this, the album-art logging in ParseIcyMetadata would repeat every
    // ~10s for an unchanged track, drowning out the signal in the bounded log file.
    private static string? _lastLoggedIcyMetadata;

    /// <summary>
    /// Parses an ICY metadata string into a StreamMetadata object.
    /// Format is typically: StreamTitle='Artist - Song';StreamUrl='...';
    /// Alternative format: Exploring title="Song",artist="Artist",url="...",amgArtworkURL="..."
    /// Another format: StreamTitle='Artist - text="Song" amgArtworkURL="..."';
    /// </summary>
    private static StreamMetadata ParseIcyMetadata(string metadataStr)
    {
        StreamMetadata metadata = new();

        if (string.IsNullOrWhiteSpace(metadataStr))
        {
            return metadata;
        }

        Debug.WriteLine($"[StreamMetadataService] Raw metadata String: {metadataStr}");

        bool logArtworkOutcome = !string.Equals(_lastLoggedIcyMetadata, metadataStr, StringComparison.Ordinal);
        _lastLoggedIcyMetadata = metadataStr;

        // Check for "Exploring" format (used by iHeartRadio and some other stations)
        if (metadataStr.Contains("Exploring ", StringComparison.OrdinalIgnoreCase))
        {
            ParseExploringFormat(metadataStr, metadata, logArtworkOutcome);
            return metadata;
        }

        // Standard ICY format parsing
        // Extract StreamTitle
        const string streamTitleKey = "StreamTitle='";
        int titleStart = metadataStr.IndexOf(streamTitleKey, StringComparison.OrdinalIgnoreCase);

        if (titleStart >= 0)
        {
            titleStart += streamTitleKey.Length;
            int titleEnd = metadataStr.IndexOf("';", titleStart, StringComparison.Ordinal);

            if (titleEnd > titleStart)
            {
                metadata.StreamTitle = metadataStr[titleStart..titleEnd];
                Debug.WriteLine($"[StreamMetadataService] StreamTitle: {metadata.StreamTitle}");

                // Check if this is the "Artist - text=" format (iHeartRadio variant)
                if (metadata.StreamTitle.Contains(" - text=\"", StringComparison.OrdinalIgnoreCase))
                {
                    ParseIHeartRadioFormat(metadata.StreamTitle, metadata);
                }
                else
                {
                    // Try to parse "Artist - Title" format
                    ParseArtistAndTitle(metadata);
                }

                // Try to extract album artwork from within StreamTitle if present
                string? artworkUrl = ExtractAttribute(metadata.StreamTitle, "amgArtworkURL");
                if (!string.IsNullOrWhiteSpace(artworkUrl))
                {
                    if (IsImageUrl(artworkUrl))
                    {
                        metadata.AlbumArtUrl = artworkUrl;
                        if (logArtworkOutcome)
                        {
                            LogService.Info("StreamMetadataService", $"Album art found via amgArtworkURL: {artworkUrl}");
                        }

                        Debug.WriteLine($"[StreamMetadataService] AlbumArtUrl from StreamTitle: {metadata.AlbumArtUrl}");
                    }
                    else if (logArtworkOutcome)
                    {
                        LogService.Info("StreamMetadataService", $"amgArtworkURL present but not recognized as an image, ignoring: {artworkUrl}");
                    }
                }
            }
        }

        // Extract StreamUrl (often contains album art URL)
        const string streamUrlKey = "StreamUrl='";
        int urlStart = metadataStr.IndexOf(streamUrlKey, StringComparison.OrdinalIgnoreCase);

        if (urlStart >= 0)
        {
            urlStart += streamUrlKey.Length;
            int urlEnd = metadataStr.IndexOf("';", urlStart, StringComparison.Ordinal);

            if (urlEnd > urlStart)
            {
                string streamUrl = metadataStr[urlStart..urlEnd];
                // Check if it's an image URL
                if (IsImageUrl(streamUrl))
                {
                    metadata.AlbumArtUrl = streamUrl;
                    if (logArtworkOutcome)
                    {
                        LogService.Info("StreamMetadataService", $"Album art found via StreamUrl: {streamUrl}");
                    }

                    Debug.WriteLine($"[StreamMetadataService] AlbumArtUrl: {metadata.AlbumArtUrl}");
                }
                else if (logArtworkOutcome && string.IsNullOrWhiteSpace(metadata.AlbumArtUrl))
                {
                    LogService.Info("StreamMetadataService", $"StreamUrl present but not recognized as an image, ignoring: {streamUrl}");
                }
            }
        }

        if (logArtworkOutcome && string.IsNullOrWhiteSpace(metadata.AlbumArtUrl))
        {
            LogService.Info("StreamMetadataService", $"No album art found in ICY metadata: {metadataStr}");
        }

        return metadata;
    }

    /// <summary>
    /// Parses the iHeartRadio variant format where StreamTitle contains structured data.
    /// Format: "Artist - text="Song Title" amgArtworkURL="..." ..."
    /// </summary>
    private static void ParseIHeartRadioFormat(string streamTitle, StreamMetadata metadata)
    {
        // Find the " - text=" separator
        int separatorIndex = streamTitle.IndexOf(" - text=\"", StringComparison.OrdinalIgnoreCase);
        if (separatorIndex < 0)
            return;

        // Extract artist (everything before " - text=")
        string artist = streamTitle[..separatorIndex].Trim();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            metadata.Artist = artist;
            Debug.WriteLine($"[StreamMetadataService] iHeartRadio format - Artist: {artist}");
        }

        // Extract title from text attribute
        string? title = ExtractAttribute(streamTitle, "text");
        if (!string.IsNullOrWhiteSpace(title))
        {
            metadata.Title = title;
            Debug.WriteLine($"[StreamMetadataService] iHeartRadio format - Title: {title}");
        }

        // Update StreamTitle to clean format
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            metadata.StreamTitle = $"{artist} - {title}";
            Debug.WriteLine($"[StreamMetadataService] iHeartRadio format - Cleaned StreamTitle: {metadata.StreamTitle}");
        }
    }

    /// <summary>
    /// Parses the "Exploring" metadata format used by iHeartRadio and similar stations.
    /// Format: Exploring title="Song",artist="Artist",amgArtworkURL="http://..."
    /// </summary>
    private static void ParseExploringFormat(string metadataStr, StreamMetadata metadata, bool logArtworkOutcome)
    {
        // Extract title
        string? title = ExtractAttribute(metadataStr, "title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            metadata.Title = title;
            Debug.WriteLine($"[StreamMetadataService] Exploring format - Title: {title}");
        }

        // Extract artist
        string? artist = ExtractAttribute(metadataStr, "artist");
        if (!string.IsNullOrWhiteSpace(artist))
        {
            metadata.Artist = artist;
            Debug.WriteLine($"[StreamMetadataService] Exploring format - Artist: {artist}");
        }

        // Build StreamTitle from artist and title
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            metadata.StreamTitle = $"{artist} - {title}";
        }
        else if (!string.IsNullOrWhiteSpace(title))
        {
            metadata.StreamTitle = title;
        }

        // Extract album artwork URL (try multiple possible attribute names)
        string? artworkUrl = ExtractAttribute(metadataStr, "amgArtworkURL")
                          ?? ExtractAttribute(metadataStr, "artworkURL")
                          ?? ExtractAttribute(metadataStr, "url");

        if (!string.IsNullOrWhiteSpace(artworkUrl))
        {
            if (IsImageUrl(artworkUrl))
            {
                metadata.AlbumArtUrl = artworkUrl;
                if (logArtworkOutcome)
                {
                    LogService.Info("StreamMetadataService", $"Album art found via Exploring format: {artworkUrl}");
                }

                Debug.WriteLine($"[StreamMetadataService] Exploring format - AlbumArtUrl: {artworkUrl}");
            }
            else if (logArtworkOutcome)
            {
                LogService.Info("StreamMetadataService", $"Exploring format artwork attribute present but not recognized as an image, ignoring: {artworkUrl}");
            }
        }
        else if (logArtworkOutcome)
        {
            LogService.Info("StreamMetadataService", $"No album art found in Exploring format metadata: {metadataStr}");
        }
    }

    /// <summary>
    /// Extracts an attribute value from a metadata string.
    /// Example: title="Without Me" returns "Without Me"
    /// </summary>
    private static string? ExtractAttribute(string metadataStr, string attributeName)
    {
        string pattern = $"{attributeName}=\"";
        int startIndex = metadataStr.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);

        if (startIndex < 0)
            return null;

        startIndex += pattern.Length;
        int endIndex = metadataStr.IndexOf('"', startIndex);

        if (endIndex < 0)
            return null;

        return metadataStr[startIndex..endIndex];
    }

    /// <summary>
    /// Checks if a URL appears to be an image URL based on extension.
    /// </summary>
    private static bool IsImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        string lowerUrl = url.ToLowerInvariant();
        return lowerUrl.EndsWith(".jpg") ||
               lowerUrl.EndsWith(".jpeg") ||
               lowerUrl.EndsWith(".png") ||
               lowerUrl.EndsWith(".gif") ||
               lowerUrl.EndsWith(".webp") ||
               lowerUrl.Contains(".jpg?") ||
               lowerUrl.Contains(".jpeg?") ||
               lowerUrl.Contains(".png?");
    }

    /// <summary>
    /// Attempts to parse Artist and Title from the StreamTitle using common formats.
    /// </summary>
    public static void ParseArtistAndTitle(StreamMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.StreamTitle))
        {
            return;
        }

        // Common separators: " - ", " – ", " — "
        string[] separators = [" - ", " – ", " — "];

        foreach (string separator in separators)
        {
            int separatorIndex = metadata.StreamTitle.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                metadata.Artist = metadata.StreamTitle[..separatorIndex].Trim();
                metadata.Title = metadata.StreamTitle[(separatorIndex + separator.Length)..].Trim();
                Debug.WriteLine($"[StreamMetadataService] Parsed - Artist: {metadata.Artist}, Title: {metadata.Title}");
                return;
            }
        }

        // If no separator found, use the whole string as Title
        metadata.Title = metadata.StreamTitle;
    }

    /// <summary>
    /// Extracts the ICY metadata interval from response headers.
    /// </summary>
    private static int GetIcyMetaInterval(HttpResponseMessage response)
    {
        // Check response headers first
        if (response.Headers.TryGetValues("icy-metaint", out IEnumerable<string>? metaIntValues))
        {
            foreach (string metaIntStr in metaIntValues)
            {
                if (int.TryParse(metaIntStr, out int parsed))
                {
                    return parsed;
                }
            }
        }

        // Also check content headers (some servers send it there)
        if (response.Content.Headers.TryGetValues("icy-metaint", out IEnumerable<string>? contentMetaIntValues))
        {
            foreach (string val in contentMetaIntValues)
            {
                if (int.TryParse(val, out int parsed))
                {
                    return parsed;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Updates the current metadata and raises the MetadataChanged event if changed.
    /// </summary>
    private void UpdateMetadata(StreamMetadata newMetadata)
    {
        // Check if metadata actually changed
        if (_currentMetadata.StreamTitle == newMetadata.StreamTitle &&
            _currentMetadata.Artist == newMetadata.Artist &&
            _currentMetadata.Title == newMetadata.Title &&
            _currentMetadata.AlbumArtUrl == newMetadata.AlbumArtUrl)
        {
            return;
        }

        _currentMetadata = newMetadata;
        Debug.WriteLine($"[StreamMetadataService] Metadata updated: {newMetadata.DisplayText}");
        MetadataChanged?.Invoke(this, newMetadata);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopPolling();
        _httpClient.Dispose();
    }
}
