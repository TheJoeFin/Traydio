using System;
using System.Net;

namespace Trdo.Services.Playback;

/// <summary>
/// A Sonos player (or the coordinator of a group of them) that the cast picker can send a
/// stream to. Holds nothing but addresses, so it can be kept and compared freely.
/// </summary>
public sealed class SonosDevice
{
    public SonosDevice(string uuid, string name, string kind, IPAddress address, int port)
    {
        Uuid = uuid;
        Name = name;
        Kind = kind;
        Address = address;
        Port = port;
    }

    /// <summary>The RINCON_… identifier of the coordinating player, without the "uuid:" prefix.</summary>
    public string Uuid { get; }

    /// <summary>Room name, or the joined room names of a group.</summary>
    public string Name { get; }

    /// <summary>"Sonos" or "Sonos group".</summary>
    public string Kind { get; }

    public IPAddress Address { get; }

    public int Port { get; }

    public string BaseUrl => $"http://{Address}:{Port}";

    public string AvTransportControlUrl => BaseUrl + "/MediaRenderer/AVTransport/Control";

    public string RenderingControlUrl => BaseUrl + "/MediaRenderer/RenderingControl/Control";

    public string ZoneGroupTopologyControlUrl => BaseUrl + "/ZoneGroupTopology/Control";

    /// <summary>
    /// Builds a device from the Location a topology report gives for the coordinator, or
    /// returns null when that address cannot be read.
    /// </summary>
    public static SonosDevice? FromZoneGroup(SonosZoneGroup group)
    {
        if (!Uri.TryCreate(group.CoordinatorLocation, UriKind.Absolute, out Uri? location) ||
            !IPAddress.TryParse(location.Host, out IPAddress? address))
        {
            return null;
        }

        return new SonosDevice(
            group.CoordinatorUuid,
            SonosPolicy.GroupDisplayName(group.RoomNames),
            SonosPolicy.GroupKind(group.RoomNames.Count),
            address,
            location.Port);
    }

    public bool SameTargetAs(SonosDevice? other) =>
        other is not null && string.Equals(Uuid, other.Uuid, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name} ({Address}:{Port})";
}
