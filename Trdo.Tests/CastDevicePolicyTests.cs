using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Services.Playback;

namespace Trdo.Tests;

/// <summary>
/// Covers how discovered renderers are filtered, labelled and ordered for the cast picker.
/// <para>
/// The list is built incrementally as mDNS answers trickle in, so the insertion rule has to
/// keep it sorted on its own, and a device announced on two interfaces must collapse to one
/// row rather than appearing twice.
/// </para>
/// </summary>
[TestClass]
public sealed class CastDevicePolicyTests
{
    [TestMethod]
    public void ShouldOffer_OnlyAudioCapableRenderers()
    {
        Assert.IsTrue(CastDevicePolicy.ShouldOffer(canRenderAudio: true));
        Assert.IsFalse(CastDevicePolicy.ShouldOffer(canRenderAudio: false));
    }

    [TestMethod]
    public void DisplayName_TrimsAndFallsBackWhenBlank()
    {
        Assert.AreEqual("Living Room", CastDevicePolicy.DisplayName("  Living Room "));
        Assert.AreEqual(CastDevicePolicy.FallbackDeviceName, CastDevicePolicy.DisplayName(null));
        Assert.AreEqual(CastDevicePolicy.FallbackDeviceName, CastDevicePolicy.DisplayName("   "));
    }

    [TestMethod]
    public void DescribeKind_KnowsChromecastRegardlessOfCase()
    {
        Assert.AreEqual("Chromecast", CastDevicePolicy.DescribeKind("chromecast"));
        Assert.AreEqual("Chromecast", CastDevicePolicy.DescribeKind("CHROMECAST"));
    }

    [TestMethod]
    public void DescribeKind_LabelsUpnpFamily()
    {
        Assert.AreEqual("UPnP / DLNA", CastDevicePolicy.DescribeKind("upnp_renderer"));
        Assert.AreEqual("UPnP / DLNA", CastDevicePolicy.DescribeKind("dlna"));
    }

    [TestMethod]
    public void DescribeKind_CapitalizesUnknownTypesAndFallsBackWhenBlank()
    {
        Assert.AreEqual("Airplay", CastDevicePolicy.DescribeKind("airplay"));
        Assert.AreEqual(CastDevicePolicy.FallbackKind, CastDevicePolicy.DescribeKind(null));
        Assert.AreEqual(CastDevicePolicy.FallbackKind, CastDevicePolicy.DescribeKind(""));
    }

    [TestMethod]
    public void InsertionIndex_KeepsListAlphabeticalIgnoringCase()
    {
        string[] sorted = ["Bedroom", "kitchen", "Office"];

        Assert.AreEqual(0, CastDevicePolicy.InsertionIndex(sorted, "Attic"));
        Assert.AreEqual(1, CastDevicePolicy.InsertionIndex(sorted, "Den"));
        Assert.AreEqual(2, CastDevicePolicy.InsertionIndex(sorted, "Living Room"));
        Assert.AreEqual(3, CastDevicePolicy.InsertionIndex(sorted, "Porch"));
    }

    [TestMethod]
    public void InsertionIndex_TiesGoAfterExistingEntry()
    {
        string[] sorted = ["Kitchen"];

        Assert.AreEqual(1, CastDevicePolicy.InsertionIndex(sorted, "kitchen"));
    }

    [TestMethod]
    public void InsertionIndex_EmptyListIsZero()
    {
        Assert.AreEqual(0, CastDevicePolicy.InsertionIndex([], "Anything"));
    }

    [TestMethod]
    public void IsSameDevice_MatchesNameAndKindIgnoringCase()
    {
        Assert.IsTrue(CastDevicePolicy.IsSameDevice("Kitchen", "chromecast", "kitchen ", "Chromecast"));
        Assert.IsFalse(CastDevicePolicy.IsSameDevice("Kitchen", "chromecast", "Kitchen", "airplay"));
        Assert.IsFalse(CastDevicePolicy.IsSameDevice("Kitchen", "chromecast", "Office", "chromecast"));
    }
}
