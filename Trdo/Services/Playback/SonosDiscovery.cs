using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Trdo.Services.Playback;

/// <summary>
/// Finds Sonos players on the local network and keeps them in the cast picker's list for as
/// long as it runs. Players answer an SSDP M-SEARCH; any one of them then reports the whole
/// household's grouping, so a single topology call turns the answers into the rooms and
/// groups the Sonos app itself would show, with each group listed once under its coordinator.
/// <para>
/// Like <see cref="CastRendererDiscovery"/> this lives only while the picker is open. The
/// search is repeated every few seconds so a player that wakes up or a group that changes
/// shows up without reopening the dialog, and disposing stops the sockets and clears the
/// rows this discovery added.
/// </para>
/// </summary>
public sealed class SonosDiscovery : IDisposable
{
    private const string Component = "SonosDiscovery";
    private const int SsdpPort = 1900;
    private static readonly IPAddress SsdpMulticastAddress = IPAddress.Parse("239.255.255.250");
    private static readonly TimeSpan ListenWindow = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RoundInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue _uiQueue;
    private readonly ObservableCollection<CastDevice> _devices;
    private readonly SonosController _controller = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;

    public SonosDiscovery(DispatcherQueue uiQueue, ObservableCollection<CastDevice> devices)
    {
        _uiQueue = uiQueue;
        _devices = devices;
    }

    /// <summary>Begins searching. False when no network interface could be used at all.</summary>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        List<IPAddress> interfaces = LocalIPv4Addresses();
        if (interfaces.Count == 0)
        {
            LogService.Warn(Component, "No IPv4 network interface is up; cannot search for Sonos players");
            return false;
        }

