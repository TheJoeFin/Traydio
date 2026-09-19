using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using Trdo.Models;
using Trdo.Services.Playback;

namespace Trdo.Tests;

/// <summary>
/// Covers the pure half of Sonos casting: which address a speaker is handed for a given
/// stream, the SOAP and DIDL documents sent to it, and how its replies are read.
/// </summary>
[TestClass]
public sealed class SonosPolicyTests
{
    [TestMethod]
    public void TransportUriCandidates_RadioOverHttp_TriesRadioSchemeThenPlain()
    {
        IReadOnlyList<string> candidates = SonosPolicy.TransportUriCandidates("http://stream.example.com:8000/live", AudioSourceKind.Radio);

        CollectionAssert.AreEqual(
            new[] { "x-rincon-mp3radio://stream.example.com:8000/live", "http://stream.example.com:8000/live" },
            candidates.ToArray());
    }

    [TestMethod]
    public void TransportUriCandidates_RadioOverHttps_KeepsSchemeInsideRadioUri()
    {
        IReadOnlyList<string> candidates = SonosPolicy.TransportUriCandidates("https://stream.example.com/live.mp3", AudioSourceKind.Radio);

        Assert.AreEqual("x-rincon-mp3radio://https://stream.example.com/live.mp3", candidates[0]);
        Assert.AreEqual("https://stream.example.com/live.mp3", candidates[1]);
    }

    [TestMethod]
    public void TransportUriCandidates_HlsAndLocalFiles_UsePlainAddressOnly()
    {
        IReadOnlyList<string> hls = SonosPolicy.TransportUriCandidates("https://cdn.example.com/radio/master.m3u8?token=1", AudioSourceKind.Radio);
        IReadOnlyList<string> file = SonosPolicy.TransportUriCandidates("http://192.168.1.10:5123/media/abc.mp3", AudioSourceKind.Files);

        Assert.HasCount(1, hls);
        Assert.AreEqual("https://cdn.example.com/radio/master.m3u8?token=1", hls[0]);
        Assert.HasCount(1, file);
        Assert.AreEqual("http://192.168.1.10:5123/media/abc.mp3", file[0]);
    }

    [TestMethod]
    public void TransportUriCandidates_BlankIsEmpty()
    {
        Assert.IsEmpty(SonosPolicy.TransportUriCandidates("  ", AudioSourceKind.Radio));
    }

    [TestMethod]
    public void ToRadioUri_LeavesAnExistingRadioUriAlone()
    {
        Assert.AreEqual("x-rincon-mp3radio://host/x", SonosPolicy.ToRadioUri("x-rincon-mp3radio://host/x"));
    }

    [TestMethod]
    public void BuildDidlLite_RadioIsBroadcastWithArtAndEscapedTitle()
    {
        string didl = SonosPolicy.BuildDidlLite("Rock & Roll <FM>", "https://example.com/logo.png", AudioSourceKind.Radio, "x-rincon-mp3radio://host/live");

        StringAssert.Contains(didl, "object.item.audioItem.audioBroadcast");
        StringAssert.Contains(didl, "Rock &amp; Roll &lt;FM&gt;");
        StringAssert.Contains(didl, "https://example.com/logo.png");
        StringAssert.Contains(didl, "x-rincon-mp3radio://host/live");
    }

