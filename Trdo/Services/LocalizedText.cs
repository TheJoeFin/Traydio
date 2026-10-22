using System;
using System.Globalization;

namespace Trdo.Services;

/// <summary>
/// Platform-independent access to UI text. Plain policy tests use the English fallback;
/// the Windows app supplies its resource lookup before loading the interface.
/// </summary>
public static class LocalizedText
{
    private static Func<string, string, string>? _resolver;

    /// <summary>Registers the resource lookup used by the Windows application.</summary>
    public static void SetResolver(Func<string, string, string> resolver) => _resolver = resolver;

    /// <summary>Gets a resource value, or the fallback when no resolver is installed.</summary>
    public static string Get(string key, string fallback) => _resolver?.Invoke(key, fallback) ?? fallback;

    /// <summary>
    /// Formats text using the current culture in the application, or invariant culture
    /// when no resolver is installed.
    /// </summary>
    public static string Format(string key, string fallback, params object[] values)
    {
        var resolver = _resolver;
        // The English fallback preserves the policies' culture-independent output.
        // The Windows app supplies a resolver and keeps the user's number formatting.
        return string.Format(
            resolver is null ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture,
            resolver?.Invoke(key, fallback) ?? fallback,
            values);
    }
}
