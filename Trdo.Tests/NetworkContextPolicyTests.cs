using System;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Services.Playback;

namespace Trdo.Tests;

[TestClass]
public sealed class NetworkContextPolicyTests
{
    [TestMethod]
    public void CountsAsOtherLan_RealEthernetWithGateway()
    {
        Assert.IsTrue(NetworkContextPolicy.CountsAsOtherLan(NetworkInterfaceType.Ethernet, "Ethernet",
            "Intel(R) Ethernet Connection I219-V", hasIpv4Gateway: true, hasRoutableIpv4Address: true));
    }

    [TestMethod]
    public void CountsAsOtherLan_HostOnlySwitchWithoutGatewayIsIgnored()
    {
        Assert.IsFalse(NetworkContextPolicy.CountsAsOtherLan(NetworkInterfaceType.Ethernet, "Ethernet 3",
            "Some Adapter", hasIpv4Gateway: false, hasRoutableIpv4Address: true));
    }

    [TestMethod]
    public void CountsAsOtherLan_VirtualAdaptersAreIgnoredEvenWithGateway()
    {
        Assert.IsFalse(NetworkContextPolicy.CountsAsOtherLan(NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)",
            "Hyper-V Virtual Ethernet Adapter", hasIpv4Gateway: true, hasRoutableIpv4Address: true));
        Assert.IsFalse(NetworkContextPolicy.CountsAsOtherLan(NetworkInterfaceType.Ethernet, "Ethernet 2",
            "TAP-Windows Adapter V9", hasIpv4Gateway: true, hasRoutableIpv4Address: true));
    }

    [TestMethod]
    public void CountsAsOtherLan_LinkLocalOnlyDoesNotCount()
    {
        Assert.IsFalse(NetworkContextPolicy.CountsAsOtherLan(NetworkInterfaceType.Ethernet, "Ethernet",
            "Realtek", hasIpv4Gateway: true, hasRoutableIpv4Address: NetworkContextPolicy.IsRoutableIpv4(IPAddress.Parse("169.254.3.4"))));
    }

    [TestMethod]
    public void Ipv4Subnet_MasksHostBits()
    {
        Assert.AreEqual("192.168.1.0/24", NetworkContextPolicy.Ipv4Subnet(IPAddress.Parse("192.168.1.57"), 24));
        Assert.AreEqual("10.4.0.0/20", NetworkContextPolicy.Ipv4Subnet(IPAddress.Parse("10.4.15.9"), 20));
    }

    [TestMethod]
    public void Signature_IgnoresHostAddressChangesWithinSubnet()
    {
        Guid adapter = Guid.NewGuid();
        string first = NetworkContextPolicy.BuildSignature("k", adapter, false,
            [NetworkContextPolicy.Ipv4Subnet(IPAddress.Parse("192.168.1.20"), 24)], ["192.168.1.1"]);
        string renewed = NetworkContextPolicy.BuildSignature("k", adapter, false,
            [NetworkContextPolicy.Ipv4Subnet(IPAddress.Parse("192.168.1.44"), 24)], ["192.168.1.1"]);
        Assert.AreEqual(first, renewed);
    }

    [TestMethod]
    public void Signature_ChangesWhenSubnetGatewayKeyOrLanChanges()
    {
        Guid adapter = Guid.NewGuid();
        string baseline = NetworkContextPolicy.BuildSignature("k", adapter, false, ["192.168.1.0/24"], ["192.168.1.1"]);
        Assert.AreNotEqual(baseline, NetworkContextPolicy.BuildSignature("k", adapter, false, ["10.0.0.0/24"], ["192.168.1.1"]));
        Assert.AreNotEqual(baseline, NetworkContextPolicy.BuildSignature("k", adapter, false, ["192.168.1.0/24"], ["192.168.1.254"]));
        Assert.AreNotEqual(baseline, NetworkContextPolicy.BuildSignature("other", adapter, false, ["192.168.1.0/24"], ["192.168.1.1"]));
        Assert.AreNotEqual(baseline, NetworkContextPolicy.BuildSignature("k", adapter, true, ["192.168.1.0/24"], ["192.168.1.1"]));
    }
}
