using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Trdo.Services.Playback;

internal sealed class CastPickerSession : IDisposable
{
    private readonly CastingContextService _context = CastingContextService.Instance;
    private readonly DispatcherQueue _queue;
    private readonly ObservableCollection<CastDevice> _live = [];
    private readonly SonosController _probeController = new();
    private readonly SemaphoreSlim _probeGate = new(2);
    private CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool> _liveChanged = NewSignal();
    private CastRendererDiscovery? _renderers;
    private SonosDiscovery? _sonos;
    private long _generation;
    private bool _isChecking;
    private bool _isResetting;
    private bool _isDisposed;

    public ObservableCollection<CastDeviceEntry> Entries { get; } = [];
    public bool IsScanning => _renderers is not null || _sonos is not null;
    public event EventHandler? Changed;
    public event EventHandler? NetworkChanged;

    public CastPickerSession(DispatcherQueue queue)
    {
        _queue = queue;
        _live.CollectionChanged += Live_CollectionChanged;
        _context.Changed += Context_Changed;
        Restart();
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Context_Changed(object? sender, EventArgs e)
    {
        if (_generation != _context.NetworkGeneration)
        {
            Restart();
            NetworkChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Reconcile();
        }
    }

    private void Restart()
    {
        _isResetting = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _renderers?.Dispose();
        _sonos?.Dispose();
        _renderers = null;
        _sonos = null;
        _live.Clear();
        Entries.Clear();
        _generation = _context.NetworkGeneration;
        _lifetime = new CancellationTokenSource();
        _isChecking = true;
        _isResetting = false;
        Reconcile();

        _renderers = CastRendererDiscovery.TryCreate(_queue, _live);
        if (_renderers is not null && !_renderers.Start())
        {
            _renderers.Dispose();
            _renderers = null;
        }

        SonosDiscovery sonos = new(_queue, _live);
        try
        {
            if (sonos.Start())
                _sonos = sonos;
            else
                sonos.Dispose();
        }
        catch (Exception ex)
        {
            LogService.Error("CastPickerSession", "Sonos discovery could not start", ex);
            sonos.Dispose();
        }

        foreach (CastHistoryDevice device in _context.RememberedDevices)
        {
            if (device.Protocol == CastProtocol.Sonos)
                _ = ProbeAsync(device, _lifetime.Token);
        }

        _ = FinishInitialCheckAsync(_lifetime.Token);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task FinishInitialCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            _isChecking = false;
            Reconcile();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeAsync(CastHistoryDevice remembered, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await _probeGate.WaitAsync(cancellationToken);
            entered = true;
            if (remembered.Uuid is not { Length: > 0 } uuid || !IPAddress.TryParse(remembered.Address, out IPAddress? address))
                throw new FormatException("A remembered Sonos address is invalid.");

            SonosDevice hint = new(uuid, remembered.Name, remembered.Kind, address, remembered.Port);
            string xml = await _probeController.GetZoneGroupStateAsync(hint.ZoneGroupTopologyControlUrl, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (SonosZoneGroup group in SonosPolicy.ParseZoneGroups(xml))
            {
                if (SonosDevice.FromZoneGroup(group) is not { } device)
                    continue;

                if (string.Equals(device.Uuid, remembered.Uuid, StringComparison.OrdinalIgnoreCase))
                {
                    _live.Add(new CastDevice(device));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException
                                   or SonosActionException or System.Xml.XmlException or FormatException)
        {
            LogService.Warn("CastPickerSession", $"Remembered Sonos availability check failed: {ex.Message}");
        }
        finally
        {
            if (entered)
                _probeGate.Release();
        }
    }

    private void Live_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isResetting)
            Reconcile();
    }

    private void Reconcile()
    {
        if (_isDisposed)
            return;

        List<CastHistoryRow> desired = CastHistoryPolicy.Merge(_context.RememberedDevices,
            _live.Select(device => device.ToHistoryDevice()).ToList());
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (!desired.Exists(row => CastHistoryPolicy.Matches(row.Device, Entries[i].Device)))
                Entries.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            CastHistoryRow row = desired[i];
            CastDeviceEntry? entry = Entries.FirstOrDefault(existing => CastHistoryPolicy.Matches(existing.Device, row.Device));
            if (entry is null)
            {
                entry = new CastDeviceEntry(row);
                Entries.Insert(i, entry);
            }
            else if (Entries.IndexOf(entry) != i)
            {
                Entries.Move(Entries.IndexOf(entry), i);
            }

            entry.Update(row, _live.FirstOrDefault(device => CastHistoryPolicy.Matches(device.ToHistoryDevice(), row.Device)), _isChecking);
        }

        TaskCompletionSource<bool> signal = _liveChanged;
        _liveChanged = NewSignal();
        signal.TrySetResult(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CastDevice> ResolveAsync(CastDeviceEntry entry, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (entry.LiveDevice is { } live)
                    return live;
                await _liveChanged.Task.WaitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            _isChecking = false;
            Reconcile();
            throw new TimeoutException(string.Format(LocalizationService.GetString(
                "CastDeviceDialog_DeviceUnavailable", "{0} is not available on this network."), entry.Name));
        }
    }

    public RendererItem Claim(CastDevice device) =>
        _renderers?.Claim(device) ?? throw new InvalidOperationException("Renderer discovery is unavailable.");

    public void ForgetLiveDevice(CastDevice device) => _live.Remove(device);

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _context.Changed -= Context_Changed;
        _live.CollectionChanged -= Live_CollectionChanged;
        _lifetime.Cancel();
        _renderers?.Dispose();
        _sonos?.Dispose();
        _probeController.Dispose();
        _lifetime.Dispose();
        _live.Clear();
        Entries.Clear();
    }
}
