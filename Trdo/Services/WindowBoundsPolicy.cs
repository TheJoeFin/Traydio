using System;
using System.Globalization;

namespace Trdo.Services;

/// <summary>
/// Where a window was left. The position is in physical screen pixels, which is what puts it
/// back on the same monitor; the size is in logical (DIP) units so it looks the same if that
/// monitor's display scale has changed since.
/// </summary>
public readonly record struct SavedWindowBounds(int X, int Y, int Width, int Height)
{
    public string Serialize() =>
        string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");

    /// <summary>Reads what <see cref="Serialize"/> wrote. Anything else is treated as nothing saved.</summary>
    public static bool TryParse(string? text, out SavedWindowBounds bounds)
    {
        bounds = default;

        string[] parts = text?.Split(',') ?? [];
        if (parts.Length != 4)
            return false;

        Span<int> values = stackalloc int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        if (values[2] <= 0 || values[3] <= 0)
            return false;

        bounds = new SavedWindowBounds(values[0], values[1], values[2], values[3]);
        return true;
    }
}

/// <summary>
/// Sizing rules for windows whose size is remembered between openings.
/// </summary>
public static class WindowBoundsPolicy
{
    private const int BaseDpi = 96;

    /// <summary>
    /// Converts a physical pixel extent to logical units. Rounds down, the counterpart to
    /// placement rounding up, so a size saved and restored over and over does not creep.
    /// </summary>
    public static int ToLogical(int physical, uint dpi) =>
        dpi == 0 ? physical : (int)(physical * (long)BaseDpi / dpi);

    /// <summary>The size to open at, given what was saved. Zero or less means nothing was.</summary>
    public static int RestoreExtent(double saved, int fallback, int minimum)
    {
        if (!double.IsFinite(saved) || saved < 1)
            return fallback;

        return (int)Math.Clamp(saved, minimum, int.MaxValue);
    }
}
