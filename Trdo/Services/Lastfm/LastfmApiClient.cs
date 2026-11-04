using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Talks to the Last.fm web service (auth + Scrobbling 2.0). One instance per set of API
/// credentials; the HTTP transport itself is shared across instances the same way
/// <see cref="RadioBrowserService"/> shares one <see cref="HttpClient"/> for its whole process
/// lifetime.
/// </summary>
internal sealed class LastfmApiClient
{
    private const string ApiRoot = "https://ws.audioscrobbler.com/2.0/";

    private static readonly HttpClient _httpClient = CreateHttpClient();

    private readonly string _apiKey;
    private readonly string _sharedSecret;

    public LastfmApiClient(string apiKey, string sharedSecret)
    {
        _apiKey = apiKey;
        _sharedSecret = sharedSecret;
    }

    private static HttpClient CreateHttpClient()
    {
        // Same reasoning as RadioBrowserService.CreateHttpClient: the WinINet-based handler can
        // fail with error 8007277C inside an MSIX app container, so use SocketsHttpHandler and a
        // direct connection.
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiRoot),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>Step 2 of the desktop auth flow: requests a fresh, single-use token.</summary>
    public Task<LastfmResult<string>> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> parameters = new()
        {
            ["method"] = "auth.getToken"
        };

        return SendAsync<LastfmTokenResponse, string>(
            HttpMethod.Get,
            LastfmJsonContext.Default.LastfmTokenResponse,
            parameters,
            response => string.IsNullOrWhiteSpace(response.Token)
                ? LastfmResult<string>.Failure(null, "Response had no token")
                : LastfmResult<string>.Success(response.Token),
            cancellationToken);
    }

    /// <summary>Step 4 of the desktop auth flow: exchanges an approved token for a session key.</summary>
    public Task<LastfmResult<(string Username, string SessionKey)>> GetSessionAsync(
        string token, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> parameters = new()
        {
            ["method"] = "auth.getSession",
            ["token"] = token
        };

        return SendAsync<LastfmSessionEnvelope, (string Username, string SessionKey)>(
            HttpMethod.Get,
            LastfmJsonContext.Default.LastfmSessionEnvelope,
            parameters,
            response => response.Session is { Name.Length: > 0, Key.Length: > 0 } session
                ? LastfmResult<(string, string)>.Success((session.Name!, session.Key!))
                : LastfmResult<(string, string)>.Failure(null, "Response had no session"),
            cancellationToken);
    }

    /// <summary>
    /// Tells Last.fm what is playing right now. Best-effort: does not affect the user's charts,
    /// and a failure here is never worth surfacing to the user or retrying from a queue.
    /// </summary>
    public Task<LastfmResult<bool>> UpdateNowPlayingAsync(
        LastfmNowPlayingRequest request, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> parameters = new()
        {
            ["method"] = "track.updateNowPlaying",
            ["sk"] = sessionKey,
            ["artist"] = request.Artist,
            ["track"] = request.Track,
        };
        AddIfPresent(parameters, "album", request.Album);
        AddIfPresent(parameters, "albumArtist", request.AlbumArtist);
        AddIfPresent(parameters, "mbid", request.Mbid);
        if (request.DurationSeconds is int duration)
            parameters["duration"] = duration.ToString(CultureInfo.InvariantCulture);
        if (request.TrackNumber is int trackNumber)
            parameters["trackNumber"] = trackNumber.ToString(CultureInfo.InvariantCulture);

        return SendAsync<LastfmNowPlayingEnvelope, bool>(
            HttpMethod.Post,
            LastfmJsonContext.Default.LastfmNowPlayingEnvelope,
            parameters,
            _ => LastfmResult<bool>.Success(true),
            cancellationToken);
    }

    /// <summary>
    /// Submits up to 50 scrobbles in one batch, per Last.fm's Scrobbling 2.0 limit. The caller
    /// (<see cref="LastfmScrobbleQueueService"/>) is responsible for chunking a larger backlog.
    /// </summary>
    public Task<LastfmResult<(int Accepted, int Ignored)>> ScrobbleAsync(
        IReadOnlyList<LastfmScrobbleRequest> batch, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> parameters = new()
        {
            ["method"] = "track.scrobble",
            ["sk"] = sessionKey,
        };

        for (int i = 0; i < batch.Count; i++)
        {
            LastfmScrobbleRequest item = batch[i];
            string suffix = $"[{i}]";

            parameters[$"artist{suffix}"] = item.Artist;
            parameters[$"track{suffix}"] = item.Track;
            parameters[$"timestamp{suffix}"] = item.TimestampUnix.ToString(CultureInfo.InvariantCulture);
            parameters[$"chosenByUser{suffix}"] = item.ChosenByUser ? "1" : "0";
            AddIfPresent(parameters, $"album{suffix}", item.Album);
            AddIfPresent(parameters, $"albumArtist{suffix}", item.AlbumArtist);
            AddIfPresent(parameters, $"mbid{suffix}", item.Mbid);
            if (item.DurationSeconds is int duration)
                parameters[$"duration{suffix}"] = duration.ToString(CultureInfo.InvariantCulture);
            if (item.TrackNumber is int trackNumber)
                parameters[$"trackNumber{suffix}"] = trackNumber.ToString(CultureInfo.InvariantCulture);
        }

        return SendAsync<LastfmScrobbleEnvelope, (int Accepted, int Ignored)>(
            HttpMethod.Post,
            LastfmJsonContext.Default.LastfmScrobbleEnvelope,
            parameters,
            response => response.Scrobbles?.Attr is { } attr
                ? LastfmResult<(int, int)>.Success((attr.Accepted, attr.Ignored))
                // A 200 OK with no @attr block is not something Last.fm's spec describes, but
                // whatever was submitted was clearly received - treat it as accepted with
                // unknown counts rather than as a failure that would retry forever.
                : LastfmResult<(int, int)>.Success((batch.Count, 0)),
            cancellationToken);
    }

    private static void AddIfPresent(Dictionary<string, string> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parameters[key] = value;
    }

    private async Task<LastfmResult<TOut>> SendAsync<TResponse, TOut>(
        HttpMethod method,
        JsonTypeInfo<TResponse> responseTypeInfo,
        Dictionary<string, string> parameters,
        Func<TResponse, LastfmResult<TOut>> project,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        parameters["api_key"] = _apiKey;
        parameters["api_sig"] = LastfmSignature.Compute(parameters, _sharedSecret);
        parameters["format"] = "json";

        string methodName = parameters.TryGetValue("method", out string? m) ? m : "(unknown)";

        try
        {
            using HttpResponseMessage response = method == HttpMethod.Get
                ? await _httpClient.GetAsync(BuildQuery(parameters), cancellationToken)
                : await _httpClient.PostAsync(string.Empty, new FormUrlEncodedContent(parameters), cancellationToken);

            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Last.fm's JSON error envelope is authoritative regardless of HTTP status, so it is
            // always checked before trusting a success payload.
            LastfmErrorEnvelope? errorEnvelope = TryDeserialize(content, LastfmJsonContext.Default.LastfmErrorEnvelope);
            if (errorEnvelope is { Error: > 0 })
            {
                LastfmErrorCode? code = Enum.IsDefined(typeof(LastfmErrorCode), errorEnvelope.Error)
                    ? (LastfmErrorCode)errorEnvelope.Error
                    : null;

                LogService.Warn("Lastfm", $"{methodName} failed: error {errorEnvelope.Error} ({errorEnvelope.Message})");
                return LastfmResult<TOut>.Failure(code, errorEnvelope.Message);
            }

            TResponse? parsed = TryDeserialize(content, responseTypeInfo);
            if (parsed is null)
            {
                LogService.Warn("Lastfm", $"{methodName}: could not parse response");
                return LastfmResult<TOut>.Failure(null, "Unparseable response");
            }

            return project(parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn("Lastfm", $"{methodName} request failed: {ex.GetType().Name}: {ex.Message}");
            return LastfmResult<TOut>.Failure(null, ex.Message);
        }
    }

    private static string BuildQuery(Dictionary<string, string> parameters)
    {
        List<string> pairs = new(parameters.Count);
        foreach (KeyValuePair<string, string> kv in parameters)
            pairs.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");

        return "?" + string.Join('&', pairs);
    }

    private static T? TryDeserialize<T>(string json, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
