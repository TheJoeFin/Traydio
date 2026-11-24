using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Trdo.Services.Playback;

/// <summary>What a probe of the stream URL concluded.</summary>
public enum StreamProbeResult
{
    /// <summary>The server answered with something that looks like a playable audio stream.</summary>
    Reachable,

    /// <summary>The host name did not resolve.</summary>
    Unresolvable,

    /// <summary>The host resolved but refused the connection, or nothing is listening on the port.</summary>
    ConnectionRefused,

    /// <summary>The server accepted the connection but never sent usable data in time.</summary>
    Timeout,

    /// <summary>An HTTPS handshake failed (bad or expired certificate, protocol mismatch).</summary>
    TlsFailure,

    /// <summary>The server answered with an HTTP error status.</summary>
    HttpError,

    /// <summary>The URL points at a playlist file (.pls/.m3u/.asx), not at the audio itself.</summary>
    PlaylistFile,

    /// <summary>The server answered with content that is not audio (typically an HTML error page).</summary>
    UnsupportedContent,

    /// <summary>The server answered but sent no data.</summary>
    EmptyStream,

    /// <summary>The probe failed for a reason that could not be classified.</summary>
    Unknown
}

/// <summary>
/// The outcome of probing a stream URL: a short user-facing summary plus the technical
/// detail worth writing to the log.
/// </summary>
public sealed class StreamDiagnosis
{
    public required StreamProbeResult Result { get; init; }

    /// <summary>One sentence suitable for showing the user, ending in a period.</summary>
    public required string Summary { get; init; }

    /// <summary>Technical detail for the log file — status codes, headers, exception text.</summary>
    public required string Detail { get; init; }

    public int? StatusCode { get; init; }
    public string? ContentType { get; init; }
    public string? Server { get; init; }
    public string? StationName { get; init; }

    /// <summary>
    /// When <see cref="Result"/> is <see cref="StreamProbeResult.PlaylistFile"/>, the first
    /// stream URL found inside the playlist — the URL the user most likely wanted.
    /// </summary>
    public string? PlaylistEntryUrl { get; init; }

    /// <summary>True when the server looks healthy, i.e. the fault lies with the player, not the stream.</summary>
    public bool ServerLooksHealthy => Result == StreamProbeResult.Reachable;

    public override string ToString() => $"{Result}: {Detail}";
}

/// <summary>
/// Probes a stream URL over plain HTTP to work out <em>why</em> it will not play.
/// <para>
/// The playback backends can only report that they failed, not why: Windows Media
/// Foundation surfaces an HRESULT and LibVLC an untyped error event. Neither
/// distinguishes "the host does not exist" from "the server returned 404" from "this
/// URL is a .pls playlist, not a stream" — but those need completely different
/// answers from the user, so the app finds out for itself.
/// </para>
/// <para>
/// Deliberately free of WinRT and player dependencies so it can be unit tested against
/// a stubbed <see cref="HttpMessageHandler"/>.
/// </para>
/// </summary>
public static class StreamDiagnostics
{
    /// <summary>How long to give the server to answer before calling it unreachable.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How much of the response body to sniff. Enough for playlist headers and a frame sync.</summary>
    private const int SniffByteCount = 2048;

    private const string UserAgent = "Trdo/2.0";

