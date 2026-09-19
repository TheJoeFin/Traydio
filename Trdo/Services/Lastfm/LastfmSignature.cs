using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Computes the <c>api_sig</c> parameter Last.fm requires on every signed call: every parameter
/// except <c>format</c>, sorted alphabetically by name, concatenated as <c>name+value</c> pairs
/// with no separators, followed by the shared secret, UTF-8 encoded and MD5 hashed.
/// </summary>
/// <remarks>
/// Kept free of HTTP/WinRT dependencies so it can be unit tested directly against Last.fm's
/// documented example vectors (see Trdo.Tests).
/// </remarks>
internal static class LastfmSignature
{
    public static string Compute(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        List<string> keys = [.. parameters.Keys];
        keys.Sort(System.StringComparer.Ordinal);

        StringBuilder builder = new();
        foreach (string key in keys)
        {
            if (key == "format")
                continue;

            builder.Append(key).Append(parameters[key]);
        }

        builder.Append(secret);

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] hash = MD5.HashData(utf8Bytes);

        StringBuilder hex = new(hash.Length * 2);
        foreach (byte b in hash)
            hex.Append(b.ToString("x2"));

        return hex.ToString();
    }
}
