namespace Trdo.Services.Lastfm;

/// <summary>
/// Outcome of one Last.fm API call: either a value, or enough information about the failure to
/// decide whether it is worth retrying. Used instead of exceptions because a failed scrobble
/// call is routine (offline, rate limited, session revoked) rather than exceptional, and the
/// queue needs to branch on exactly which of those happened.
/// </summary>
internal readonly struct LastfmResult<T>
{
    public bool IsSuccess { get; }

    /// <summary>
    /// The result value. Only meaningful when <see cref="IsSuccess"/> is true - on failure this
    /// is <c>default(T)</c>, which callers must not read.
    /// </summary>
    /// <remarks>
    /// Deliberately plain <c>T</c>, not <c>T?</c>: for an unconstrained <typeparamref name="T"/>,
    /// <c>T?</c> becomes <c>Nullable&lt;T&gt;</c> when <typeparamref name="T"/> is instantiated
    /// with a value type (as it is for the tuple results below), and a nullable-wrapped tuple's
    /// named elements are not directly accessible without first unwrapping it.
    /// </remarks>
    public T Value { get; }

    public LastfmErrorCode? ErrorCode { get; }
    public string? ErrorMessage { get; }

    private LastfmResult(bool isSuccess, T value, LastfmErrorCode? errorCode, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static LastfmResult<T> Success(T value) => new(true, value, null, null);

    public static LastfmResult<T> Failure(LastfmErrorCode? errorCode, string? errorMessage) =>
        new(false, default, errorCode, errorMessage);

    /// <summary>
    /// Whether this failure is worth retrying later. A transport-level failure (no error code at
    /// all - offline, timeout, an unparseable response) is treated as retryable by default: it
    /// says nothing about the request itself, only that this attempt could not reach Last.fm.
    /// </summary>
    public bool IsRetryable =>
        !IsSuccess && (!ErrorCode.HasValue || LastfmErrorPolicy.IsRetryable(ErrorCode.Value));

    public bool RequiresReauth =>
        !IsSuccess && ErrorCode.HasValue && LastfmErrorPolicy.RequiresReauth(ErrorCode.Value);
}
