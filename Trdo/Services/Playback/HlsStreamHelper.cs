using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services.Metadata;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;

namespace Trdo.Services.Playback;

internal static class HlsStreamHelper
{
    private const string UserAgent = "Trdo/1.0";

    public static bool IsLikelyHlsUrl(string streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string path = uri.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps an HLS playlist URL to the direct HTTP stream URL for ICY metadata fallback.
    /// </summary>
    public static string? GetDirectStreamUrlFromHls(string hlsStreamUrl)
    {
        if (!IsLikelyHlsUrl(hlsStreamUrl))
        {
            return null;
        }

        const string hlsSuffix = "/hls.m3u8";
        if (hlsStreamUrl.EndsWith(hlsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return hlsStreamUrl[..^hlsSuffix.Length];
        }

        return null;
    }

    public static async Task<StreamMetadata?> TryFetchMetadataFromLatestSegmentAsync(
        HttpClient httpClient,
        string playlistUrl,
        CancellationToken cancellationToken = default)
    {
        string? segmentUrl = await ResolveLatestSegmentUrlAsync(httpClient, playlistUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(segmentUrl))
        {
            Debug.WriteLine($"[HlsStreamHelper] No media segment found in playlist: {playlistUrl}");
            return null;
        }

        byte[] segmentData = await DownloadSegmentPrefixAsync(httpClient, segmentUrl, cancellationToken);
        if (segmentData.Length == 0)
        {
            return null;
        }

        byte[]? id3Data = ExtractId3Tag(segmentData);
        if (id3Data is null)
        {
            Debug.WriteLine($"[HlsStreamHelper] No ID3 tag found in segment: {segmentUrl}");
            return null;
        }

        StreamMetadata metadata = Id3TagParser.Parse(id3Data);
        return metadata.HasMetadata ? metadata : null;
    }

    private static async Task<string?> ResolveLatestSegmentUrlAsync(
        HttpClient httpClient,
        string playlistUrl,
        CancellationToken cancellationToken)
    {
        string playlistContent = await DownloadTextAsync(httpClient, playlistUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistContent))
        {
            return null;
        }

        string[] lines = playlistContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= lines.Length)
            {
                break;
            }

            string variantUrl = ResolvePlaylistReference(playlistUrl, lines[i + 1]);
            return await ResolveLatestSegmentUrlAsync(httpClient, variantUrl, cancellationToken);
        }

        string? lastSegment = null;
        foreach (string line in lines)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            lastSegment = line;
        }

        return string.IsNullOrWhiteSpace(lastSegment)
            ? null
            : ResolvePlaylistReference(playlistUrl, lastSegment);
    }

    private static async Task<byte[]> DownloadSegmentPrefixAsync(
        HttpClient httpClient,
        string segmentUrl,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, segmentUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65535);

        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            Debug.WriteLine($"[HlsStreamHelper] Segment request failed: {(int)response.StatusCode} for {segmentUrl}");
            return [];
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<string> DownloadTextAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Debug.WriteLine($"[HlsStreamHelper] Playlist request failed: {(int)response.StatusCode} for {url}");
            return string.Empty;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string ResolvePlaylistReference(string basePlaylistUrl, string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.ToString();
        }

        if (!Uri.TryCreate(basePlaylistUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return reference;
        }

        return new Uri(baseUri, reference).ToString();
    }

    internal static byte[]? ExtractId3Tag(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> id3Marker = "ID3"u8;
        int index = data.IndexOf(id3Marker);
        if (index < 0 || index + 10 > data.Length)
        {
            return null;
        }

        int tagSize = ReadSyncSafeInt(data.Slice(index + 6, 4));
        int totalLength = 10 + tagSize;
        int available = data.Length - index;
        int copyLength = Math.Min(totalLength, available);

        return data.Slice(index, copyLength).ToArray();
    }

    private static int ReadSyncSafeInt(ReadOnlySpan<byte> data)
    {
        return (data[0] << 21) | (data[1] << 14) | (data[2] << 7) | data[3];
    }

    public static async Task<bool> IsHlsContentTypeAsync(HttpClient httpClient, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Head, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string? contentType = response.Content.Headers.ContentType?.MediaType;
            return contentType is not null &&
                   (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HlsStreamHelper] HEAD probe failed for {uri}: {ex.Message}");
            return false;
        }
    }

    public static async Task<(MediaPlaybackItem? Item, string? Error)> CreatePlaybackItemAsync(
        string streamUrl,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out Uri? uri))
        {
            return (null, "Invalid stream URL");
        }

        bool isHttp = uri.Scheme is "http" or "https";
        bool isHls = isHttp &&
                     (IsLikelyHlsUrl(streamUrl) || await IsHlsContentTypeAsync(httpClient, uri, cancellationToken));

        if (isHls)
        {
            return await CreateHlsPlaybackItemAsync(uri, httpClient, cancellationToken);
        }

        MediaSource mediaSource = MediaSource.CreateFromUri(uri);
        return (new MediaPlaybackItem(mediaSource), null);
    }

    private static async Task<(MediaPlaybackItem? Item, string? Error)> CreateHlsPlaybackItemAsync(
        Uri uri,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        try
        {
            AdaptiveMediaSourceCreationResult result =
                await AdaptiveMediaSource.CreateFromUriAsync(uri).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

            if (result.Status != AdaptiveMediaSourceCreationStatus.Success || result.MediaSource is null)
            {
                string error = $"HLS source creation failed: {result.Status}";
                Debug.WriteLine($"[HlsStreamHelper] {error} for {uri}");
                return (null, error);
            }

            AdaptiveMediaSource adaptiveSource = result.MediaSource;
            if (adaptiveSource.IsLive)
            {
                adaptiveSource.DesiredLiveOffset = TimeSpan.FromSeconds(3);
            }

            if (adaptiveSource.AvailableBitrates.Count > 0)
            {
                uint maxBitrate = 0;
                foreach (uint bitrate in adaptiveSource.AvailableBitrates)
                {
                    if (bitrate > maxBitrate)
                    {
                        maxBitrate = bitrate;
                    }
                }

                if (maxBitrate > 0)
                {
                    adaptiveSource.InitialBitrate = maxBitrate;
                }
            }

            MediaSource mediaSource = MediaSource.CreateFromAdaptiveMediaSource(adaptiveSource);
            return (new MediaPlaybackItem(mediaSource), null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HlsStreamHelper] Exception creating HLS item for {uri}: {ex.Message}");
            return (null, ex.Message);
        }
    }
}
