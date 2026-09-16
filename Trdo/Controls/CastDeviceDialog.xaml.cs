using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Trdo.Services;
using Trdo.Services.Playback;

namespace Trdo.Controls;

/// <summary>
/// Picks a device on the local network to send the audio to, or brings it back to this PC.
/// Two searches feed one list: LibVLC's renderer discovery (Chromecast) and the Sonos SSDP
/// search, either of which may be unavailable on a given PC without stopping the other.
/// <para>
/// Both discoveries live exactly as long as the dialog: they start in <c>Opened</c> and are
/// disposed in <c>Closed</c>, so no socket or LibVLC object outlives the picker. The one
/// exception is a LibVLC item the user chose, which is claimed from its discovery and handed
/// to <see cref="RadioPlayerService"/>, which owns it from then on. A Sonos row holds only
/// addresses, so nothing needs claiming for it.
/// </para>
/// </summary>
public sealed partial class CastDeviceDialog : ContentDialog
{
    private const string Component = "CastDeviceDialog";

    private readonly RadioPlayerService _player = RadioPlayerService.Instance;
    private readonly ObservableCollection<CastDevice> _devices = [];
    private CastRendererDiscovery? _rendererDiscovery;
    private SonosDiscovery? _sonosDiscovery;

    public CastDeviceDialog()
    {
        InitializeComponent();

        // x:Uid fills these from Resources.resw. The fallbacks only matter if a resource goes
        // missing, because a ContentDialog with no button text has no way to be closed.
        if (string.IsNullOrEmpty(PrimaryButtonText))
        {
            PrimaryButtonText = "Cast";
        }

        if (string.IsNullOrEmpty(CloseButtonText))
        {
            CloseButtonText = "Cancel";
        }

        if (Title is not string { Length: > 0 })
        {
            Title = "Cast to device";
        }
    }

    private void ContentDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_player.IsCasting)
        {
            CurrentTargetText.Text = string.Format(
                LocalizationService.GetString("CastDeviceDialog_CurrentlyCasting", "Currently casting to {0}"),
                _player.CastTargetName);
            CurrentTargetText.Visibility = Visibility.Visible;

            // Only offered while there is something to stop; an empty secondary text hides the button.
            SecondaryButtonText = LocalizationService.GetString("CastDeviceDialog_StopCasting", "Stop casting");
        }

        _rendererDiscovery = CastRendererDiscovery.TryCreate(DispatcherQueue, _devices);
        if (_rendererDiscovery is not null && !_rendererDiscovery.Start())
        {
            _rendererDiscovery.Dispose();
            _rendererDiscovery = null;
        }

        SonosDiscovery sonosDiscovery = new(DispatcherQueue, _devices);
        try
        {
            if (sonosDiscovery.Start())
            {
                _sonosDiscovery = sonosDiscovery;
            }
            else
            {
                sonosDiscovery.Dispose();
            }
        }
        catch (Exception ex)
        {
            LogService.Error(Component, "Sonos search could not start", ex);
            sonosDiscovery.Dispose();
        }

        if (_rendererDiscovery is null && _sonosDiscovery is null)
        {
            ScanningRow.Visibility = Visibility.Collapsed;
            UnavailableText.Visibility = Visibility.Visible;
            return;
        }

        DeviceList.ItemsSource = _devices;
        _devices.CollectionChanged += Devices_CollectionChanged;
        UpdateEmptyState();
    }

    private void ContentDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _devices.CollectionChanged -= Devices_CollectionChanged;
        DeviceList.ItemsSource = null;

        _rendererDiscovery?.Dispose();
        _rendererDiscovery = null;
        _sonosDiscovery?.Dispose();
        _sonosDiscovery = null;
        _devices.Clear();
    }

    private void Devices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        bool hasDevices = _devices.Count > 0;
        DeviceList.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        IsPrimaryButtonEnabled = DeviceList.SelectedItem is CastDevice;

    private void DeviceList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is CastDevice)
        {
            CastToSelectedDevice();
            Hide();
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        CastToSelectedDevice();

    private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        ChangeCastTarget(null, null);

    private void CastToSelectedDevice()
    {
        if (DeviceList.SelectedItem is not CastDevice device)
        {
            return;
        }

        if (device.Sonos is { } sonos)
        {
            ChangeSonosTarget(sonos);
            return;
        }

        if (_rendererDiscovery is null)
        {
            return;
        }

        // Claim before the dialog closes: Closed disposes every item the discovery still owns.
        RendererItem item = _rendererDiscovery.Claim(device);
        ChangeCastTarget(item, device.Name);
    }

    private async void ChangeCastTarget(RendererItem? item, string? name)
    {
        try
        {
            await _player.SetCastTargetAsync(item, name);
        }
        catch (Exception ex)
        {
            ReportFailure(name, ex);
        }
    }

    private async void ChangeSonosTarget(SonosDevice device)
    {
        try
        {
            await _player.SetSonosTargetAsync(device);
        }
        catch (Exception ex)
        {
            ReportFailure(device.Name, ex);
        }
    }

    private static void ReportFailure(string? name, Exception ex)
    {
        LogService.Error(Component, $"Changing the cast target to '{name ?? "this PC"}' failed", ex);
        Debug.WriteLine($"[{Component}] Cast target change failed: {ex}");
        PlaybackErrorService.Instance.Report(string.Format(
            LocalizationService.GetString("CastDeviceDialog_Failed", "Couldn't cast to {0}: {1}"),
            name ?? "this PC",
            ex.Message));
    }
}
