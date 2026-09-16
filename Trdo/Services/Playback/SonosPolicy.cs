using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Trdo.Models;

namespace Trdo.Services.Playback;

/// <summary>What the speaker's AVTransport service reports it is doing.</summary>
public enum SonosTransportState
{
    Unknown,
    Stopped,
    Playing,
    PausedPlayback,
    Transitioning,
    NoMediaPresent,
}

/// <summary>One row of a Sonos household's zone group topology: a group and who leads it.</summary>
public sealed record SonosZoneGroup(
    string CoordinatorUuid,
    string CoordinatorLocation,
    IReadOnlyList<string> RoomNames);

/// <summary>A snapshot of the speaker's position report for the current track.</summary>
public sealed record SonosPositionInfo(
    TimeSpan Position,
    TimeSpan? Duration,
    string? TrackUri,
    string? StreamContent,
    string? Title);

/// <summary>
/// Pure decisions and parsing for talking to a Sonos speaker over UPnP: which URI to hand the
/// speaker for a given stream, how to describe it in DIDL-Lite, and how to read the XML the
/// speaker sends back. Free of WinUI and sockets so it can be unit tested.
/// </summary>
public static class SonosPolicy
{
    public const string ZonePlayerSearchTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";
    public const string RadioScheme = "x-rincon-mp3radio://";
    public const string SonosUdnPrefix = "uuid:RINCON_";
    public const string AvTransportNamespace = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string RenderingControlNamespace = "urn:schemas-upnp-org:service:RenderingControl:1";
    public const string ZoneGroupTopologyNamespace = "urn:schemas-upnp-org:service:ZoneGroupTopology:1";

