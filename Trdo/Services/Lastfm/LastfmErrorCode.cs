namespace Trdo.Services.Lastfm;

/// <summary>
/// The subset of Last.fm's documented API error codes this app acts on differently from one
/// another. Values match the API spec so a raw <c>error</c> field can be cast directly.
/// </summary>
public enum LastfmErrorCode
{
    InvalidService = 2,
    InvalidMethod = 3,
    AuthenticationFailed = 4,
    InvalidFormat = 5,
    InvalidParameters = 6,
    InvalidResourceSpecified = 7,
    OperationFailed = 8,
    InvalidSessionKey = 9,
    InvalidApiKey = 10,
    ServiceOffline = 11,
    SubscribersOnly = 12,
    InvalidMethodSignature = 13,
    TemporaryError = 16,
    SuspendedApiKey = 26,
    RateLimitExceeded = 29,
}

/// <summary>
/// Decides what a failed Last.fm call means for the caller: try again later, ask the user to
/// reconnect, or give up on this particular request. Kept free of HTTP/WinRT dependencies so it
/// can be unit tested directly (see Trdo.Tests).
/// </summary>
internal static class LastfmErrorPolicy
{
    /// <summary>
    /// Whether a failed request is worth retrying later. Transient service trouble is; a request
    /// that was wrong (bad params, bad key) will be wrong again no matter how many times it is
    /// resent, and Last.fm's own scrobbling guidelines call out exactly this distinction.
    /// </summary>
    public static bool IsRetryable(LastfmErrorCode code) => code switch
    {
        LastfmErrorCode.ServiceOffline => true,
        LastfmErrorCode.TemporaryError => true,
        LastfmErrorCode.RateLimitExceeded => true,

        // Retryable only after the session is replaced - see RequiresReauth. The queue keeps
        // the item rather than discarding it, since the fix is on the user, not the data.
        LastfmErrorCode.InvalidSessionKey => true,

        _ => false,
    };

    /// <summary>
    /// Whether this failure means the stored session key no longer works and the user must
    /// reconnect before anything else will succeed.
    /// </summary>
    public static bool RequiresReauth(LastfmErrorCode code) => code == LastfmErrorCode.InvalidSessionKey;
}