    /// <summary>
    /// Probes <paramref name="streamUrl"/> and classifies the result. Never throws:
    /// any unexpected failure comes back as <see cref="StreamProbeResult.Unknown"/>.
    /// </summary>
    public static async Task<StreamDiagnosis> ProbeAsync(
        string streamUrl,
        HttpClient httpClient,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out Uri? uri))
        {
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.Unknown,
                Summary = "The stream address isn't a valid URL.",
                Detail = $"Could not parse '{streamUrl}' as an absolute URI."
            };
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultProbeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            // Icecast/Shoutcast servers describe themselves in the response headers when
            // metadata is requested, which gives us the station name and bitrate for free.
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token);

            return await ClassifyResponseAsync(uri, response, linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.Timeout,
                Summary = "The station's server didn't respond in time.",
                Detail = $"No usable response from {uri.Host}:{GetPort(uri)} within " +
                         $"{(timeout ?? DefaultProbeTimeout).TotalSeconds:F0}s."
            };
        }
        catch (HttpRequestException ex)
        {
            return ClassifyTransportException(uri, ex);
        }
        catch (Exception ex)
        {
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.Unknown,
                Summary = "The station's server couldn't be reached.",
                Detail = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// True when the address names a playlist file (.pls, .m3u, .asx) by extension. HLS
    /// (.m3u8) is deliberately excluded: both engines play it natively.
    /// </summary>
    public static bool HasPlaylistExtension(string? streamUrl)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string path = uri.AbsolutePath;
        return path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".asx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetches a playlist address and returns the first stream URL inside it, or
    /// <c>null</c> when the address isn't a playlist or has no usable entry.
    /// </summary>
    public static async Task<string?> ResolvePlaylistEntryAsync(
        string playlistUrl,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        StreamDiagnosis diagnosis = await ProbeAsync(playlistUrl, httpClient, cancellationToken: cancellationToken);
        return diagnosis.Result == StreamProbeResult.PlaylistFile ? diagnosis.PlaylistEntryUrl : null;
    }

    private static async Task<StreamDiagnosis> ClassifyResponseAsync(
        Uri uri,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        string? server = FirstHeaderValue(response, "Server");
        string? stationName = FirstHeaderValue(response, "icy-name");
        string? bitrate = FirstHeaderValue(response, "icy-br");

        string serverDescription =
            $"HTTP {status} {response.ReasonPhrase}; content-type={contentType ?? "(none)"}" +
            $"; server={server ?? "(none)"}" +
            (stationName is null ? string.Empty : $"; icy-name={stationName}") +
            (bitrate is null ? string.Empty : $"; icy-br={bitrate}");

        if (!response.IsSuccessStatusCode)
        {
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.HttpError,
                Summary = DescribeHttpError(status, response.ReasonPhrase),
                Detail = serverDescription,
                StatusCode = status,
                ContentType = contentType,
                Server = server
            };
        }

        byte[] sniff = await ReadPrefixAsync(response, cancellationToken);

        if (sniff.Length == 0)
        {
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.EmptyStream,
                Summary = "The station's server accepted the connection but sent no audio.",
                Detail = serverDescription + "; body was empty",
                StatusCode = status,
                ContentType = contentType,
                Server = server,
                StationName = stationName
            };
        }

        string textPrefix = Encoding.UTF8.GetString(sniff, 0, Math.Min(sniff.Length, SniffByteCount));

        if (IsPlaylist(contentType, uri, textPrefix))
        {
            string? entry = ExtractFirstPlaylistEntry(textPrefix);
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.PlaylistFile,
                Summary = entry is null
                    ? "This address is a playlist file, not an audio stream."
                    : "This address is a playlist file, not an audio stream. It points to another URL.",
                Detail = serverDescription + $"; body looks like a playlist; firstEntry={entry ?? "(none found)"}",
                StatusCode = status,
                ContentType = contentType,
                Server = server,
                StationName = stationName,
                PlaylistEntryUrl = entry
            };
        }

        if (LooksLikeHtml(contentType, textPrefix))
        {
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.UnsupportedContent,
                Summary = "This address returns a web page, not an audio stream.",
                Detail = serverDescription + "; body looks like HTML",
                StatusCode = status,
                ContentType = contentType,
                Server = server
            };
        }

        return new StreamDiagnosis
        {
            Result = StreamProbeResult.Reachable,
            Summary = "The station's server is reachable and sending audio.",
            Detail = serverDescription + $"; received {sniff.Length} bytes of body",
            StatusCode = status,
            ContentType = contentType,
            Server = server,
            StationName = stationName
        };
    }

    /// <summary>
    /// Maps a transport-level failure onto a specific cause. <see cref="HttpRequestException"/>
    /// buries the useful part (DNS vs. refused vs. TLS) in its inner exception.
    /// </summary>
    private static StreamDiagnosis ClassifyTransportException(Uri uri, HttpRequestException ex)
    {
        string endpoint = $"{uri.Host}:{GetPort(uri)}";

        SocketException? socketException = FindInner<SocketException>(ex);
        if (socketException is not null)
        {
            switch (socketException.SocketErrorCode)
            {
                case SocketError.HostNotFound:
                case SocketError.NoData:
                    return new StreamDiagnosis
                    {
                        Result = StreamProbeResult.Unresolvable,
                        Summary = $"The station's address ({uri.Host}) couldn't be found.",
                        Detail = $"DNS lookup for {uri.Host} failed: {socketException.SocketErrorCode}"
                    };

                case SocketError.ConnectionRefused:
                    return new StreamDiagnosis
                    {
                        Result = StreamProbeResult.ConnectionRefused,
                        Summary = "The station's server refused the connection — it's probably offline.",
                        Detail = $"Connection to {endpoint} refused"
                    };

                case SocketError.TimedOut:
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                    return new StreamDiagnosis
                    {
                        Result = StreamProbeResult.Timeout,
                        Summary = "The station's server couldn't be reached.",
                        Detail = $"Connection to {endpoint} failed: {socketException.SocketErrorCode}"
                    };
            }
        }

        if (FindInner<System.Security.Authentication.AuthenticationException>(ex) is { } authException)
        {
            return new StreamDiagnosis
            {
                Result = StreamProbeResult.TlsFailure,
                Summary = "The station's secure connection couldn't be established.",
                Detail = $"TLS handshake with {endpoint} failed: {authException.Message}"
            };
        }

        return new StreamDiagnosis
        {
            Result = StreamProbeResult.Unknown,
            Summary = "The station's server couldn't be reached.",
            Detail = $"{ex.GetType().Name} for {endpoint}: {ex.Message}"
        };
    }

    private static async Task<byte[]> ReadPrefixAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            byte[] buffer = new byte[SniffByteCount];
            int total = 0;

            // A live stream never ends, so read until the buffer fills rather than to EOF.
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return buffer[..total];
        }
        catch (OperationCanceledException)
        {
            // Timing out mid-body still tells us the server answered; treat it as no body.
            return [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsPlaylist(string? contentType, Uri uri, string textPrefix)
    {
        if (contentType is not null &&
            (contentType.Contains("scpls", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("pls+xml", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("x-mpegurl", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("x-ms-asf", StringComparison.OrdinalIgnoreCase)))
        {
            // An HLS playlist is a stream the players handle natively, so it is not a fault.
            return !textPrefix.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase);
        }

        string path = uri.AbsolutePath;
        bool playlistExtension =
            path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".asx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);

        string trimmed = textPrefix.TrimStart();
        bool playlistBody =
            trimmed.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<asx", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) &&
             !trimmed.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase));

        return playlistBody || (playlistExtension && !LooksBinary(textPrefix));
    }

    private static bool LooksLikeHtml(string? contentType, string textPrefix)
    {
        if (contentType is not null && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string trimmed = textPrefix.TrimStart();
        return trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksBinary(string textPrefix)
    {
        foreach (char c in textPrefix)
        {
            if (c == '\0')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pulls the first stream URL out of a .pls, .m3u or .asx body, so the failure message
    /// can tell the user exactly which address to use instead.
    /// </summary>
    internal static string? ExtractFirstPlaylistEntry(string playlistText)
    {
        foreach (string rawLine in playlistText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // .pls: "File1=http://..."
            int equals = line.IndexOf('=');
            if (equals > 0 && line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = line[(equals + 1)..].Trim();
                if (IsHttpUrl(candidate))
                {
                    return candidate;
                }

                continue;
            }

            // .asx: <ref href="http://..." />
            int hrefIndex = line.IndexOf("href", StringComparison.OrdinalIgnoreCase);
            if (hrefIndex >= 0)
            {
                string? href = ExtractQuoted(line[hrefIndex..]);
                if (href is not null && IsHttpUrl(href))
                {
                    return href;
                }

                continue;
            }

            // .m3u: bare URL lines, comments start with '#'
            if (!line.StartsWith('#') && IsHttpUrl(line))
            {
                return line;
            }
        }

        return null;
    }

    private static string? ExtractQuoted(string text)
    {
        int first = text.IndexOfAny(['"', '\'']);
        if (first < 0)
        {
            return null;
        }

        char quote = text[first];
        int second = text.IndexOf(quote, first + 1);
        return second < 0 ? null : text[(first + 1)..second];
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string DescribeHttpError(int status, string? reason) => status switch
    {
        401 or 403 => "The station's server refused access to this stream.",
        404 or 410 => "The station's server says this stream address no longer exists.",
        429 => "The station's server is rejecting connections because it's too busy.",
        >= 500 => "The station's server reported an internal error.",
        _ => $"The station's server rejected the request (HTTP {status} {reason})."
    };

    private static string? FirstHeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values) ||
            response.Content.Headers.TryGetValues(name, out values))
        {
            return values.FirstOrDefault();
        }

        return null;
    }

    private static int GetPort(Uri uri) => uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;

    private static T? FindInner<T>(Exception? exception) where T : Exception
    {
        while (exception is not null)
        {
            if (exception is T match)
            {
                return match;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
