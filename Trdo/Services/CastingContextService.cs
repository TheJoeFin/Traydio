using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Services.Playback;
using Windows.Networking.Connectivity;
using Windows.Storage;

namespace Trdo.Services;

public sealed class CastingContextService
{
    private sealed record NetworkSnapshot(string? Key, string Signature, HashSet<IPAddress> Addresses, bool HasOtherLan);

    private static readonly Lazy<CastingContextService> _instance = new(() => new CastingContextService());
    public static CastingContextService Instance => _instance.Value;

    private readonly CastHistoryStore _store = new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "casthistory.json"));
    private CastHistoryDocument _history = CastHistoryPolicy.CreateDocument();
    private NetworkSnapshot _network = new(null, string.Empty, [], false);
    private DispatcherQueue? _queue;
    private DispatcherQueueTimer? _refreshTimer;
    private Task _pendingSave = Task.CompletedTask;
    private int _saveRevision;
    private int _refreshRevision;
    private int _networkNotificationRevision;
    private int _processedNetworkNotificationRevision;
    private Task? _initialization;
    private bool _isRunning;

    public event EventHandler? Changed;
    public long NetworkGeneration { get; private set; }
    public string? NetworkKey => _network.Key;
    private bool IsContextCurrent => _processedNetworkNotificationRevision == Volatile.Read(ref _networkNotificationRevision);
    public bool HasRememberedDevices => CurrentHistory is { Devices.Count: > 0 };
    public bool IsShortcutVisible => CastHistoryPolicy.ShouldShowShortcut(CurrentHistory);
    private NetworkCastHistory? CurrentHistory => CastHistoryPolicy.FindNetwork(_history, NetworkKey);
    public IReadOnlyList<CastHistoryDevice> RememberedDevices =>
        CurrentHistory is { } history ? history.Devices : Array.Empty<CastHistoryDevice>();

    private CastingContextService()
    {
    }

    /// <summary>
    /// Safe to call and await more than once; every caller shares one initialization. Until it
    /// completes there is no network key, so nothing is remembered or offered, and <see cref="Changed"/>
    /// fires when the first network read lands so listeners refresh.
    /// </summary>
    public Task InitializeAsync(DispatcherQueue queue) => _initialization ??= InitializeCoreAsync(queue);

    private async Task InitializeCoreAsync(DispatcherQueue queue)
    {
        _queue = queue;
        try
        {
            _history = await _store.LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                   or FormatException or NotSupportedException)
        {
            LogService.Error("CastingContextService", "Could not read casting history; preserving the original file", ex);
        }
        _isRunning = true;
        _refreshTimer = queue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(300);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += RefreshTimer_Tick;
        NetworkInformation.NetworkStatusChanged += NetworkStatusChanged;
        RadioPlayerService.Instance.CastPlaybackConfirmed += CastPlaybackConfirmed;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!_isRunning)
            return;

        int revision = Interlocked.Increment(ref _refreshRevision);
        int notificationRevision = Volatile.Read(ref _networkNotificationRevision);
        NetworkSnapshot snapshot = await Task.Run(ReadNetwork);
        if (!_isRunning || revision != _refreshRevision ||
            notificationRevision != Volatile.Read(ref _networkNotificationRevision))
            return;

        _processedNetworkNotificationRevision = notificationRevision;
        if (snapshot.Signature == _network.Signature)
            return;

        _network = snapshot;
        NetworkGeneration++;
        Changed?.Invoke(this, EventArgs.Empty);
        LogService.Info("CastingContextService", $"Wi-Fi context changed (identified={NetworkKey is not null}, remembered={HasRememberedDevices})");
    }

    public CastAttempt? CreateAttempt(CastDevice device)
    {
        if (!IsContextCurrent || NetworkKey is not { } key)
            return null;

        if (device.Sonos is { } sonos)
        {
            if (!_network.Addresses.Contains(LocalMediaHttpServer.LocalAddressFor(sonos.Address)))
                return null;
        }
        else if (_network.HasOtherLan)
        {
            // LibVLC exposes no endpoint/interface provenance, so an Ethernet renderer must
            // not accidentally become a remembered speaker for the connected Wi-Fi.
            LogService.Info("CastingContextService", "Skipping renderer history on an ambiguous multi-LAN connection");
            return null;
        }

        return new CastAttempt(key, NetworkGeneration, device.ToHistoryDevice());
    }

    public Task SetShortcutHiddenAsync(bool hidden)
    {
        if (CurrentHistory is not { } history)
            throw new InvalidOperationException("There is no remembered casting network to update.");

        history.ShortcutHidden = hidden;
        Changed?.Invoke(this, EventArgs.Empty);
        return QueueSave();
    }

    private void CastPlaybackConfirmed(object? sender, CastAttempt attempt)
    {
        if (!_isRunning || !CastHistoryPolicy.CanRemember(attempt, NetworkKey, NetworkGeneration, IsContextCurrent))
            return;

        CastHistoryPolicy.RecordSuccess(_history, attempt, DateTimeOffset.UtcNow);
        Changed?.Invoke(this, EventArgs.Empty);
        _ = SaveConfirmedCastAsync();
    }

    private async Task SaveConfirmedCastAsync()
    {
        try
        {
            await QueueSave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("CastingContextService", "Could not persist a confirmed cast", ex);
        }
    }

    private Task QueueSave()
    {
        string json = CastHistoryStorageFormat.Serialize(_history);
        int revision = Interlocked.Increment(ref _saveRevision);
        _pendingSave = SaveAfterAsync(_pendingSave, json, revision);
        return _pendingSave;
    }

    private async Task SaveAfterAsync(Task previous, string json, int revision)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn("CastingContextService", $"Retrying after a history write failed: {ex.Message}");
        }

        if (revision == Volatile.Read(ref _saveRevision))
            await _store.SaveAsync(json).ConfigureAwait(false);
    }

    private void NetworkStatusChanged(object sender)
    {
        Interlocked.Increment(ref _networkNotificationRevision);
        if (_queue?.TryEnqueue(() =>
        {
            if (!_isRunning)
                return;
            _refreshTimer?.Stop();
            _refreshTimer?.Start();
        }) != true)
            LogService.Warn("CastingContextService", "Could not dispatch a network-context update");
    }

    private async void RefreshTimer_Tick(DispatcherQueueTimer sender, object args) => await RefreshAsync();

    private NetworkSnapshot ReadNetwork()
    {
        try
        {
            List<ConnectionProfile> wifi = NetworkInformation.GetConnectionProfiles()
                .Where(profile => profile.IsWlanConnectionProfile &&
                    profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                .ToList();
            if (wifi.Count != 1)
                return new NetworkSnapshot(null, "unknown", [], false);

            ConnectionProfile profile = wifi[0];
            string ssid = profile.WlanConnectionProfileDetails.GetConnectedSsid();
            if (string.IsNullOrEmpty(ssid) || profile.NetworkAdapter is null)
                return new NetworkSnapshot(null, "unknown", [], false);

            string key = CastHistoryPolicy.NetworkKey(ssid,
                profile.NetworkSecuritySettings.NetworkAuthenticationType.ToString(),
                profile.NetworkSecuritySettings.NetworkEncryptionType.ToString(), _history.Salt);
            Guid adapterId = profile.NetworkAdapter.NetworkAdapterId;
            HashSet<IPAddress> addresses = [];
            bool hasOtherLan = false;
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                    continue;

                bool isWifi = Guid.TryParse(adapter.Id, out Guid id) && id == adapterId;
                foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (IPAddress.IsLoopback(address.Address))
                        continue;
                    if (isWifi)
                        addresses.Add(address.Address);
                    else
                        hasOtherLan = true;
                }
            }

            string signature = $"{key}:{adapterId}:{hasOtherLan}:{string.Join(",", addresses.Select(address => address.ToString()).Order(StringComparer.Ordinal))}";
            return new NetworkSnapshot(key, signature, addresses, hasOtherLan);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NetworkInformationException
                                   or UnauthorizedAccessException or InvalidOperationException)
        {
            LogService.Warn("CastingContextService", $"Could not identify the connected Wi-Fi: {ex.Message}");
            return new NetworkSnapshot(null, "unknown", [], false);
        }
    }

    public void Shutdown()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        NetworkInformation.NetworkStatusChanged -= NetworkStatusChanged;
        RadioPlayerService.Instance.CastPlaybackConfirmed -= CastPlaybackConfirmed;
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
        }

        try
        {
            _pendingSave.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("CastingContextService", "Could not flush casting history on shutdown", ex);
        }
    }
}
