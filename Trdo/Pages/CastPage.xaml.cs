using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Controls;
using Trdo.Services;
using Trdo.Services.Playback;
using Trdo.ViewModels;

namespace Trdo.Pages;

/// <summary>
/// Picks the device playback is cast to. A page rather than a dialog because it is a
/// real task (continuous discovery, resolving a device, connecting, errors) and because
/// it keeps the hosting tray popup from light-dismissing while it is the current page:
/// discovery can raise a system network-permission prompt that steals focus.
/// </summary>
public sealed partial class CastPage : Page
{
    private readonly RadioPlayerService _player = RadioPlayerService.Instance;
    private CastPickerSession? _session;
    private CancellationTokenSource? _operation;
    private IDisposable? _keepOpen;
    private bool _isBusy;

    public CastPage()
    {
        InitializeComponent();

        PageTitle.Text = GetText("CastDeviceDialog/Title", "Cast to device");
        CastButtonText.Text = GetText("CastDeviceDialog/PrimaryButtonText", "Cast");
        StopCastingText.Text = LocalizationService.GetString("CastDeviceDialog_StopCasting", "Stop casting");
        GroupSpeakersButton.Content = LocalizationService.GetString("CastPage_GroupSpeakers", "Group speakers…");
    }

    private void GroupSpeakersButton_Click(object sender, RoutedEventArgs e) =>
        (FindShellViewModel())?.NavigateToGroupSpeakersPage();

