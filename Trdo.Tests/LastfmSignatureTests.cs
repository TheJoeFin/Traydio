using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using Trdo.Services.Lastfm;

namespace Trdo.Tests;

/// <summary>
/// Verifies <see cref="LastfmSignature"/> against MD5 vectors computed independently (via
/// System.Security.Cryptography, outside this test) from Last.fm's documented signing rule:
/// sort parameters alphabetically by name (excluding <c>format</c>), concatenate as
/// <c>name+value</c> pairs, append the shared secret, UTF-8 encode, MD5 hash.
/// </summary>
[TestClass]
public sealed class LastfmSignatureTests
{
    [TestMethod]
    public void Compute_MatchesKnownVector()
    {
        Dictionary<string, string> parameters = new()
        {
            ["method"] = "auth.getToken",
            ["api_key"] = "b25b959554ed76058ac220b7b2e0a026",
        };

        string signature = LastfmSignature.Compute(parameters, "myAppSecretString");

        Assert.AreEqual("af6592eab0277dcb43b29d04653f7d1b", signature);
    }

    [TestMethod]
    public void Compute_ExcludesFormatParameter()
    {
        Dictionary<string, string> withoutFormat = new()
        {
            ["method"] = "auth.getToken",
            ["api_key"] = "b25b959554ed76058ac220b7b2e0a026",
        };
        Dictionary<string, string> withFormat = new(withoutFormat)
        {
            ["format"] = "json",
        };

        string signatureWithoutFormat = LastfmSignature.Compute(withoutFormat, "myAppSecretString");
        string signatureWithFormat = LastfmSignature.Compute(withFormat, "myAppSecretString");

        Assert.AreEqual(signatureWithoutFormat, signatureWithFormat);
        Assert.AreEqual("af6592eab0277dcb43b29d04653f7d1b", signatureWithFormat);
    }

    [TestMethod]
    public void Compute_SortsParametersAlphabeticallyRegardlessOfInputOrder()
    {
        Dictionary<string, string> inOrder = new()
        {
            ["api_key"] = "b25b959554ed76058ac220b7b2e0a026",
            ["method"] = "auth.getSession",
            ["sk"] = "tok_ABC123",
            ["token"] = "mysecret999",
        };
        Dictionary<string, string> shuffled = new()
        {
            ["token"] = "mysecret999",
            ["sk"] = "tok_ABC123",
            ["method"] = "auth.getSession",
            ["api_key"] = "b25b959554ed76058ac220b7b2e0a026",
        };

        Assert.AreEqual("4b6db4b05f6ea3a873bb806724ffc05d", LastfmSignature.Compute(inOrder, ""));
        Assert.AreEqual(LastfmSignature.Compute(inOrder, ""), LastfmSignature.Compute(shuffled, ""));
    }

    [TestMethod]
    public void Compute_EncodesNonAsciiValuesAsUtf8()
    {
        Dictionary<string, string> parameters = new()
        {
            ["api_key"] = "abc",
            ["artist"] = "Café Tacvba",
            ["method"] = "track.scrobble",
            ["track"] = "Test",
        };

        string signature = LastfmSignature.Compute(parameters, "s3cr3t");

        Assert.AreEqual("a0a2a81e4381dbe6a9f9db52540ddb02", signature);
    }
}
