using System;
using System.Collections.Generic;
using System.Text.Json;
using Trdo.Models;

namespace Trdo.Services;

/// <summary>
/// The on-disk format for the station list: everything about turning stations into text and
/// back, with none of the file handling.
/// <para>
/// Split out from <see cref="RadioStationService"/> deliberately. That class needs
/// <c>Windows.Storage</c> for the app data folder and so cannot be loaded by a plain test
/// host, which left the format itself - the part with a backwards-compatibility contract
/// worth guarding - untestable. This half has no platform dependencies.
/// </para>
/// <para>
/// The compatibility contract: <c>stations.json</c> is a bare JSON array whose every element
/// deserialises cleanly as a <see cref="RadioStation"/>. It must stay that way. A pre-2.0
/// build that fails to parse the file treats it as empty and then overwrites it on quit, so
/// a format an older build cannot read does not degrade gracefully - it destroys the user's
/// stations. New properties may only ever be added as optional ones.
/// </para>
/// </summary>
public static class StationStorageFormat
{
    // Every call below passes a source-generated JsonTypeInfo rather than JsonSerializerOptions
    // carrying a resolver. Both work at runtime, but only this form is visible to the trimmer,
    // so a type that was never registered is a build warning here instead of an exception in a
    // Release build on a user's machine.

    /// <summary>
    /// Parses the contents of <c>stations.json</c>. Returns an empty list for empty input;
    /// throws for malformed JSON so the caller can decide whether to overwrite.
    /// </summary>
    public static List<RadioStation> ParseStations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize(json, RadioStationJsonContext.Default.ListRadioStation) ?? [];
    }

    /// <summary>
    /// Serialises the station list in the bare-array form described on this class.
    /// </summary>
    public static string SerializeStations(IEnumerable<RadioStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        return JsonSerializer.Serialize(
            [.. stations],
            RadioStationJsonContext.Default.ListRadioStation);
    }

    /// <summary>
    /// Parses the contents of <c>stationlayout.json</c>, returning null for anything it cannot
    /// make sense of.
    /// <para>
    /// Unlike the station file, an unreadable layout is not worth reporting: the layout only
    /// describes arrangement, so losing it costs the user their folders, not their stations,
    /// and reconciliation already handles a missing layout by producing a flat list.
    /// </para>
    /// </summary>
    public static StationLayoutDocument? ParseLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, RadioStationJsonContext.Default.StationLayoutDocument);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StationStorageFormat] Discarding unreadable layout: {ex.Message}");
            return null;
        }
    }

    /// <summary>Serialises the layout document.</summary>
    public static string SerializeLayout(StationLayoutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, RadioStationJsonContext.Default.StationLayoutDocument);
    }
}
