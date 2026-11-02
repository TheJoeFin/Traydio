using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Services;
using Trdo.Services.Playback;
using Trdo.ViewModels;

namespace Trdo.Controls;

/// <summary>
/// Fills a <see cref="MenuFlyout"/> with the speakers that can be cast to right now, so
/// the common case is one click. Discovery runs only while the flyout is open; the
/// "Cast…" entry at the far end from the anchor opens the full cast page instead.
/// </summary>
internal sealed class CastSpeakerMenu
{
    private readonly MenuFlyout _flyout;
    private readonly FrameworkElement _anchor;
    private readonly Action _openCastPage;
    private CastPickerSession? _session;
    private IDisposable? _keepOpen;
    private string _signature = string.Empty;

    public CastSpeakerMenu(MenuFlyout flyout, FrameworkElement anchor, Action openCastPage)
    {
        _flyout = flyout;
        _anchor = anchor;
        _openCastPage = openCastPage;
        _flyout.Opening += Flyout_Opening;
        _flyout.Closed += Flyout_Closed;
    }

    private void Flyout_Opening(object? sender, object e)
    {
        // Discovery can raise a system network-permission prompt after it starts; keep
        // the tray popup visible until the flyout is done, even while that prompt has focus.
        _keepOpen = TrayPopupWindow.FromXamlRoot(_anchor.XamlRoot)?.KeepOpen();

        _signature = string.Empty;
        _session = CastPickerSession.Acquire(_anchor.DispatcherQueue);
        _session.Changed += Session_Changed;
        Rebuild();
    }

    private void Flyout_Closed(object? sender, object e)
    {
        // A click hands the session to the connect it started, so it may already be gone.
        ReleaseSession()?.Dispose();
        _keepOpen?.Dispose();
        _keepOpen = null;
    }

    private void Session_Changed(object? sender, EventArgs e) => Rebuild();

    private CastPickerSession? ReleaseSession()
    {
        CastPickerSession? session = _session;
        _session = null;
        if (session is not null)
            session.Changed -= Session_Changed;
        return session;
    }

    private void Rebuild()
    {
        if (_session is not { } session)
            return;

        RadioPlayerService player = RadioPlayerService.Instance;

        // Previously used speakers go first and are listed straight away (they're known from
        // history), even while their availability check is still running; they drop out if
        // the check ends without finding them. Everything else is listed once discovery sees it.
        List<CastDeviceEntry> devices = [];
        foreach (CastDeviceEntry entry in session.Entries)
        {
            if (entry.IsRemembered && (entry.IsAvailable || entry.IsChecking))
                devices.Add(entry);
        }

        foreach (CastDeviceEntry entry in session.Entries)
        {
            if (!entry.IsRemembered && entry.IsAvailable)
                devices.Add(entry);
        }

        // Discovery reports in bursts; only touch the open menu when what it shows changes.
        string signature = $"{session.IsScanning}|{player.IsCasting}|" +
            string.Join("|", devices.ConvertAll(entry =>
                $"{entry.Name}:{entry.Glyph}:{string.Join(",", entry.Rooms)}:{player.IsCastTarget(entry.LiveDevice)}"));
        if (signature == _signature)
            return;
        _signature = signature;

        IList<MenuFlyoutItemBase> items = _flyout.Items;
        items.Clear();

        // First item = farthest from the button, since the flyout opens upward.
        MenuFlyoutItem more = new()
        {
            Text = LocalizationService.GetString("PlayingPage_Cast", "Cast…"),
            Icon = new FontIcon { Glyph = "" },
        };
        AutomationProperties.SetAutomationId(more, "CastingOptionsMenuItem");
        more.Click += (_, _) => _openCastPage();
        items.Add(more);
        items.Add(new MenuFlyoutSeparator());

        foreach (CastDeviceEntry entry in devices)
        {
            bool isCurrent = player.IsCastTarget(entry.LiveDevice);
            ToggleMenuFlyoutItem item = new()
            {
                Text = entry.Name,
                Icon = new FontIcon { Glyph = entry.Glyph },
                IsChecked = isCurrent,
            };
            if (entry.Rooms.Count > 0)
                ToolTipService.SetToolTip(item, string.Join(", ", entry.Rooms));

            CastDeviceEntry captured = entry;
            item.Click += (_, _) =>
            {
                if (!isCurrent)
                    _ = CastToAsync(captured);
            };
            items.Add(item);
        }

        if (devices.Count == 0 && session.IsScanning)
        {
            items.Add(new MenuFlyoutItem
            {
                Text = LocalizationService.GetString("CastDeviceDialog_Searching", "Looking for devices on your network…"),
                IsEnabled = false,
            });
        }

        if (player.IsCasting)
        {
            items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem stop = new()
            {
                Text = LocalizationService.GetString("CastDeviceDialog_StopCasting", "Stop casting"),
                Icon = new FontIcon { Glyph = "" },
            };
            stop.Click += async (_, _) => await PlayerViewModel.Shared.StopCastingAsync();
            items.Add(stop);
        }
    }

    private async Task CastToAsync(CastDeviceEntry entry)
    {
        // The flyout closes on click, which would dispose the session mid-connect; take it over.
        if (ReleaseSession() is not { } session)
            return;

        try
        {
            await session.CastToAsync(entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogService.Error("CastSpeakerMenu", "Changing the cast target failed", ex);
            PlaybackErrorService.Instance.Report(string.Format(
                LocalizationService.GetString("CastDeviceDialog_Failed", "Couldn't cast to {0}: {1}"),
                entry.Name, ex.Message));
        }
        finally
        {
            session.Dispose();
        }
    }
}
