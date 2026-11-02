using System;
using System.Collections.Generic;
using System.Net;

namespace Trdo.Services.Playback;

/// <summary>
/// A Sonos player (or the coordinator of a group of them) that the cast picker can send a
/// stream to. Holds nothing but addresses, so it can be kept and compared freely.
/// </summary>
public sealed class SonosDevice
{
    public SonosDevice(
        string uuid,
        string name,
        string kind,
        IPAddress address,
        int port,
        IReadOnlyList<string>? rooms = null,
        IReadOnlyList<string>? roomUuids = null)
    {
        Uuid = uuid;
        Name = name;
        Kind = kind;
        Address = address;
        Port = port;
        Rooms = rooms ?? [];
        RoomUuids = roomUuids ?? new[] { uuid };
    }

    /// <summary>The rooms playing together under this target. Empty when only a name is known.</summary>
    public IReadOnlyList<string> Rooms { get; }

    /// <summary>
    /// UUIDs of every visible room in the group, the coordinator included. A speaker that was
    /// cast to on its own is now reached through whichever group it has since been put into.
    /// </summary>
    public IReadOnlyList<string> RoomUuids { get; }

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
            location.Port,
            group.RoomNames,
            group.RoomUuids);
    }

    /// <summary>Builds a device for one room from the address a topology report gives for it, or null if unreadable.</summary>
    public static SonosDevice? FromRoom(SonosRoom room)
    {
        if (!Uri.TryCreate(room.Location, UriKind.Absolute, out Uri? location) ||
            !IPAddress.TryParse(location.Host, out IPAddress? address))
        {
            return null;
        }

        return new SonosDevice(room.Uuid, room.Name, "Sonos", address, location.Port,
            new[] { room.Name }, new[] { room.Uuid });
    }

    public bool SameTargetAs(SonosDevice? other) =>
        other is not null && string.Equals(Uuid, other.Uuid, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name} ({Address}:{Port})";
}
