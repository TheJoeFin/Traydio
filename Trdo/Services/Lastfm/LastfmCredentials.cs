using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Windows.ApplicationModel;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Loads this app's own Last.fm API credentials (api_key + shared secret) from
/// <c>OAuth.resw</c> next to the executable. That file is gitignored - every developer working
/// on Last.fm integration registers their own API account at last.fm/api/account/create and
/// fills in a local copy from <c>OAuth.resw.example</c> - so its absence is an expected,
/// non-fatal state: Last.fm features simply stay unavailable rather than the app crashing.
/// </summary>
/// <remarks>
/// Deliberately not wired into the app's localization/PRI resource pipeline (that is for
/// per-locale, x:Uid-driven UI strings under Strings/&lt;locale&gt;/Resources.resw, which this is
/// not - a single flat, non-localized credentials file). Read directly as XML instead.
/// </remarks>
internal static class LastfmCredentials
{
    private const string OAuthFileName = "OAuth.resw";
    private const string ApiKeyResourceName = "LastfmApiKey";
    private const string ApiSecretResourceName = "LastfmApiSecret";

    private static (string ApiKey, string ApiSecret)? _cached;
    private static bool _loadAttempted;
    private static readonly object _lock = new();

    /// <summary>
    /// Whether real credentials were found. Everything that depends on Last.fm - the settings
    /// toggle, the Connect button - should check this before offering the feature at all.
    /// </summary>
    public static bool TryGetCredentials(out string apiKey, out string apiSecret)
    {
        EnsureLoaded();

        if (_cached is { } credentials)
        {
            apiKey = credentials.ApiKey;
            apiSecret = credentials.ApiSecret;
            return true;
        }

        apiKey = string.Empty;
        apiSecret = string.Empty;
        return false;
    }

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loadAttempted)
                return;

            _loadAttempted = true;

            try
            {
                string? path = FindOAuthFilePath();
                if (path is null || !File.Exists(path))
                {
                    LogService.Info("Lastfm",
                        "OAuth.resw not found; Last.fm scrobbling stays unavailable until a developer " +
                        "copies OAuth.resw.example to OAuth.resw and fills in a real API key/secret.");
                    return;
                }

                XDocument doc = XDocument.Load(path);
                string? apiKey = ReadValue(doc, ApiKeyResourceName);
                string? apiSecret = ReadValue(doc, ApiSecretResourceName);

                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
                {
                    LogService.Warn("Lastfm",
                        $"OAuth.resw is missing {ApiKeyResourceName}/{ApiSecretResourceName} values; Last.fm scrobbling stays unavailable.");
                    return;
                }

                _cached = (apiKey, apiSecret);
                LogService.Info("Lastfm", "Loaded Last.fm API credentials from OAuth.resw");
            }
            catch (Exception ex)
            {
                LogService.Warn("Lastfm", $"Failed to load OAuth.resw: {ex.Message}");
            }
        }
    }

    private static string? ReadValue(XDocument doc, string resourceName)
    {
        return doc.Root?
            .Elements("data")
            .FirstOrDefault(e => (string?)e.Attribute("name") == resourceName)?
            .Element("value")?
            .Value?
            .Trim();
    }

    private static string? FindOAuthFilePath()
    {
        try
        {
            string installedLocation = Package.Current.InstalledLocation.Path;
            return Path.Combine(installedLocation, OAuthFileName);
        }
        catch
        {
            // Package.Current throws when running unpackaged (e.g. a plain dev F5 without
            // MSIX) - fall back to the build output directory, where the content item lands.
            return Path.Combine(AppContext.BaseDirectory, OAuthFileName);
        }
    }
}