        _ = RunAsync(_cts.Token);
        LogService.Info(Component, $"Sonos search started on {interfaces.Count} interface(s)");
        return true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTime roundStart = DateTime.UtcNow;
                try
                {
                    List<string> locations = await SearchAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<SonosZoneGroup>? groups = await ResolveTopologyAsync(locations, cancellationToken).ConfigureAwait(false);
                    if (groups is not null)
                    {
                        Reconcile(groups);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.Warn(Component, $"Search round failed: {ex.Message}");
                }

                TimeSpan elapsed = DateTime.UtcNow - roundStart;
                TimeSpan wait = RoundInterval - elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One M-SEARCH on every interface, then a short listen for replies. Returns the distinct device-description URLs that answered.</summary>
    private static async Task<List<string>> SearchAsync(CancellationToken cancellationToken)
    {
        List<UdpClient> sockets = [];
        byte[] request = Encoding.ASCII.GetBytes(SonosPolicy.BuildSearchRequest(SsdpMulticastAddress.ToString(), SsdpPort, 2));
        IPEndPoint destination = new(SsdpMulticastAddress, SsdpPort);

        foreach (IPAddress local in LocalIPv4Addresses())
        {
            UdpClient? socket = null;
            try
            {
                socket = new UdpClient(new IPEndPoint(local, 0));
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                await socket.SendAsync(request, destination, cancellationToken).ConfigureAwait(false);
                sockets.Add(socket);
            }
            catch (Exception ex)
            {
                LogService.Warn(Component, $"M-SEARCH on {local} failed: {ex.Message}");
                socket?.Dispose();
            }
        }

        HashSet<string> locations = new(StringComparer.OrdinalIgnoreCase);
        if (sockets.Count == 0)
        {
            return [.. locations];
        }

        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(ListenWindow);
        Lock gate = new();

        Task[] listeners = new Task[sockets.Count];
        for (int i = 0; i < sockets.Count; i++)
        {
            UdpClient socket = sockets[i];
            listeners[i] = Task.Run(async () =>
            {
                while (!window.IsCancellationRequested)
                {
                    try
                    {
                        UdpReceiveResult result = await socket.ReceiveAsync(window.Token).ConfigureAwait(false);
                        string? location = SonosPolicy.ParseSearchResponseLocation(Encoding.UTF8.GetString(result.Buffer));
                        if (location is not null)
                        {
                            lock (gate)
                            {
                                locations.Add(location);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        // An ICMP "port unreachable" from a host that did not like the
                        // multicast surfaces here; keep listening for the ones that did.
                    }
                }
            }, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            foreach (UdpClient socket in sockets)
            {
                socket.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return [.. locations];
    }

    /// <summary>
    /// Asks the first reachable responder for the household topology. Null when nobody
    /// answered the search at all, so the list is left alone rather than emptied; an empty
    /// list when players answered but none of them would describe the household.
    /// </summary>
    private async Task<IReadOnlyList<SonosZoneGroup>?> ResolveTopologyAsync(List<string> locations, CancellationToken cancellationToken)
    {
        if (locations.Count == 0)
        {
            return null;
        }

        Dictionary<string, SonosZoneGroup> merged = new(StringComparer.OrdinalIgnoreCase);
        bool anyAnswered = false;

        foreach (string location in locations)
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? uri))
            {
                continue;
            }

            string topologyUrl = $"http://{uri.Host}:{uri.Port}/ZoneGroupTopology/Control";
            try
            {
                string xml = await _controller.GetZoneGroupStateAsync(topologyUrl, cancellationToken).ConfigureAwait(false);
                anyAnswered = true;
                foreach (SonosZoneGroup group in SonosPolicy.ParseZoneGroups(xml))
                {
                    merged[group.CoordinatorUuid] = group;
                }

                // One household answers for every one of its players; a second responder
                // only matters if it belongs to a different household, which the merge
                // above already covers if it shows up. Stop after the first good answer.
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Warn(Component, $"Topology query to {uri.Host} failed: {ex.Message}");
            }
        }

        if (!anyAnswered)
        {
            return null;
        }

        return [.. merged.Values];
    }

    private void Reconcile(IReadOnlyList<SonosZoneGroup> groups)
    {
        List<SonosDevice> desired = [];
        foreach (SonosZoneGroup group in groups)
        {
            if (SonosDevice.FromZoneGroup(group) is { } device)
            {
                desired.Add(device);
            }
        }

        if (!_uiQueue.TryEnqueue(() => ReconcileOnUiThread(desired)))
        {
            LogService.Warn(Component, "UI queue is gone; dropping a discovery result");
        }
    }

    private void ReconcileOnUiThread(List<SonosDevice> desired)
    {
        if (_isDisposed)
        {
            return;
        }

        // Rows this discovery owns that are no longer in the household, or whose name or
        // address changed (a regrouping), come out; anything new goes in sorted.
        for (int i = _devices.Count - 1; i >= 0; i--)
        {
            if (_devices[i].Sonos is not { } existing)
            {
                continue;
            }

            SonosDevice? match = desired.Find(d => d.SameTargetAs(existing));
            if (match is null ||
                !string.Equals(match.Name, existing.Name, StringComparison.Ordinal) ||
                !match.Address.Equals(existing.Address) ||
                match.Port != existing.Port)
            {
                LogService.Info(Component, $"Sonos '{existing.Name}' {(match is null ? "left the network" : "changed")}");
                _devices.RemoveAt(i);
            }
        }

        foreach (SonosDevice device in desired)
        {
            bool present = false;
            foreach (CastDevice row in _devices)
            {
                if (row.Sonos is { } sonos && sonos.SameTargetAs(device))
                {
                    present = true;
                    break;
                }
            }

            if (present)
            {
                continue;
            }

            CastDevice cast = new(device);
            List<string> names = new(_devices.Count);
            foreach (CastDevice row in _devices)
            {
                names.Add(row.Name);
            }

            _devices.Insert(CastDevicePolicy.InsertionIndex(names, cast.Name), cast);
            LogService.Info(Component, $"Found Sonos '{device.Name}' ({device.Kind}) at {device.Address}:{device.Port}");
        }
    }

    private static List<IPAddress> LocalIPv4Addresses()
    {
        List<IPAddress> result = [];
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    !adapter.SupportsMulticast)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    IPAddress address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    byte[] bytes = address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue; // link-local: no router, no Sonos
                    }

                    result.Add(address);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(Component, $"Could not enumerate network interfaces: {ex.Message}");
        }

        return result;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _controller.Dispose();

        for (int i = _devices.Count - 1; i >= 0; i--)
        {
            if (_devices[i].Sonos is not null)
            {
                _devices.RemoveAt(i);
            }
        }

        LogService.Info(Component, "Sonos search stopped");
    }
}
