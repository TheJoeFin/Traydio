using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Services.Lastfm;

namespace Trdo.Tests;

/// <summary>
/// Covers which Last.fm API error codes are worth retrying and which mean the stored session
/// key must be replaced before anything else can succeed.
/// </summary>
[TestClass]
public sealed class LastfmErrorPolicyTests
{
    [DataTestMethod]
    [DataRow(LastfmErrorCode.ServiceOffline)]
    [DataRow(LastfmErrorCode.TemporaryError)]
    [DataRow(LastfmErrorCode.RateLimitExceeded)]
    [DataRow(LastfmErrorCode.InvalidSessionKey)]
    public void IsRetryable_TrueForTransientOrReauthableFailures(LastfmErrorCode code)
    {
        Assert.IsTrue(LastfmErrorPolicy.IsRetryable(code));
    }

    [DataTestMethod]
    [DataRow(LastfmErrorCode.InvalidParameters)]
    [DataRow(LastfmErrorCode.InvalidApiKey)]
    [DataRow(LastfmErrorCode.InvalidMethod)]
    [DataRow(LastfmErrorCode.AuthenticationFailed)]
    [DataRow(LastfmErrorCode.InvalidMethodSignature)]
    public void IsRetryable_FalseForRequestsThatWillNeverSucceed(LastfmErrorCode code)
    {
        Assert.IsFalse(LastfmErrorPolicy.IsRetryable(code));
    }

    [TestMethod]
    public void RequiresReauth_TrueOnlyForInvalidSessionKey()
    {
        Assert.IsTrue(LastfmErrorPolicy.RequiresReauth(LastfmErrorCode.InvalidSessionKey));
        Assert.IsFalse(LastfmErrorPolicy.RequiresReauth(LastfmErrorCode.ServiceOffline));
        Assert.IsFalse(LastfmErrorPolicy.RequiresReauth(LastfmErrorCode.InvalidApiKey));
    }
}
