using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Trdo.Services.Playback;

/// <summary>
/// Finds renderers (a Chromecast, say) on the local network through LibVLC's renderer
/// discovery and keeps <see cref="Devices"/> current for as long as it runs.
/// <para>
/// This is deliberately not a background service. An mDNS listener holds a multicast socket
/// open and wakes on every announcement on the LAN, a poor trade for a tray app that casts
/// rarely. The picker creates one of these when it opens, and disposing it stops every
/// discoverer, closes the socket and releases every item it found, so nothing is left
/// running or allocated once the dialog is gone.
/// </para>
/// </summary>
public sealed class CastRendererDiscovery : IDisposable
{
    private const string Component = "CastRendererDiscovery";

    private readonly LibVLC _libVlc;
    private readonly DispatcherQueue _uiQueue;
    private readonly List<RendererDiscoverer> _discoverers = [];
    private CastDevice? _claimed;
    private bool _isDisposed;

    private CastRendererDiscovery(LibVLC libVlc, DispatcherQueue uiQueue, ObservableCollection<CastDevice> devices)
    {
        _libVlc = libVlc;
        _uiQueue = uiQueue;
        Devices = devices;
    }

    /// <summary>
    /// The picker's list, shared with <see cref="SonosDiscovery"/>. Only ever changed on the
    /// UI thread. This discovery adds and removes only the rows it found itself.
    /// </summary>
    public ObservableCollection<CastDevice> Devices { get; }

    /// <summary>
    /// Null when LibVLC failed to load on this machine, in which case LibVLC renderers
    /// (Chromecast) cannot be found or cast to. Sonos does not depend on it.
    /// </summary>
    public static CastRendererDiscovery? TryCreate(DispatcherQueue uiQueue, ObservableCollection<CastDevice> devices) =>
        LibVlcHost.Instance is { } libVlc ? new CastRendererDiscovery(libVlc, uiQueue, devices) : null;

    /// <summary>
    /// Starts every discovery protocol this LibVLC build offers (mDNS for Chromecast on the
    /// Windows build). False when none could be started, so the picker can say so instead
    /// of searching forever.
    /// </summary>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        RendererDescription[] protocols;
        try
        {
            protocols = _libVlc.RendererList;
        }
        catch (Exception ex)
        {
            LogService.Error(Component, "Could not list LibVLC's renderer discovery protocols", ex);
            return false;
        }

        foreach (RendererDescription protocol in protocols)
        {
            RendererDiscoverer? discoverer = null;
            try
            {
                discoverer = new RendererDiscoverer(_libVlc, protocol.Name);
                discoverer.ItemAdded += OnItemAdded;
                discoverer.ItemDeleted += OnItemDeleted;

                if (!discoverer.Start())
                {
                    LogService.Warn(Component,
                        $"Renderer discovery '{protocol.Name}' ({protocol.LongName}) failed to start");
                    Detach(discoverer);
                    discoverer.Dispose();
                    continue;
                }

                _discoverers.Add(discoverer);
                LogService.Info(Component, $"Renderer discovery '{protocol.Name}' ({protocol.LongName}) started");
            }
            catch (Exception ex)
            {
                LogService.Error(Component, $"Renderer discovery '{protocol.Name}' threw on start", ex);
                if (discoverer is not null)
                {
                    Detach(discoverer);
                    discoverer.Dispose();
                }
            }
        }

        if (_discoverers.Count == 0)
        {
            LogService.Warn(Component, "No renderer discovery protocol is available; casting cannot find devices");
        }

        return _discoverers.Count > 0;
    }

    /// <summary>
    /// Hands the device's LibVLC handle to the caller, who becomes responsible for disposing
    /// it. The discovery stops treating it as its own, so disposing the discovery afterwards
    /// leaves the item alive for the player.
    /// </summary>
    public RendererItem Claim(CastDevice device)
    {
        _claimed = device;
        return device.Item ?? throw new InvalidOperationException("A Sonos row has no LibVLC handle to claim.");
    }

    // LibVLC raises these on its own thread. Everything that touches Devices is moved onto
    // the UI thread, and nothing here calls back into LibVLC from the callback (see
    // LibVlcPlaybackBackend.RaiseOffVlcThread for why that matters).
    private void OnItemAdded(object? sender, RendererDiscovererItemAddedEventArgs e)
    {
        RendererItem item = e.RendererItem;
        if (!_uiQueue.TryEnqueue(() => AddOnUiThread(item)))
        {
            item.Dispose();
        }
    }

    private void OnItemDeleted(object? sender, RendererDiscovererItemDeletedEventArgs e)
    {
        RendererItem item = e.RendererItem;
        if (!_uiQueue.TryEnqueue(() => RemoveOnUiThread(item)))
        {
            item.Dispose();
        }
    }

    private void AddOnUiThread(RendererItem item)
    {
        if (_isDisposed || !CastDevicePolicy.ShouldOffer(item.CanRenderAudio) || IndexOf(item) >= 0)
        {
            Debug.WriteLine($"[{Component}] Ignoring renderer '{item.Name}' (disposed={_isDisposed}, audio={item.CanRenderAudio})");
            item.Dispose();
            return;
        }

        CastDevice device = new(item);
        List<string> names = new(Devices.Count);
        foreach (CastDevice existing in Devices)
        {
            names.Add(existing.Name);
        }

        Devices.Insert(CastDevicePolicy.InsertionIndex(names, device.Name), device);
        LogService.Info(Component,
            $"Found renderer '{device.Name}' ({device.Kind}, audio={item.CanRenderAudio}, video={item.CanRenderVideo})");
    }

    private void RemoveOnUiThread(RendererItem item)
    {
        try
        {
            if (_isDisposed)
            {
                return;
            }

            int index = IndexOf(item);
            if (index < 0)
            {
                return;
            }

            CastDevice device = Devices[index];
            Devices.RemoveAt(index);
            LogService.Info(Component, $"Renderer '{device.Name}' left the network");

            if (!ReferenceEquals(device, _claimed))
            {
                device.Item?.Dispose();
            }
        }
        finally
        {
            // The event's own wrapper is separate from the one stored in the list and holds
            // its own reference, so it is released regardless of whether it matched anything.
            item.Dispose();
        }
    }

    private int IndexOf(RendererItem item)
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].Matches(item))
            {
                return i;
            }
        }

        return -1;
    }

    private void Detach(RendererDiscoverer discoverer)
    {
        discoverer.ItemAdded -= OnItemAdded;
        discoverer.ItemDeleted -= OnItemDeleted;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        foreach (RendererDiscoverer discoverer in _discoverers)
        {
            Detach(discoverer);
            try
            {
                discoverer.Stop();
                discoverer.Dispose();
            }
            catch (Exception ex)
            {
                LogService.Warn(Component, $"Error stopping renderer discovery: {ex.Message}");
            }
        }

        _discoverers.Clear();

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            CastDevice device = Devices[i];
            if (device.Item is null)
            {
                continue; // a Sonos row, owned by SonosDiscovery
            }

            if (!ReferenceEquals(device, _claimed))
            {
                device.Item.Dispose();
            }

            Devices.RemoveAt(i);
        }
        LogService.Info(Component, "Renderer discovery stopped");
    }
}
