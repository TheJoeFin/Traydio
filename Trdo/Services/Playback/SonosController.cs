using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// The Sonos implementation in Traydio was heavily influenced by RoomRelay by guicn555
// (https://github.com/guicn555/RoomRelay, MIT): its SSDP discovery, UPnP SOAP control and
// RenderingControl volume handling are the model for what follows.

namespace Trdo.Services.Playback;

/// <summary>The speaker refused a UPnP action. <see cref="ErrorCode"/> is the UPnP error number when the reply carried one.</summary>
public sealed class SonosActionException : Exception
{
    public SonosActionException(string action, int? errorCode, string message)
        : base(message)
    {
        Action = action;
        ErrorCode = errorCode;
    }

    public string Action { get; }

    public int? ErrorCode { get; }
}

/// <summary>
/// The UPnP SOAP calls a Sonos player answers: AVTransport for what it plays,
/// RenderingControl for how loud, ZoneGroupTopology for who it is grouped with. Every call
/// is one short HTTP POST; nothing here keeps state, so one instance serves any player.
/// </summary>
public sealed class SonosController : IDisposable
{
    private const string Component = "SonosController";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpClient _http;

    public SonosController()
    {
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Traydio/2.1 (Windows)");
    }

    public async Task SetTransportUriAsync(SonosDevice device, string uri, string didlLite, CancellationToken cancellationToken)
    {
        await InvokeAsync(device.AvTransportControlUrl, SonosPolicy.AvTransportNamespace, "SetAVTransportURI",
        [
            new("InstanceID", "0"),
            new("CurrentURI", uri),
            new("CurrentURIMetaData", didlLite),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task PlayAsync(SonosDevice device, CancellationToken cancellationToken)
    {
        await InvokeAsync(device.AvTransportControlUrl, SonosPolicy.AvTransportNamespace, "Play",
        [
            new("InstanceID", "0"),
            new("Speed", "1"),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(SonosDevice device, CancellationToken cancellationToken)
    {
        await InvokeAsync(device.AvTransportControlUrl, SonosPolicy.AvTransportNamespace, "Pause",
        [
            new("InstanceID", "0"),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(SonosDevice device, CancellationToken cancellationToken)
    {
        await InvokeAsync(device.AvTransportControlUrl, SonosPolicy.AvTransportNamespace, "Stop",
        [
            new("InstanceID", "0"),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task SeekAsync(SonosDevice device, TimeSpan position, CancellationToken cancellationToken)
    {
        await InvokeAsync(device.AvTransportControlUrl, SonosPolicy.AvTransportNamespace, "Seek",
        [
            new("InstanceID", "0"),
            new("Unit", "REL_TIME"),
            new("Target", SonosPolicy.FormatTrackTime(position)),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task<SonosTransportState> GetTransportStateAsync(SonosDevice device, CancellationToken cancellationToken)
    {
        string response = await InvokeAsync(device.AvTransportControlUrl, SonosPolicy.AvTransportNamespace, "GetTransportInfo",
        [
            new("InstanceID", "0"),
        ], cancellationToken).ConfigureAwait(false);

        return SonosPolicy.ParseTransportState(SonosPolicy.ReadSoapValue(response, "CurrentTransportState"));
    }

    public async Task<SonosPositionInfo> GetPositionInfoAsync(SonosDevice device, CancellationToken cancellationToken)
    {
        string response = await InvokeAsync(device.AvTransportControlUrl, SonosPolicy.AvTransportNamespace, "GetPositionInfo",
        [
            new("InstanceID", "0"),
        ], cancellationToken).ConfigureAwait(false);

        return SonosPolicy.ParsePositionInfo(response);
    }

    public async Task<int> GetVolumeAsync(SonosDevice device, CancellationToken cancellationToken)
    {
        string response = await InvokeAsync(device.RenderingControlUrl, SonosPolicy.RenderingControlNamespace, "GetVolume",
        [
            new("InstanceID", "0"),
            new("Channel", "Master"),
        ], cancellationToken).ConfigureAwait(false);

        string? value = SonosPolicy.ReadSoapValue(response, "CurrentVolume");
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int volume))
        {
            throw new SonosActionException("GetVolume", null, "The speaker did not report its volume.");
        }

        return Math.Clamp(volume, 0, 100);
    }

    public async Task SetVolumeAsync(SonosDevice device, int volume, CancellationToken cancellationToken)
    {
        await InvokeAsync(device.RenderingControlUrl, SonosPolicy.RenderingControlNamespace, "SetVolume",
        [
            new("InstanceID", "0"),
            new("Channel", "Master"),
            new("DesiredVolume", Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture)),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> GetMuteAsync(SonosDevice device, CancellationToken cancellationToken)
    {
        string response = await InvokeAsync(device.RenderingControlUrl, SonosPolicy.RenderingControlNamespace, "GetMute",
        [
            new("InstanceID", "0"),
            new("Channel", "Master"),
        ], cancellationToken).ConfigureAwait(false);

        return SonosPolicy.ReadSoapValue(response, "CurrentMute")?.Trim() == "1";
    }

    public async Task SetMuteAsync(SonosDevice device, bool muted, CancellationToken cancellationToken)
    {
        await InvokeAsync(device.RenderingControlUrl, SonosPolicy.RenderingControlNamespace, "SetMute",
        [
            new("InstanceID", "0"),
            new("Channel", "Master"),
            new("DesiredMute", muted ? "1" : "0"),
        ], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The household's zone group topology as the inner ZoneGroupState XML. Any player in
    /// the household answers for all of them.
    /// </summary>
    public async Task<string> GetZoneGroupStateAsync(string zoneGroupTopologyControlUrl, CancellationToken cancellationToken)
    {
        string response = await InvokeAsync(zoneGroupTopologyControlUrl, SonosPolicy.ZoneGroupTopologyNamespace, "GetZoneGroupState",
            [], cancellationToken).ConfigureAwait(false);

        return SonosPolicy.ReadSoapValue(response, "ZoneGroupState") ?? string.Empty;
    }

    private async Task<string> InvokeAsync(
        string controlUrl,
        string serviceNamespace,
        string action,
        IReadOnlyList<KeyValuePair<string, string>> arguments,
        CancellationToken cancellationToken)
    {
        string envelope = SonosPolicy.BuildSoapEnvelope(serviceNamespace, action, arguments);

        using HttpRequestMessage request = new(HttpMethod.Post, controlUrl);
        request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceNamespace}#{action}\"");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SonosActionException(action, null, "The speaker did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new SonosActionException(action, null, $"The speaker could not be reached ({ex.Message}).");
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            int? code = SonosPolicy.ReadUpnpErrorCode(body);
            string description = DescribeError(code);
            LogService.Warn(Component, $"{action} failed: HTTP {(int)response.StatusCode}, UPnP error {code?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            throw new SonosActionException(action, code, description);
        }
    }

    /// <summary>
    /// The UPnP AVTransport error codes a listener can act on; the rest are shown by number.
    /// </summary>
    public static string DescribeError(int? code) => code switch
    {
        701 => "The speaker cannot do that in its current state.",
        702 => "The speaker reported an invalid instance.",
        710 => "The speaker does not support seeking in this stream.",
        711 => "The speaker could not seek to that position.",
        714 => "The speaker does not support this stream's format.",
        716 => "The speaker could not find the stream at that address.",
        800 => "The speaker is grouped with another player that controls playback.",
        null => "The speaker rejected the request.",
        _ => $"The speaker rejected the request (UPnP error {code}).",
    };

    public void Dispose() => _http.Dispose();
}
