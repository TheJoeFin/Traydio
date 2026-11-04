using System;
using System.Collections.Generic;

namespace Trdo.Services.Playback;

/// <summary>
/// Pure decisions about the renderers LibVLC discovers on the network: which ones are worth
/// offering to an audio-only app, what to call them, and where each lands in a list that is
/// kept sorted for display. Free of LibVLC and WinUI so it can be unit tested.
/// </summary>
public static class CastDevicePolicy
{
    public const string FallbackDeviceName = "Unknown device";
    public const string FallbackKind = "Media device";

    /// <summary>
    /// A radio app has nothing to show a video-only renderer; offering one would let the user
    /// pick a device that accepts the stream and plays silence.
    /// </summary>
    public static bool ShouldOffer(bool canRenderAudio) => canRenderAudio;

    public static string DisplayName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? FallbackDeviceName : name.Trim();

    /// <summary>
    /// Turns LibVLC's renderer type identifier into a label. Only Chromecast is known for
    /// certain; anything else is shown as LibVLC names it rather than guessed at.
    /// </summary>
    public static string DescribeKind(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return FallbackKind;
        }

        string trimmed = type.Trim();
        if (trimmed.Equals("chromecast", StringComparison.OrdinalIgnoreCase))
        {
            return "Chromecast";
        }

        if (trimmed.Contains("upnp", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("dlna", StringComparison.OrdinalIgnoreCase))
        {
            return "UPnP / DLNA";
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    public static int CompareForDisplay(string? left, string? right) =>
        string.Compare(DisplayName(left), DisplayName(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where <paramref name="newName"/> belongs in a list already ordered by
    /// <see cref="CompareForDisplay"/>. Ties go after the existing entry, so a device that
    /// was found first stays above a namesake found later.
    /// </summary>
    public static int InsertionIndex(IReadOnlyList<string> sortedNames, string newName)
    {
        int index = 0;
        while (index < sortedNames.Count && CompareForDisplay(sortedNames[index], newName) <= 0)
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Whether two discovery reports describe one physical device. mDNS can announce the
    /// same device once per network interface, and the list should not show it twice.
    /// </summary>
    public static bool IsSameDevice(string? nameA, string? typeA, string? nameB, string? typeB) =>
        string.Equals(DisplayName(nameA), DisplayName(nameB), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(DescribeKind(typeA), DescribeKind(typeB), StringComparison.OrdinalIgnoreCase);
}
