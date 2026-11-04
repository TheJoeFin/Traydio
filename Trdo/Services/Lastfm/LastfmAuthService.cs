using System;
using System.Threading;
using System.Threading.Tasks;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Drives Last.fm's desktop authentication flow: request a token, send the user to approve it in
/// their browser, then exchange the approved token for a session key. See
/// <see cref="Controls.LastfmAuthWindow"/> for the UI that calls this.
/// </summary>
internal static class LastfmAuthService
{
    private const string AuthorizeUrlFormat = "https://www.last.fm/api/auth/?api_key={0}&token={1}";

    /// <summary>Whether this build has real Last.fm API credentials configured at all.</summary>
    public static bool IsAvailable => LastfmCredentials.TryGetCredentials(out _, out _);

    /// <summary>Step 2: requests a fresh, single-use token, valid for 60 minutes.</summary>
    public static async Task<LastfmResult<string>> RequestTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!LastfmCredentials.TryGetCredentials(out string apiKey, out string apiSecret))
            return LastfmResult<string>.Failure(null, "Last.fm API credentials are not configured.");

        LastfmApiClient client = new(apiKey, apiSecret);
        return await client.GetTokenAsync(cancellationToken);
    }

    /// <summary>Step 3: the URL to send the user's browser to so they can approve the token.</summary>
    public static Uri? BuildAuthorizeUri(string token)
    {
        if (!LastfmCredentials.TryGetCredentials(out string apiKey, out _))
            return null;

        return new Uri(string.Format(
            AuthorizeUrlFormat,
            Uri.EscapeDataString(apiKey),
            Uri.EscapeDataString(token)));
    }

    /// <summary>
    /// Step 4: exchanges an approved token for a session key and saves it. Safe to call again if
    /// the user has not approved yet - the token stays valid for 60 minutes and is single-use
    /// only once this succeeds.
    /// </summary>
    public static async Task<LastfmResult<string>> CompleteAuthAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!LastfmCredentials.TryGetCredentials(out string apiKey, out string apiSecret))
            return LastfmResult<string>.Failure(null, "Last.fm API credentials are not configured.");

        LastfmApiClient client = new(apiKey, apiSecret);
        LastfmResult<(string Username, string SessionKey)> result = await client.GetSessionAsync(token, cancellationToken);

        if (!result.IsSuccess)
            return LastfmResult<string>.Failure(result.ErrorCode, result.ErrorMessage);

        LastfmAccountStore.SaveSession(result.Value.Username, result.Value.SessionKey);
        return LastfmResult<string>.Success(result.Value.Username);
    }
}
