using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Trdo.Services.Playback;

/// <summary>
/// Serves the local music files a Sonos player has been asked to play. A speaker can only
/// fetch over HTTP, and it fetches the way a browser does - HEAD first, then GET with byte
/// ranges - so this is a small HTTP/1.1 file server rather than a stream relay. It only
/// hands out files that have been registered, each under an unguessable token, and it
/// listens only for as long as a Sonos target is set.
/// </summary>
public sealed class LocalMediaHttpServer : IDisposable
{
    private const string Component = "LocalMediaHttpServer";
    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokensByPath = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    /// <summary>The port the server is listening on, or 0 when it is not running.</summary>
    public int Port { get; private set; }

    /// <summary>Starts listening on an ephemeral port on every interface. Safe to call twice.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_lock)
        {
            if (_listener is not null)
            {
                return;
            }

            TcpListener listener = new(IPAddress.Any, 0);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(listener, _cts.Token);
        }

        LogService.Info(Component, $"Serving local music on port {Port}");
    }

    /// <summary>
    /// Registers <paramref name="filePath"/> and returns the URL a device at
    /// <paramref name="remoteAddress"/> can fetch it from. The host in that URL is whichever
    /// of this PC's addresses routes to the device, so a PC with several adapters hands out
    /// the one the speaker can actually reach.
    /// </summary>
    public string Register(string filePath, IPAddress remoteAddress)
    {
        Start();

        string token;
        lock (_lock)
        {
            if (!_tokensByPath.TryGetValue(filePath, out string? existing))
            {
                existing = RandomNumberGenerator.GetHexString(24, lowercase: true);
                _tokensByPath[filePath] = existing;
                _files[existing] = filePath;
            }

            token = existing;
        }

        IPAddress local = LocalAddressFor(remoteAddress);
        string extension = Path.GetExtension(filePath);
        return $"http://{local}:{Port.ToString(CultureInfo.InvariantCulture)}/media/{token}{extension}";
    }

    /// <summary>The address this PC uses to talk to <paramref name="remote"/>: what the OS would pick as the source of a packet there.</summary>
    public static IPAddress LocalAddressFor(IPAddress remote)
    {
        try
        {
            using Socket probe = new(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, 9);
            if (probe.LocalEndPoint is IPEndPoint endpoint && !IPAddress.Any.Equals(endpoint.Address))
            {
                return endpoint.Address;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(Component, $"Could not work out the local address facing {remote}: {ex.Message}");
        }

        return IPAddress.Loopback;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = ServeAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LogService.Error(Component, "Accept loop ended unexpectedly", ex);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();

                using CancellationTokenSource headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                headerCts.CancelAfter(HeaderReadTimeout);
                (string method, string target, Dictionary<string, string> headers)? request =
                    await ReadRequestAsync(stream, headerCts.Token).ConfigureAwait(false);

                if (request is null)
                {
                    return;
                }

                (string method, string target, Dictionary<string, string> headers) = request.Value;
                await RespondAsync(stream, method, target, headers, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // The speaker closes range requests early once it has what it wants.
            }
            catch (SocketException)
            {
            }
            catch (Exception ex)
            {
                LogService.Warn(Component, $"Request failed: {ex.Message}");
            }
        }
    }

    private static async Task<(string, string, Dictionary<string, string>)?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Headers only; nothing this server accepts has a body.
        byte[] buffer = new byte[8192];
        int length = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            length += read;
            int end = IndexOfHeaderEnd(buffer, length);
            if (end >= 0)
            {
                string text = Encoding.ASCII.GetString(buffer, 0, end);
                string[] lines = text.Split("\r\n");
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    return null;
                }

                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                    }
                }

                return (requestLine[0].ToUpperInvariant(), requestLine[1], headers);
            }

            if (length == buffer.Length)
            {
                return null;
            }
        }
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i + 1;
            }
        }

        return -1;
    }

    private async Task RespondAsync(NetworkStream stream, string method, string target, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (method is not ("GET" or "HEAD"))
        {
            await WriteStatusAsync(stream, "405 Method Not Allowed", cancellationToken).ConfigureAwait(false);
            return;
        }

        string? filePath = Resolve(target);
        if (filePath is null || !File.Exists(filePath))
        {
            await WriteStatusAsync(stream, "404 Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }

        FileInfo info = new(filePath);
        long total = info.Length;
        long start = 0;
        long end = total - 1;
        bool partial = false;

        if (headers.TryGetValue("Range", out string? range) && TryParseRange(range, total, out long rangeStart, out long rangeEnd))
        {
            start = rangeStart;
            end = rangeEnd;
            partial = true;
        }
        else if (range is not null)
        {
            string unsatisfiable =
                "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                $"Content-Range: bytes */{total.ToString(CultureInfo.InvariantCulture)}\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(unsatisfiable), cancellationToken).ConfigureAwait(false);
            return;
        }

        long count = end - start + 1;
        StringBuilder header = new();
        header.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        header.Append("Content-Type: ").Append(ContentTypeFor(filePath)).Append("\r\n");
        header.Append("Content-Length: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        header.Append("Accept-Ranges: bytes\r\n");
        if (partial)
        {
            header.Append("Content-Range: bytes ")
                .Append(start.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(end.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(total.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        header.Append("Cache-Control: no-cache\r\n");
        header.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);

        if (method == "HEAD")
        {
            return;
        }

        await using FileStream file = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        file.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[1 << 16];
        long remaining = count;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(buffer.Length, remaining);
            int read = await file.ReadAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private string? Resolve(string target)
    {
        const string prefix = "/media/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string rest = target[prefix.Length..];
        int query = rest.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            rest = rest[..query];
        }

        int dot = rest.IndexOf('.');
        string token = dot >= 0 ? rest[..dot] : rest;

        lock (_lock)
        {
            return _files.TryGetValue(token, out string? path) ? path : null;
        }
    }

    /// <summary>Parses a single "bytes=start-end" range (the only shape a player sends); false when it cannot be honoured.</summary>
    public static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        if (total <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string spec = header["bytes=".Length..].Trim();
        if (spec.Contains(','))
        {
            return false;
        }

        int dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        string first = spec[..dash].Trim();
        string second = spec[(dash + 1)..].Trim();

        if (first.Length == 0)
        {
            // Suffix range: the last N bytes.
            if (!long.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, total - suffix);
            end = total - 1;
            return true;
        }

        if (!long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0 || start >= total)
        {
            return false;
        }

        if (second.Length == 0)
        {
            end = total - 1;
            return true;
        }

        if (!long.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, total - 1);
        return true;
    }

    public static string ContentTypeFor(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".mp4" => "audio/mp4",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/ogg",
        ".wma" => "audio/x-ms-wma",
        ".aif" or ".aiff" => "audio/aiff",
        _ => "application/octet-stream",
    };

    private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken cancellationToken)
    {
        string response = $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
            }

            _listener = null;
            Port = 0;
            _files.Clear();
            _tokensByPath.Clear();
        }

        LogService.Info(Component, "Local music server stopped");
    }
}
