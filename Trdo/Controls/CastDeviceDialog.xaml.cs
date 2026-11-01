using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Services;
using Trdo.Services.Playback;

namespace Trdo.Controls;

public sealed partial class CastDeviceDialog : ContentDialog
{
    private readonly RadioPlayerService _player = RadioPlayerService.Instance;
    private CastPickerSession? _session;
    private CancellationTokenSource? _operation;
    private bool _isBusy;

    public CastDeviceDialog()
    {
        InitializeComponent();
        if (string.IsNullOrEmpty(PrimaryButtonText))
            PrimaryButtonText = "Cast";
        if (string.IsNullOrEmpty(CloseButtonText))
            CloseButtonText = "Cancel";
        if (Title is not string { Length: > 0 })
            Title = "Cast to device";
    }

    private async void ContentDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_player.IsCasting)
        {
            CurrentTargetText.Text = string.Format(
                LocalizationService.GetString("CastDeviceDialog_CurrentlyCasting", "Currently casting to {0}"),
                _player.CastTargetName);
            CurrentTargetText.Visibility = Visibility.Visible;
            SecondaryButtonText = LocalizationService.GetString("CastDeviceDialog_StopCasting", "Stop casting");
        }

        _session = new CastPickerSession(DispatcherQueue);
        _session.Changed += Session_Changed;
        _session.NetworkChanged += Session_NetworkChanged;
        DeviceList.ItemsSource = _session.Entries;
        UpdateState();
        await CastingContextService.Instance.RefreshAsync();
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args) =>
        _operation?.Cancel();

    private void ContentDialog_CloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        _operation?.Cancel();

    private void ContentDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _operation?.Cancel();
        if (_session is not null)
        {
            _session.Changed -= Session_Changed;
            _session.NetworkChanged -= Session_NetworkChanged;
            DeviceList.ItemsSource = null;
            _session.Dispose();
            _session = null;
        }
    }

    private void Session_Changed(object? sender, EventArgs e) => UpdateState();

    private void Session_NetworkChanged(object? sender, EventArgs e)
    {
        _operation?.Cancel();
        CastError.IsOpen = false;
    }

    private void UpdateState()
    {
        bool hasDevices = _session?.Entries.Count > 0;
        DeviceList.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;
        bool scanning = _session?.IsScanning == true;
        ScanningRow.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        UnavailableText.Visibility = scanning ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = !_isBusy && DeviceList.SelectedItem is CastDeviceEntry;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private async void DeviceList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (await ChangeTargetAsync(stopCasting: false))
            Hide();
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // A button deferral also blocks Cancel while resolution is pending.
        args.Cancel = true;
        if (await ChangeTargetAsync(stopCasting: false))
            Hide();
    }

    private async void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (await ChangeTargetAsync(stopCasting: true))
            Hide();
    }

    private async Task<bool> ChangeTargetAsync(bool stopCasting)
    {
        CastDeviceEntry? entry = DeviceList.SelectedItem as CastDeviceEntry;
        if (_isBusy || _session is not { } session || (!stopCasting && entry is null))
            return false;

        CastDevice? device = null;
        string? name = stopCasting ? null : entry?.Name;
        _isBusy = true;
        _operation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operation.Token;
        string primaryText = PrimaryButtonText;
        PrimaryButtonText = LocalizationService.GetString("CastDeviceDialog_Connecting", "Connecting...");
        IsSecondaryButtonEnabled = false;
        DeviceList.IsEnabled = false;
        CastError.IsOpen = false;
        UpdateState();
        try
        {
            if (stopCasting)
            {
                await _player.SetCastTargetAsync(null, null, cancellationToken);
            }
            else if (entry is not null)
            {
                await CastingContextService.Instance.RefreshAsync();
                cancellationToken.ThrowIfCancellationRequested();
                device = await session.ResolveAsync(entry, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                CastAttempt? attempt = CastingContextService.Instance.CreateAttempt(device);
                if (device.Sonos is { } sonos)
                {
                    await _player.SetSonosTargetAsync(sonos, cancellationToken, attempt);
                }
                else
                {
                    RendererItem item = session.Claim(device);
                    await _player.SetCastTargetAsync(item, device.Name, cancellationToken, attempt);
                }
            }
            else
            {
                throw new InvalidOperationException("No cast device is selected.");
            }
            return !cancellationToken.IsCancellationRequested && _session is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (device is not null)
                session.ForgetLiveDevice(device);
            LogService.Error("CastDeviceDialog", "Changing the cast target failed", ex);
            if (_session is not null)
            {
                CastError.Message = string.Format(
                    LocalizationService.GetString("CastDeviceDialog_Failed", "Couldn't cast to {0}: {1}"),
                    name ?? LocalizationService.GetString("CastDeviceDialog_ThisPc", "this PC"), ex.Message);
                CastError.IsOpen = true;
            }
            return false;
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _isBusy = false;
            PrimaryButtonText = primaryText;
            IsSecondaryButtonEnabled = true;
            DeviceList.IsEnabled = true;
            UpdateState();
        }
    }
}