    [TestMethod]
    public void BuildDidlLite_LocalFileIsMusicTrackAndSkipsNonHttpArt()
    {
        string didl = SonosPolicy.BuildDidlLite("Track", "C:\\music\\cover.jpg", AudioSourceKind.Files, "http://pc:1/media/a.mp3");

        StringAssert.Contains(didl, "object.item.audioItem.musicTrack");
        Assert.IsFalse(didl.Contains("albumArtURI", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildSoapEnvelope_EscapesArgumentValues()
    {
        string envelope = SonosPolicy.BuildSoapEnvelope(SonosPolicy.AvTransportNamespace, "SetAVTransportURI",
        [
            new("InstanceID", "0"),
            new("CurrentURIMetaData", "<DIDL-Lite a=\"1\"/>"),
        ]);

        StringAssert.Contains(envelope, "<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">");
        StringAssert.Contains(envelope, "<CurrentURIMetaData>&lt;DIDL-Lite a=&quot;1&quot;/&gt;</CurrentURIMetaData>");
        StringAssert.Contains(envelope, "<InstanceID>0</InstanceID>");
    }

    [TestMethod]
    public void ParseSearchResponseLocation_AcceptsSonosReplyOnly()
    {
        string sonos = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age = 1800\r\nLOCATION: http://192.168.1.20:1400/xml/device_description.xml\r\n" +
                       "ST: urn:schemas-upnp-org:device:ZonePlayer:1\r\nUSN: uuid:RINCON_000E58AABBCC01400::urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n";
        string other = "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:80/rootDesc.xml\r\nST: upnp:rootdevice\r\nUSN: uuid:1234::upnp:rootdevice\r\n\r\n";
        string notify = "NOTIFY * HTTP/1.1\r\nLOCATION: http://192.168.1.20:1400/xml/device_description.xml\r\nST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n";

        Assert.AreEqual("http://192.168.1.20:1400/xml/device_description.xml", SonosPolicy.ParseSearchResponseLocation(sonos));
        Assert.IsNull(SonosPolicy.ParseSearchResponseLocation(other));
        Assert.IsNull(SonosPolicy.ParseSearchResponseLocation(notify));
        Assert.IsNull(SonosPolicy.ParseSearchResponseLocation(string.Empty));
    }

    [TestMethod]
    public void ParseZoneGroups_ListsCoordinatorsWithVisibleRoomsOnly()
    {
        const string xml =
            "<ZoneGroupState><ZoneGroups>" +
            "<ZoneGroup Coordinator=\"RINCON_A\" ID=\"RINCON_A:1\">" +
            "<ZoneGroupMember UUID=\"RINCON_A\" Location=\"http://192.168.1.20:1400/xml/device_description.xml\" ZoneName=\"Kitchen\" />" +
            "<ZoneGroupMember UUID=\"RINCON_B\" Location=\"http://192.168.1.21:1400/xml/device_description.xml\" ZoneName=\"Living Room\" />" +
            "<ZoneGroupMember UUID=\"RINCON_C\" Location=\"http://192.168.1.22:1400/xml/device_description.xml\" ZoneName=\"Living Room\" Invisible=\"1\" />" +
            "</ZoneGroup>" +
            "<ZoneGroup Coordinator=\"RINCON_D\" ID=\"RINCON_D:2\">" +
            "<ZoneGroupMember UUID=\"RINCON_D\" Location=\"http://192.168.1.23:1400/xml/device_description.xml\" ZoneName=\"Office\" />" +
            "</ZoneGroup>" +
            "<ZoneGroup Coordinator=\"RINCON_E\" ID=\"RINCON_E:3\">" +
            "<ZoneGroupMember UUID=\"RINCON_E\" Location=\"http://192.168.1.24:1400/xml/device_description.xml\" ZoneName=\"Sub\" Invisible=\"1\" />" +
            "</ZoneGroup>" +
            "</ZoneGroups></ZoneGroupState>";

        IReadOnlyList<SonosZoneGroup> groups = SonosPolicy.ParseZoneGroups(xml);

        Assert.HasCount(2, groups);
        Assert.AreEqual("RINCON_A", groups[0].CoordinatorUuid);
        Assert.AreEqual("http://192.168.1.20:1400/xml/device_description.xml", groups[0].CoordinatorLocation);
        CollectionAssert.AreEqual(new[] { "Kitchen", "Living Room" }, groups[0].RoomNames.ToArray());
        Assert.AreEqual("RINCON_D", groups[1].CoordinatorUuid);
        Assert.AreEqual("Kitchen + Living Room", SonosPolicy.GroupDisplayName(groups[0].RoomNames));
        Assert.AreEqual("Sonos group", SonosPolicy.GroupKind(groups[0].RoomNames.Count));
        Assert.AreEqual("Sonos", SonosPolicy.GroupKind(1));
    }

    [TestMethod]
    public void ParseZoneGroups_TruncatedXmlIsEmpty()
    {
        Assert.IsEmpty(SonosPolicy.ParseZoneGroups("<ZoneGroupState><ZoneGroups><ZoneGroup Coordinator=\"X\""));
    }

    [TestMethod]
    public void GroupDisplayName_SummarisesLargeGroups()
    {
        Assert.AreEqual("Kitchen + 3 rooms", SonosPolicy.GroupDisplayName(["Kitchen", "Den", "Hall", "Loft"]));
        Assert.AreEqual("Kitchen + Den + Hall", SonosPolicy.GroupDisplayName(["Kitchen", "Den", "Hall"]));
        Assert.AreEqual(CastDevicePolicy.FallbackDeviceName, SonosPolicy.GroupDisplayName([]));
    }

    [TestMethod]
    public void ReadSoapValue_IgnoresNamespaces()
    {
        const string response =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
            "<u:GetTransportInfoResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
            "<CurrentTransportState>PLAYING</CurrentTransportState><CurrentTransportStatus>OK</CurrentTransportStatus>" +
            "</u:GetTransportInfoResponse></s:Body></s:Envelope>";

        Assert.AreEqual("PLAYING", SonosPolicy.ReadSoapValue(response, "CurrentTransportState"));
        Assert.IsNull(SonosPolicy.ReadSoapValue(response, "Missing"));
        Assert.AreEqual(SonosTransportState.Playing, SonosPolicy.ParseTransportState("PLAYING"));
        Assert.AreEqual(SonosTransportState.PausedPlayback, SonosPolicy.ParseTransportState("paused_playback"));
        Assert.AreEqual(SonosTransportState.Unknown, SonosPolicy.ParseTransportState(null));
    }

    [TestMethod]
    public void ReadUpnpErrorCode_ReadsFaultBody()
    {
        const string fault =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault>" +
            "<faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
            "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>714</errorCode><errorDescription>Illegal MIME-Type</errorDescription></UPnPError>" +
            "</detail></s:Fault></s:Body></s:Envelope>";

        Assert.AreEqual(714, SonosPolicy.ReadUpnpErrorCode(fault));
        Assert.IsNull(SonosPolicy.ReadUpnpErrorCode("<x/>"));
    }

    [TestMethod]
    public void ParseTrackTime_RoundTripsAndRejectsPlaceholders()
    {
        Assert.AreEqual(new TimeSpan(0, 3, 21), SonosPolicy.ParseTrackTime("0:03:21"));
        Assert.AreEqual(new TimeSpan(1, 2, 3), SonosPolicy.ParseTrackTime("1:02:03"));
        Assert.IsNull(SonosPolicy.ParseTrackTime("NOT_IMPLEMENTED"));
        Assert.IsNull(SonosPolicy.ParseTrackTime(""));
        Assert.AreEqual("0:03:21", SonosPolicy.FormatTrackTime(new TimeSpan(0, 3, 21)));
        Assert.AreEqual("0:00:00", SonosPolicy.FormatTrackTime(TimeSpan.FromSeconds(-5)));
    }

    [TestMethod]
    public void ParsePositionInfo_ReadsRadioStreamContent()
    {
        const string response =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
            "<u:GetPositionInfoResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
            "<Track>1</Track><TrackDuration>0:00:00</TrackDuration>" +
            "<TrackMetaData>&lt;DIDL-Lite xmlns:dc=&quot;http://purl.org/dc/elements/1.1/&quot; xmlns:upnp=&quot;urn:schemas-upnp-org:metadata-1-0/upnp/&quot; xmlns:r=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot; xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot;&gt;" +
            "&lt;item id=&quot;-1&quot; parentID=&quot;-1&quot; restricted=&quot;true&quot;&gt;&lt;res protocolInfo=&quot;x-rincon-mp3radio:*:*:*&quot;&gt;x-rincon-mp3radio://host/live&lt;/res&gt;" +
            "&lt;r:streamContent&gt;Daft Punk - Around the World&lt;/r:streamContent&gt;&lt;dc:title&gt;live&lt;/dc:title&gt;&lt;upnp:class&gt;object.item&lt;/upnp:class&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;</TrackMetaData>" +
            "<TrackURI>x-rincon-mp3radio://host/live</TrackURI><RelTime>0:12:34</RelTime><AbsTime>NOT_IMPLEMENTED</AbsTime>" +
            "</u:GetPositionInfoResponse></s:Body></s:Envelope>";

        SonosPositionInfo info = SonosPolicy.ParsePositionInfo(response);

        Assert.AreEqual(new TimeSpan(0, 12, 34), info.Position);
        Assert.IsNull(info.Duration);
        Assert.AreEqual("x-rincon-mp3radio://host/live", info.TrackUri);
        Assert.AreEqual("Daft Punk - Around the World", info.StreamContent);
        Assert.AreEqual("live", info.Title);
    }

    [TestMethod]
    public void ParsePositionInfo_LocalTrackHasDuration()
    {
        const string response =
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
            "<u:GetPositionInfoResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
            "<TrackDuration>0:04:05</TrackDuration><TrackMetaData>NOT_IMPLEMENTED</TrackMetaData><RelTime>0:00:10</RelTime>" +
            "</u:GetPositionInfoResponse></s:Body></s:Envelope>";

        SonosPositionInfo info = SonosPolicy.ParsePositionInfo(response);

        Assert.AreEqual(new TimeSpan(0, 4, 5), info.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), info.Position);
        Assert.IsNull(info.StreamContent);
    }

    [TestMethod]
    public void CleanStreamContent_HandlesStationBookkeepingFormats()
    {
        Assert.AreEqual("Artist - Song", SonosPolicy.CleanStreamContent("  Artist - Song "));
        Assert.AreEqual("Band - Song", SonosPolicy.CleanStreamContent("TYPE=SNG|TITLE Song|ARTIST Band|ALBUM X"));
        Assert.AreEqual("Song", SonosPolicy.CleanStreamContent("TYPE=SNG|TITLE Song"));
        Assert.IsNull(SonosPolicy.CleanStreamContent("TYPE=ADV|DURATION 30"));
        Assert.AreEqual("Hello", SonosPolicy.CleanStreamContent("text=\"Hello\""));
        Assert.IsNull(SonosPolicy.CleanStreamContent(""));
        Assert.IsNull(SonosPolicy.CleanStreamContent(null));
    }

    [TestMethod]
    public void IsNaturalEnd_OnlyForLocalFilesThatStopped()
    {
        Assert.IsTrue(SonosPolicy.IsNaturalEnd(AudioSourceKind.Files, SonosTransportState.Stopped));
        Assert.IsTrue(SonosPolicy.IsNaturalEnd(AudioSourceKind.Files, SonosTransportState.NoMediaPresent));
        Assert.IsFalse(SonosPolicy.IsNaturalEnd(AudioSourceKind.Files, SonosTransportState.PausedPlayback));
        Assert.IsFalse(SonosPolicy.IsNaturalEnd(AudioSourceKind.Radio, SonosTransportState.Stopped));
    }

    [TestMethod]
    public void BuildSearchRequest_IsWellFormedSsdp()
    {
        string request = SonosPolicy.BuildSearchRequest("239.255.255.250", 1900, 2);

        StringAssert.StartsWith(request, "M-SEARCH * HTTP/1.1\r\n");
        StringAssert.Contains(request, "HOST: 239.255.255.250:1900\r\n");
        StringAssert.Contains(request, "MAN: \"ssdp:discover\"\r\n");
        StringAssert.Contains(request, "ST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n");
        StringAssert.EndsWith(request, "\r\n\r\n");
    }
}