    private static readonly XNamespace Didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace Rincon = "urn:schemas-rinconnetworks-com:metadata-1-0/";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// The URIs worth trying for a stream, best first. A live radio stream goes through the
    /// speaker's radio decoder (the x-rincon-mp3radio scheme), which is what copes with
    /// Icecast/Shoutcast servers and ICY titles; if the speaker rejects that form, the plain
    /// address is offered next. HLS playlists and local files are the other way round: the
    /// radio decoder cannot read either, so the plain address leads.
    /// </summary>
    public static IReadOnlyList<string> TransportUriCandidates(string streamUrl, AudioSourceKind sourceKind)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return [];
        }

        string trimmed = streamUrl.Trim();
        if (sourceKind == AudioSourceKind.Files || LooksLikeHlsPlaylist(trimmed))
        {
            return [trimmed];
        }

        string radioUri = ToRadioUri(trimmed);
        return radioUri == trimmed ? [trimmed] : [radioUri, trimmed];
    }

    /// <summary>
    /// Wraps an http(s) address in the radio scheme. The scheme is dropped for plain http,
    /// which is the form every firmware understands; https is kept in full because the
    /// speaker would otherwise fall back to http and be refused by a TLS-only server.
    /// </summary>
    public static string ToRadioUri(string url)
    {
        if (url.StartsWith(RadioScheme, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return RadioScheme + url["http://".Length..];
        }

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return RadioScheme + url;
        }

        return url;
    }

    public static bool LooksLikeHlsPlaylist(string url)
    {
        int query = url.IndexOfAny(['?', '#']);
        string path = query >= 0 ? url[..query] : url;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The DIDL-Lite the speaker shows in the Sonos app for what it is playing. A radio
    /// station is an audioBroadcast so the app labels it as a station and shows the ICY
    /// title beneath; a local file is a musicTrack with its own duration.
    /// </summary>
    public static string BuildDidlLite(string title, string? albumArtUrl, AudioSourceKind sourceKind, string resourceUri)
    {
        bool isBroadcast = sourceKind != AudioSourceKind.Files;
        string safeTitle = string.IsNullOrWhiteSpace(title) ? "Traydio" : title.Trim();

        XElement item = new(Didl + "item",
            new XAttribute("id", "-1"),
            new XAttribute("parentID", "-1"),
            new XAttribute("restricted", "true"),
            new XElement(Dc + "title", safeTitle),
            new XElement(Upnp + "class", isBroadcast ? "object.item.audioItem.audioBroadcast" : "object.item.audioItem.musicTrack"));

        if (!string.IsNullOrWhiteSpace(albumArtUrl) &&
            Uri.TryCreate(albumArtUrl, UriKind.Absolute, out Uri? art) &&
            (art.Scheme == Uri.UriSchemeHttp || art.Scheme == Uri.UriSchemeHttps))
        {
            item.Add(new XElement(Upnp + "albumArtURI", art.AbsoluteUri));
        }

        item.Add(new XElement(Didl + "res",
            new XAttribute("protocolInfo", isBroadcast ? "x-rincon-mp3radio:*:*:*" : "http-get:*:audio/*:*"),
            resourceUri));

        XElement root = new(Didl + "DIDL-Lite",
            new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "upnp", Upnp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", Rincon.NamespaceName),
            item);

        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The M-SEARCH datagram that asks every Sonos player on the LAN to answer.</summary>
    public static string BuildSearchRequest(string multicastHost, int port, int maxWaitSeconds) =>
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {multicastHost}:{port}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        $"MX: {maxWaitSeconds}\r\n" +
        $"ST: {ZonePlayerSearchTarget}\r\n" +
        "\r\n";

    /// <summary>
    /// The device-description URL out of an SSDP reply, or null when the datagram is not a
    /// successful reply from a Sonos player (anything else on the LAN answers M-SEARCH too).
    /// </summary>
    public static string? ParseSearchResponseLocation(string datagram)
    {
        if (string.IsNullOrEmpty(datagram))
        {
            return null;
        }

        string[] lines = datagram.Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) ||
            !lines[0].Contains(" 200", StringComparison.Ordinal))
        {
            return null;
        }

        string? location = null;
        string? searchTarget = null;
        string? usn = null;
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
            {
                location = value;
            }
            else if (name.Equals("ST", StringComparison.OrdinalIgnoreCase))
            {
                searchTarget = value;
            }
            else if (name.Equals("USN", StringComparison.OrdinalIgnoreCase))
            {
                usn = value;
            }
        }

        bool isSonos =
            (searchTarget is not null && searchTarget.Equals(ZonePlayerSearchTarget, StringComparison.OrdinalIgnoreCase)) ||
            (usn is not null && usn.StartsWith(SonosUdnPrefix, StringComparison.OrdinalIgnoreCase));

        if (!isSonos || location is null || !Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    /// <summary>
    /// Reads the groups out of a ZoneGroupState document (the inner XML, already unescaped
    /// from its SOAP envelope). Members flagged invisible - the second half of a stereo
    /// pair, a Sub, a bonded surround - are left out of the room list because the Sonos app
    /// does not name them either. Groups whose coordinator is itself invisible are skipped.
    /// </summary>
    public static IReadOnlyList<SonosZoneGroup> ParseZoneGroups(string zoneGroupStateXml)
    {
        List<SonosZoneGroup> groups = [];
        if (string.IsNullOrWhiteSpace(zoneGroupStateXml))
        {
            return groups;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(zoneGroupStateXml);
        }
        catch (Exception)
        {
            return groups;
        }

        foreach (XElement group in document.Descendants("ZoneGroup"))
        {
            string coordinator = (string?)group.Attribute("Coordinator") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(coordinator))
            {
                continue;
            }

            string? coordinatorLocation = null;
            bool coordinatorInvisible = false;
            List<string> rooms = [];
            foreach (XElement member in group.Elements("ZoneGroupMember"))
            {
                string uuid = (string?)member.Attribute("UUID") ?? string.Empty;
                bool invisible = ((string?)member.Attribute("Invisible")) == "1";
                string? zoneName = (string?)member.Attribute("ZoneName");

                if (uuid.Equals(coordinator, StringComparison.OrdinalIgnoreCase))
                {
                    coordinatorLocation = (string?)member.Attribute("Location");
                    coordinatorInvisible = invisible;
                }

                if (!invisible && !string.IsNullOrWhiteSpace(zoneName) &&
                    !rooms.Contains(zoneName, StringComparer.OrdinalIgnoreCase))
                {
                    rooms.Add(zoneName.Trim());
                }
            }

            if (coordinatorInvisible || string.IsNullOrWhiteSpace(coordinatorLocation) || rooms.Count == 0)
            {
                continue;
            }

            groups.Add(new SonosZoneGroup(coordinator, coordinatorLocation, rooms));
        }

        return groups;
    }

    /// <summary>How a group is named in the picker: "Kitchen", "Kitchen + Living Room", or "Kitchen + 3 rooms".</summary>
    public static string GroupDisplayName(IReadOnlyList<string> roomNames)
    {
        if (roomNames.Count == 0)
        {
            return CastDevicePolicy.FallbackDeviceName;
        }

        if (roomNames.Count == 1)
        {
            return roomNames[0];
        }

        if (roomNames.Count <= 3)
        {
            return string.Join(" + ", roomNames);
        }

        return $"{roomNames[0]} + {roomNames.Count - 1} rooms";
    }

    public static string GroupKind(int roomCount) => roomCount > 1 ? "Sonos group" : "Sonos";

    /// <summary>The value of the first element named <paramref name="localName"/> in a SOAP response, in any namespace.</summary>
    public static string? ReadSoapValue(string responseXml, string localName)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            return null;
        }

        try
        {
            XDocument document = XDocument.Parse(responseXml);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName == localName)
                {
                    return element.Value;
                }
            }
        }
        catch (Exception)
        {
            // Fall through: a truncated reply reads as "no value" rather than a crash.
        }

        return null;
    }

    /// <summary>The UPnP error code out of a SOAP fault body, or null when the body is not a fault.</summary>
    public static int? ReadUpnpErrorCode(string responseXml)
    {
        string? code = ReadSoapValue(responseXml, "errorCode");
        return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    public static SonosTransportState ParseTransportState(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "STOPPED" => SonosTransportState.Stopped,
        "PLAYING" => SonosTransportState.Playing,
        "PAUSED_PLAYBACK" => SonosTransportState.PausedPlayback,
        "TRANSITIONING" => SonosTransportState.Transitioning,
        "NO_MEDIA_PRESENT" => SonosTransportState.NoMediaPresent,
        _ => SonosTransportState.Unknown,
    };

    /// <summary>
    /// Sonos reports times as H:MM:SS. "NOT_IMPLEMENTED" and blanks come back for live
    /// streams, which have no duration.
    /// </summary>
    public static TimeSpan? ParseTrackTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Trim().Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return null;
        }

        return new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
    }

    public static string FormatTrackTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}",
            (int)time.TotalHours, time.Minutes, time.Seconds);
    }

    /// <summary>
    /// Reads a GetPositionInfo response. The ICY title of a radio stream lives inside the
    /// escaped TrackMetaData DIDL as r:streamContent; a local file's title is dc:title.
    /// </summary>
    public static SonosPositionInfo ParsePositionInfo(string responseXml)
    {
        TimeSpan position = ParseTrackTime(ReadSoapValue(responseXml, "RelTime")) ?? TimeSpan.Zero;
        TimeSpan? duration = ParseTrackTime(ReadSoapValue(responseXml, "TrackDuration"));
        if (duration is { } d && d <= TimeSpan.Zero)
        {
            duration = null;
        }

        string? trackUri = ReadSoapValue(responseXml, "TrackURI");
        string? streamContent = null;
        string? title = null;

        string? metadata = ReadSoapValue(responseXml, "TrackMetaData");
        if (!string.IsNullOrWhiteSpace(metadata) && !metadata.Equals("NOT_IMPLEMENTED", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                XDocument didl = XDocument.Parse(metadata);
                foreach (XElement element in didl.Descendants())
                {
                    if (element.Name.LocalName == "streamContent")
                    {
                        streamContent = element.Value;
                    }
                    else if (element.Name.LocalName == "title")
                    {
                        title = element.Value;
                    }
                }
            }
            catch (Exception)
            {
                // Some firmware emits DIDL that is not well-formed; the position is still good.
            }
        }

        return new SonosPositionInfo(position, duration, trackUri, CleanStreamContent(streamContent), title);
    }

    /// <summary>
    /// The speaker relays the raw ICY title, which some stations pad with their own
    /// bookkeeping ("TYPE=SNG|TITLE Song|ARTIST Band", "text=..."). Only a plain title is
    /// worth showing; anything else is dropped rather than shown as gibberish.
    /// </summary>
    public static string? CleanStreamContent(string? streamContent)
    {
        if (string.IsNullOrWhiteSpace(streamContent))
        {
            return null;
        }

        string text = streamContent.Trim();
        if (text.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase))
        {
            string? artist = null;
            string? title = null;
            foreach (string part in text.Split('|'))
            {
                if (part.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                {
                    title = part[6..].Trim();
                }
                else if (part.StartsWith("ARTIST ", StringComparison.OrdinalIgnoreCase))
                {
                    artist = part[7..].Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
        }

        if (text.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].Trim().Trim('"');
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Whether a speaker that stopped on its own should be read as "the track finished". Only
    /// a local file has an end to reach; a radio stream that stops has dropped.
    /// </summary>
    public static bool IsNaturalEnd(AudioSourceKind sourceKind, SonosTransportState state) =>
        sourceKind == AudioSourceKind.Files && state is SonosTransportState.Stopped or SonosTransportState.NoMediaPresent;

    /// <summary>
    /// Builds a SOAP envelope for a UPnP action. Arguments are escaped, so a DIDL document
    /// can be passed straight through as a value.
    /// </summary>
    public static string BuildSoapEnvelope(string serviceNamespace, string action, IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        StringBuilder builder = new(512);
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<s:Envelope xmlns:s=\"").Append(Soap.NamespaceName).Append("\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        builder.Append("<s:Body>");
        builder.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(serviceNamespace).Append("\">");
        foreach ((string name, string value) in arguments)
        {
            builder.Append('<').Append(name).Append('>');
            builder.Append(EscapeXml(value));
            builder.Append("</").Append(name).Append('>');
        }

        builder.Append("</u:").Append(action).Append('>');
        builder.Append("</s:Body></s:Envelope>");
        return builder.ToString();
    }

    public static string EscapeXml(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length + 16);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }
}
