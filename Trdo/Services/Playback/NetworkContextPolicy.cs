using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Trdo.Services.Playback;

/// <summary>
/// Pure rules for deciding what counts as "the network" for cast history, kept free of
/// adapter enumeration so they can be tested.
/// </summary>
public static class NetworkContextPolicy
{
    private static readonly string[] VirtualMarkers =
    [
        "vEthernet", "Hyper-V", "Virtual", "VirtualBox", "VMware", "VMnet", "TAP-", "TAP ",
        "WireGuard", "OpenVPN", "Wintun", "Tailscale", "ZeroTier", "Docker", "WSL", "Npcap",
        "Loopback", "Teredo", "ISATAP", "Pseudo", "Bluetooth",
    ];

    /// <summary>
    /// Whether an up adapter is a second real LAN that a renderer could be reached through.
    /// A real LAN has an IPv4 default gateway: Hyper-V's Default Switch, WSL2 and Docker
    /// host-side adapters are host-only and have none, so the gateway is the robust signal.
    /// The name check is a second filter for VPN/virtual adapters that do carry a gateway.
    /// </summary>
    public static bool CountsAsOtherLan(NetworkInterfaceType type, string? name, string? description,
        bool hasIpv4Gateway, bool hasRoutableIpv4Address) =>
        type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
        hasIpv4Gateway && hasRoutableIpv4Address && !LooksVirtual(name, description);

    public static bool LooksVirtual(string? name, string? description) =>
        VirtualMarkers.Any(marker =>
            (name?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (description?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false));

    public static bool IsRoutableIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;

        byte[] bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254) && !address.Equals(IPAddress.Any);
    }

    public static bool IsUsableGateway(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any);

    /// <summary>The subnet an address lives on ("192.168.1.0/24"), so a DHCP renewal inside it is not a move.</summary>
    public static string Ipv4Subnet(IPAddress address, int prefixLength)
    {
        byte[] bytes = address.GetAddressBytes();
        int prefix = Math.Clamp(prefixLength, 0, 32);
        for (int i = 0; i < bytes.Length; i++)
        {
            int bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return $"{new IPAddress(bytes)}/{prefix}";
    }

    /// <summary>
    /// Identifies the network, not the host: SSID key, adapter, IPv4 subnets and gateways.
    /// Host addresses (DHCP renewals, rotating IPv6 temporary addresses) are left out so they
    /// don't restart open pickers or invalidate a cast that just succeeded.
    /// </summary>
    public static string BuildSignature(string key, Guid adapterId, bool hasOtherLan,
        IEnumerable<string> subnets, IEnumerable<string> gateways) =>
        $"{key}:{adapterId}:{hasOtherLan}:" +
        $"{string.Join(",", subnets.Distinct().Order(StringComparer.Ordinal))}:" +
        $"{string.Join(",", gateways.Distinct().Order(StringComparer.Ordinal))}";
}
