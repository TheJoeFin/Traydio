using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// Describes a faceted radio station search against the Radio Browser API.
/// Any property left empty/null is omitted from the request.
/// </summary>
public class StationSearchQuery
{
    public string? Name { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
    public string? Codec { get; set; }
    public int? BitrateMin { get; set; }
    public string Order { get; set; } = "votes";
    public bool Reverse { get; set; } = true;
    public bool HideBroken { get; set; }
    public int Limit { get; set; } = 50;

    /// <summary>
    /// True when nothing but the default ordering is set, i.e. there is nothing to search for.
    /// </summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(Country) &&
        string.IsNullOrWhiteSpace(Language) &&
        (Tags is null || Tags.Count == 0) &&
        string.IsNullOrWhiteSpace(Codec) &&
        !BitrateMin.HasValue;
}

/// <summary>
/// Service for searching radio stations using the Radio Browser API
/// </summary>
public class RadioBrowserService
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    // Lookup lists rarely change, so fetch each one once and reuse it.
    private static List<RadioBrowserCountry>? _cachedCountries;
    private static List<RadioBrowserLanguage>? _cachedLanguages;
    private static List<RadioBrowserTag>? _cachedTags;

    private static HttpClient CreateHttpClient()
    {
        // Use SocketsHttpHandler to avoid DNS resolution issues in packaged apps
        // The WinINet-based handler can fail with error 8007277C in app containers
        SocketsHttpHandler handler = new()
        {
            // Disable proxy to ensure direct connection works in packaged apps
            UseProxy = false,
            // Enable automatic decompression for better performance
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://de1.api.radio-browser.info/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Set a user agent as recommended by Radio Browser API. They ask for something
        // identifiable, and that matters most when refreshing details for a whole station list
        // in one go.
        client.DefaultRequestHeaders.Add("User-Agent", "Traydio/2.0 (+https://github.com/joefinney/Trdo)");

        return client;
    }

    /// <summary>
    /// Searches for radio stations using any combination of name and filters
    /// via the /json/stations/search endpoint.
    /// </summary>
    /// <param name="query">The search criteria</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of matching radio stations</returns>
    public async Task<List<RadioBrowserStation>> SearchAsync(
        StationSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (query.IsEmpty)
            {
                return [];
            }

            StringBuilder url = new("json/stations/search?");
            AppendParam(url, "name", query.Name);
            AppendParam(url, "country", query.Country);
            AppendParam(url, "language", query.Language);
            if (query.Tags is { Count: > 0 })
            {
                AppendParam(url, "tagList", string.Join(",", query.Tags));
            }
            AppendParam(url, "codec", query.Codec);
            if (query.BitrateMin.HasValue)
            {
                AppendParam(url, "bitrateMin", query.BitrateMin.Value.ToString());
            }
            AppendParam(url, "order", query.Order);
            AppendParam(url, "reverse", query.Reverse ? "true" : "false");
            AppendParam(url, "hidebroken", query.HideBroken ? "true" : "false");
            AppendParam(url, "limit", query.Limit.ToString());

            string requestUrl = url.ToString().TrimEnd('&');

            Debug.WriteLine($"[RadioBrowserService] Search URL: {_httpClient.BaseAddress}{requestUrl}");

            HttpResponseMessage response = await _httpClient.GetAsync(requestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            Debug.WriteLine($"[RadioBrowserService] Received response, length: {content.Length}");

            List<RadioBrowserStation>? stations = JsonSerializer.Deserialize(content, RadioBrowserJsonContext.Default.ListRadioBrowserStation);

            Debug.WriteLine($"[RadioBrowserService] Found {stations?.Count ?? 0} stations");

            return stations ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioBrowserService] Error searching stations: {ex.Message}");
            Debug.WriteLine($"[RadioBrowserService] Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Looks up directory entries matching a stream URL exactly.
    /// <para>
    /// Only ever called because the user asked for it. The app makes no unprompted requests
    /// about the stations someone has saved.
    /// </para>
    /// </summary>
    /// <returns>The candidate entries, or an empty list if the directory knows nothing about this URL.</returns>
    public async Task<List<RadioBrowserStation>> LookupByUrlAsync(
        string streamUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
            return [];

        try
        {
            // byurl takes the URL as a form parameter rather than in the path, which avoids
            // having to escape a whole URL inside another URL.
            using FormUrlEncodedContent body = new([new KeyValuePair<string, string>("url", streamUrl)]);
            using HttpResponseMessage response =
                await _httpClient.PostAsync("json/stations/byurl", body, cancellationToken);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            List<RadioBrowserStation>? stations =
                JsonSerializer.Deserialize(content, RadioBrowserJsonContext.Default.ListRadioBrowserStation);

            Debug.WriteLine($"[RadioBrowserService] byurl returned {stations?.Count ?? 0} candidates");
            return stations ?? [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A lookup that fails is not an error worth surfacing on its own - the station
            // simply keeps the details it already had. The batch reports the totals.
            Debug.WriteLine($"[RadioBrowserService] byurl lookup failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Gets the list of countries (ordered by station count, most first), cached after first fetch.
    /// </summary>
    public async Task<List<RadioBrowserCountry>> GetCountriesAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCountries is not null)
        {
            return _cachedCountries;
        }

        _cachedCountries = await GetListAsync<RadioBrowserCountry>(
            "json/countries?order=stationcount&reverse=true&hidebroken=true",
            RadioBrowserJsonContext.Default.ListRadioBrowserCountry,
            cancellationToken);
        return _cachedCountries;
    }

    /// <summary>
    /// Gets the list of languages (ordered by station count, most first), cached after first fetch.
    /// </summary>
    public async Task<List<RadioBrowserLanguage>> GetLanguagesAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedLanguages is not null)
        {
            return _cachedLanguages;
        }

        _cachedLanguages = await GetListAsync<RadioBrowserLanguage>(
            "json/languages?order=stationcount&reverse=true&hidebroken=true",
            RadioBrowserJsonContext.Default.ListRadioBrowserLanguage,
            cancellationToken);
        return _cachedLanguages;
    }

    /// <summary>
    /// Gets the most popular tags/genres (ordered by station count, most first), cached after first fetch.
    /// </summary>
    /// <param name="limit">
    /// Maximum number of tags to return; the full list runs to tens of thousands, most of them
    /// one-station typos. A thousand is generous now that the picker reaches a tag by typing
    /// rather than by scrolling — a longer list costs nothing to search, and cutting it short
    /// only loses genres someone might actually want.
    /// </param>
    public async Task<List<RadioBrowserTag>> GetTagsAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        if (_cachedTags is not null)
        {
            return _cachedTags;
        }

        _cachedTags = await GetListAsync<RadioBrowserTag>(
            $"json/tags?order=stationcount&reverse=true&hidebroken=true&limit={limit}",
            RadioBrowserJsonContext.Default.ListRadioBrowserTag,
            cancellationToken);
        return _cachedTags;
    }

    private static async Task<List<T>> GetListAsync<T>(string url, JsonTypeInfo<List<T>> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            Debug.WriteLine($"[RadioBrowserService] Fetching list: {_httpClient.BaseAddress}{url}");

            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            List<T>? items = JsonSerializer.Deserialize(content, typeInfo);

            Debug.WriteLine($"[RadioBrowserService] Fetched {items?.Count ?? 0} items");
            return items ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioBrowserService] Error fetching list: {ex.Message}");
            throw;
        }
    }

    private static void AppendParam(StringBuilder url, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        url.Append(key).Append('=').Append(Uri.EscapeDataString(value)).Append('&');
    }
}
