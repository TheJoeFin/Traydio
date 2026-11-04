using System.Collections.Generic;

namespace Trdo.Models;

/// <summary>
/// One or more Radio Browser entries that share a station name - typically the same station
/// registered more than once with a different stream URL (a relay, a backup feed, a second
/// codec). Grouping them keeps a search for a popular station from listing the same name five
/// times in a row; every stream stays reachable underneath the shared header.
/// </summary>
public sealed class StationSearchResultGroup(IReadOnlyList<RadioBrowserStation> streams)
{
    /// <summary>Every entry sharing this station name, best quality (highest bitrate) first.</summary>
    public IReadOnlyList<RadioBrowserStation> Streams { get; } = streams;

    /// <summary>The stream whose details head the card - the group's best bitrate, then votes.</summary>
    public RadioBrowserStation Primary => Streams[0];

    public string Name => Primary.Name;

    public string Country => Primary.Country;

    public string Tags => Primary.Tags;

    public string Favicon => Primary.Favicon;

    public bool HasMultipleStreams => Streams.Count > 1;

    public string StreamCountLabel => $"{Streams.Count} streams";
}
