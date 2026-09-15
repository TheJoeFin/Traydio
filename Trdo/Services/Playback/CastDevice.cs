using LibVLCSharp.Shared;

namespace Trdo.Services.Playback;

/// <summary>
/// A renderer LibVLC found on the network, as the cast picker lists it.
/// </summary>
public sealed class CastDevice
{
    internal CastDevice(RendererItem item)
    {
        Item = item;
        Name = CastDevicePolicy.DisplayName(item.Name);
        Kind = CastDevicePolicy.DescribeKind(item.Type);
    }

    /// <summary>
    /// The LibVLC handle. Owned by the <see cref="CastRendererDiscovery"/> that found it until
    /// the device is claimed, after which the player owns it.
    /// </summary>
    internal RendererItem Item { get; }

    public string Name { get; }

    /// <summary>What sort of device this is, e.g. "Chromecast".</summary>
    public string Kind { get; }

    /// <summary>
    /// Whether a later discovery report refers to this device. The native pointer is the
    /// reliable test; the name/type comparison catches a re-announcement on another interface.
    /// </summary>
    internal bool Matches(RendererItem other) =>
        other.NativeReference == Item.NativeReference ||
        CastDevicePolicy.IsSameDevice(Item.Name, Item.Type, other.Name, other.Type);
}