    private ShellViewModel? FindShellViewModel()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is ShellPage shell)
                return shell.ViewModel;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string GetText(string key, string fallback) => LocalizationService.GetString(key, fallback);

    // XamlRoot may still be unset while the page is being navigated to, which would leave the
    // popup unheld; in that case wait for Loaded. Never acquires twice.
    private void AcquireKeepOpen()
    {
        if (_keepOpen is not null)
            return;

        if (TrayPopupWindow.FromXamlRoot(XamlRoot) is { } popup)
        {
            _keepOpen = popup.KeepOpen();
            return;
        }

        Loaded -= Page_Loaded;
        Loaded += Page_Loaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Page_Loaded;
        AcquireKeepOpen();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        AcquireKeepOpen();

        if (_player.IsCasting)
        {
            CurrentTargetText.Text = string.Format(
                LocalizationService.GetString("CastDeviceDialog_CurrentlyCasting", "Currently casting to {0}"),
                _player.CastTargetName);
            CurrentTargetText.Visibility = Visibility.Visible;
            StopCastingButton.Visibility = Visibility.Visible;
        }

        _session = CastPickerSession.Acquire(DispatcherQueue);
        _session.Changed += Session_Changed;
        _session.NetworkChanged += Session_NetworkChanged;
        DeviceList.ItemsSource = _session.Entries;
        UpdateState();
        _ = CastingContextService.Instance.RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        Loaded -= Page_Loaded;
        _operation?.Cancel();
        if (_session is not null)
        {
            _session.Changed -= Session_Changed;
            _session.NetworkChanged -= Session_NetworkChanged;
            DeviceList.ItemsSource = null;
            _session.Dispose();
            _session = null;
        }

        _keepOpen?.Dispose();
        _keepOpen = null;
    }

    /// <summary>
    /// Right-click on a Sonos speaker or group: add it to another group, or take one of its rooms out.
    /// Only for Sonos targets that are currently reachable; a Chromecast or an offline entry has no menu.
    /// </summary>
    private void DeviceItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: CastDeviceEntry entry } element ||
            BuildGroupMenu(entry) is not { } menu)
        {
            return;
        }

        args.Handled = true;
        if (args.TryGetPosition(element, out Windows.Foundation.Point position))
            menu.ShowAt(element, new FlyoutShowOptions { Position = position });
        else
            menu.ShowAt(element);
    }

    private MenuFlyout? BuildGroupMenu(CastDeviceEntry entry)
    {
        if (_isBusy || _session is not { } session || entry.LiveDevice?.Sonos is not { } sonos)
            return null;

        MenuFlyout menu = new();

        List<CastDeviceEntry> others = [.. session.Entries
            .Where(other => !ReferenceEquals(other, entry) && other.LiveDevice?.Sonos is not null)];
        if (others.Count > 0)
        {
            MenuFlyoutSubItem add = new()
            {
                Text = LocalizationService.GetString("CastPage_AddToGroup", "Add to group"),
                Icon = new FontIcon { Glyph = "" },
            };
            foreach (CastDeviceEntry other in others)
            {
                SonosDevice target = other.LiveDevice!.Sonos!;
                MenuFlyoutItem item = new() { Text = other.Name };
                if (other.Rooms.Count > 0)
                    ToolTipService.SetToolTip(item, string.Join(", ", other.Rooms));
                item.Click += async (_, _) => await ChangeGroupAsync(household => PlanAdd(household, sonos, target));
                add.Items.Add(item);
            }
            menu.Items.Add(add);
        }

        // Rooms and their UUIDs come from the same member list, so index i is one room.
        if (sonos.Rooms.Count > 1 && sonos.Rooms.Count == sonos.RoomUuids.Count)
        {
            MenuFlyoutSubItem remove = new()
            {
                Text = LocalizationService.GetString("CastPage_RemoveFromGroup", "Remove from group"),
                Icon = new FontIcon { Glyph = "" },
            };
            for (int i = 0; i < sonos.Rooms.Count; i++)
            {
                string uuid = sonos.RoomUuids[i];
                MenuFlyoutItem item = new() { Text = sonos.Rooms[i] };
                item.Click += async (_, _) => await ChangeGroupAsync(household => PlanRemove(household, uuid));
                remove.Items.Add(item);
            }
            menu.Items.Add(remove);
        }

        if (menu.Items.Count == 0)
            return null;

        menu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem more = new()
        {
            Text = LocalizationService.GetString("CastPage_GroupSpeakers", "Group speakers…"),
            Icon = new FontIcon { Glyph = "" },
        };
        more.Click += (_, _) => FindShellViewModel()?.NavigateToGroupSpeakersPage();
        menu.Items.Add(more);
        return menu;
    }

    /// <summary>
    /// Everything in both targets ends up in one group. The coordinator being cast to keeps
    /// leading when it is involved, so adding it to another group never cuts the player off.
    /// </summary>
    private static IReadOnlyList<SonosGroupingStep> PlanAdd(
        IReadOnlyList<SonosZoneGroup> household, SonosDevice adding, SonosDevice target) =>
        SonosGroupingPolicy.Plan(
            household,
            adding.RoomUuids.Concat(target.RoomUuids).ToArray(),
            RadioPlayerService.Instance.SonosTarget?.Uuid);

    /// <summary>Just that room leaves; the rest of its group carries on.</summary>
    private static IReadOnlyList<SonosGroupingStep> PlanRemove(IReadOnlyList<SonosZoneGroup> household, string roomUuid)
    {
        foreach (SonosZoneGroup group in household)
        {
            SonosRoom? room = group.Members.FirstOrDefault(member =>
                string.Equals(member.Uuid, roomUuid, StringComparison.OrdinalIgnoreCase));
            if (room is not null)
                return new[] { new SonosGroupingStep(SonosGroupingAction.Leave, room, null) };
        }
        return [];
    }

    private async Task ChangeGroupAsync(Func<IReadOnlyList<SonosZoneGroup>, IReadOnlyList<SonosGroupingStep>> plan)
    {
        if (_isBusy || _session is not { } session)
            return;

        _isBusy = true;
        _operation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operation.Token;
        DeviceList.IsEnabled = false;
        CastError.IsOpen = false;
        UpdateState();
        try
        {
            IReadOnlyList<SonosGroupingStep> steps = plan(await session.GetSonosHouseholdAsync(cancellationToken));
            if (steps.Count > 0)
            {
                await session.ApplySonosGroupingAsync(steps, cancellationToken);

                // Sonos takes a moment to report the new grouping; then show it rather than
                // waiting for the next search round.
                await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                await session.RefreshSonosAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("CastPage", "Changing the Sonos group failed", ex);
            if (_session is not null)
            {
                CastError.Message = string.Format(
                    LocalizationService.GetString("GroupSpeakersPage_Failed", "Couldn't change the group: {0}"), ex.Message);
                CastError.IsOpen = true;
            }
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _isBusy = false;
            DeviceList.IsEnabled = true;
            UpdateState();
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
        CastButton.IsEnabled = !_isBusy && DeviceList.SelectedItem is CastDeviceEntry;
        GroupSpeakersButton.Visibility = _session?.HasSonos == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private async void DeviceList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        await ChangeTargetAndCloseAsync(stopCasting: false);

    private async void CastButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeTargetAndCloseAsync(stopCasting: false);

    private async void StopCastingButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeTargetAndCloseAsync(stopCasting: true);

    private async Task ChangeTargetAndCloseAsync(bool stopCasting)
    {
        if (await ChangeTargetAsync(stopCasting))
            NavigationService.Instance.GoBack();
    }

    private async Task<bool> ChangeTargetAsync(bool stopCasting)
    {
        CastDeviceEntry? entry = DeviceList.SelectedItem as CastDeviceEntry;
        if (_isBusy || _session is not { } session || (!stopCasting && entry is null))
            return false;

        string? name = stopCasting ? null : entry?.Name;
        _isBusy = true;
        _operation = new CancellationTokenSource();
        CancellationToken cancellationToken = _operation.Token;
        string castText = CastButtonText.Text;
        CastButtonText.Text = LocalizationService.GetString("CastDeviceDialog_Connecting", "Connecting...");
        StopCastingButton.IsEnabled = false;
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
                await session.CastToAsync(entry, cancellationToken);
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
            LogService.Error("CastPage", "Changing the cast target failed", ex);
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
            CastButtonText.Text = castText;
            StopCastingButton.IsEnabled = true;
            DeviceList.IsEnabled = true;
            UpdateState();
        }
    }
}
