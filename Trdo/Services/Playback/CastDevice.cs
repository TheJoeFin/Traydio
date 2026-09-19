using LibVLCSharp.Shared;

namespace Trdo.Services.Playback;

/// <summary>
/// A device the cast picker lists: either a renderer LibVLC found on the network (a
/// Chromecast) or a Sonos player found by <see cref="SonosDiscovery"/>. Exactly one of
/// <see cref="Item"/> and <see cref="Sonos"/> is set.
/// </summary>
public sealed class CastDevice
{
    internal CastDevice(RendererItem item)
    {
        Item = item;
        Name = CastDevicePolicy.DisplayName(item.Name);
        Kind = CastDevicePolicy.DescribeKind(item.Type);
    }

    internal CastDevice(SonosDevice sonos)
    {
        Sonos = sonos;
        Name = CastDevicePolicy.DisplayName(sonos.Name);
        Kind = sonos.Kind;
    }

    /// <summary>
    /// The LibVLC handle. Owned by the <see cref="CastRendererDiscovery"/> that found it until
    /// the device is claimed, after which the player owns it. Null for a Sonos row.
    /// </summary>
    internal RendererItem? Item { get; }

    /// <summary>The Sonos player, or null for a LibVLC renderer.</summary>
    public SonosDevice? Sonos { get; }

    public bool IsSonos => Sonos is not null;

    public string Name { get; }

    /// <summary>What sort of device this is, e.g. "Chromecast" or "Sonos".</summary>
    public string Kind { get; }

    /// <summary>Segoe Fluent glyph for the row: a speaker for Sonos, a generic device otherwise.</summary>
    public string Glyph => IsSonos ? "" : "";

    /// <summary>
    /// Whether a later discovery report refers to this device. The native pointer is the
    /// reliable test; the name/type comparison catches a re-announcement on another interface.
    /// </summary>
    internal bool Matches(RendererItem other) =>
        Item is not null &&
        (other.NativeReference == Item.NativeReference ||
         CastDevicePolicy.IsSameDevice(Item.Name, Item.Type, other.Name, other.Type));
}
