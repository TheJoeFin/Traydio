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

    // One discovery session is shared by everything that wants cast devices (the speaker
    // flyout, the cast page, the group page). Each consumer calls Acquire and later Dispose;
    // the session only shuts down once nobody has held it for IdleGrace, so moving from the
    // flyout to the cast page reuses what the flyout already found instead of rediscovering.
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(60);
    private static CastPickerSession? s_shared;
    private int _leases;
    private CancellationTokenSource? _idle;

    public static CastPickerSession Acquire(DispatcherQueue queue)
    {
        CastPickerSession session = s_shared is { _isDisposed: false } existing ? existing : s_shared = new CastPickerSession(queue);
        session._idle?.Cancel();
        session._idle?.Dispose();
        session._idle = null;
        session._leases++;
        return session;
    }

    private CastPickerSession(DispatcherQueue queue)
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

                // The speaker itself, or the group it has since been put into.
                if (CastHistoryPolicy.Resolves(remembered, new CastDevice(device).ToHistoryDevice()))
                {
                    // Discovery or another remembered room in this group may have added it already.
                    if (!_live.Any(existing => existing.Sonos?.SameTargetAs(device) == true))
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

        // Converted once: a renderer's ToHistoryDevice makes native LibVLC calls.
        List<(CastDevice Device, CastHistoryDevice History)> live = _live.Select(device => (device, device.ToHistoryDevice())).ToList();
        List<CastHistoryRow> desired = CastHistoryPolicy.Merge(_context.RememberedDevices, live.Select(pair => pair.History).ToList());
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
            else if (Entries.IndexOf(entry) is var current && current != i)
            {
                Entries.Move(current, i);
            }

            CastDevice? liveDevice = live.FirstOrDefault(pair => CastHistoryPolicy.Matches(pair.History, row.Device)).Device;
            entry.Update(row, liveDevice, _isChecking);
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

    /// <summary>True once discovery (or a remembered-speaker check) has seen at least one Sonos speaker.</summary>
    public bool HasSonos => _live.Any(device => device.Sonos is not null);

    /// <summary>
    /// The household's groups, asked of the first Sonos speaker discovery sees; any player answers
    /// for all of them. Waits for one to appear, and times out if none does.
    /// </summary>
    public async Task<IReadOnlyList<SonosZoneGroup>> GetSonosHouseholdAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_live.FirstOrDefault(device => device.Sonos is not null)?.Sonos is { } sonos)
                {
                    string xml = await _probeController.GetZoneGroupStateAsync(sonos.ZoneGroupTopologyControlUrl, timeout.Token);
                    return SonosPolicy.ParseZoneGroups(xml);
                }
                await _liveChanged.Task.WaitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException(LocalizationService.GetString(
                "GroupSpeakersPage_NoneFound", "No Sonos speakers were found on this network."));
        }
    }

    /// <summary>Re-reads Sonos groupings now, so the list reflects a change this app just made.</summary>
    public Task RefreshSonosAsync(CancellationToken cancellationToken) =>
        _sonos?.RefreshAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Runs a plan from <see cref="SonosGroupingPolicy"/>, stopping at the first room that can't be reached.</summary>
    public async Task ApplySonosGroupingAsync(IReadOnlyList<SonosGroupingStep> steps, CancellationToken cancellationToken)
    {
        foreach (SonosGroupingStep step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SonosDevice device = SonosDevice.FromRoom(step.Room)
                ?? throw new InvalidOperationException($"Couldn't find the address of {step.Room.Name}.");
            if (step.Action == SonosGroupingAction.Join)
                await _probeController.JoinGroupAsync(device, step.CoordinatorUuid!, cancellationToken);
            else
                await _probeController.LeaveGroupAsync(device, cancellationToken);
        }
    }

    /// <summary>
    /// Points playback at <paramref name="entry"/>: waits for the device to be seen live,
    /// then hands it to the player. A device that fails to connect is dropped from the live
    /// set so it isn't offered as available again.
    /// </summary>
    public async Task CastToAsync(CastDeviceEntry entry, CancellationToken cancellationToken)
    {
        CastDevice? device = null;
        try
        {
            await _context.RefreshAsync();
            cancellationToken.ThrowIfCancellationRequested();
            device = await ResolveAsync(entry, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CastAttempt? attempt = _context.CreateAttempt(device);
            if (device.Sonos is { } sonos)
            {
                await RadioPlayerService.Instance.SetSonosTargetAsync(sonos, cancellationToken, attempt);
            }
            else
            {
                RendererItem item = Claim(device);
                await RadioPlayerService.Instance.SetCastTargetAsync(item, device.Name, cancellationToken, attempt);
            }
        }
        catch (Exception ex) when (device is not null
                                   && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            ForgetLiveDevice(device);
            throw;
        }
    }

    public RendererItem Claim(CastDevice device) =>
        _renderers?.Claim(device) ?? throw new InvalidOperationException("Renderer discovery is unavailable.");

    public void ForgetLiveDevice(CastDevice device) => _live.Remove(device);

    /// <summary>Releases this consumer's hold; discovery keeps running for a grace period in case someone else picks it up.</summary>
    public void Dispose()
    {
        if (_isDisposed || _leases == 0)
            return;

        if (--_leases > 0)
            return;

        _idle = new CancellationTokenSource();
        _ = ShutdownWhenIdleAsync(_idle.Token);
    }

    private async Task ShutdownWhenIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(IdleGrace, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_leases == 0)
            Shutdown();
    }

    private void Shutdown()
    {
        if (_isDisposed)
            return;

        if (ReferenceEquals(s_shared, this))
            s_shared = null;
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
