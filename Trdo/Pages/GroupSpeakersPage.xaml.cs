using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Controls;
using Trdo.Services;
using Trdo.Services.Playback;

namespace Trdo.Pages;

/// <summary>One row of the room picker: a Sonos room and who it is playing with right now.</summary>
public sealed class SonosRoomItem(SonosRoom room, string details)
{
    public SonosRoom Room { get; } = room;
    public string Name => Room.Name;
    public string Details { get; } = details;
    public string AutomationName => $"{Name}, {Details}";
}

/// <summary>
/// Chooses which Sonos rooms play together. The selection is the group that should exist
/// afterwards; <see cref="SonosGroupingPolicy"/> works out the joins and leaves to get there.
/// Like the cast page, it holds the tray popup open while it is showing.
/// </summary>
public sealed partial class GroupSpeakersPage : Page
{
    private CastPickerSession? _session;
    private CancellationTokenSource? _operation;
    private IDisposable? _keepOpen;
    private IReadOnlyList<SonosZoneGroup> _household = [];
    private bool _isBusy;
    private bool _isPopulating;

    // The name box follows the saved name of whatever is ticked until the user types in it;
    // after that their text is left alone as the selection changes.
    private bool _isFillingName;
    private bool _nameEdited;

    public GroupSpeakersPage()
    {
        InitializeComponent();

        PageTitle.Text = LocalizationService.GetString("GroupSpeakersPage_Title", "Group speakers");
        HintText.Text = LocalizationService.GetString(
            "GroupSpeakersPage_Hint", "Choose the speakers that should play together.");
        WarningText.Text = LocalizationService.GetString(
            "GroupSpeakersPage_Warning", "Speakers you add stop what they are playing and follow the group.");
        ApplyButtonText.Text = LocalizationService.GetString("GroupSpeakersPage_Apply", "Apply");
        GroupNameBox.Header = LocalizationService.GetString("GroupSpeakersPage_NameHeader", "Group name");
        GroupNameBox.MaxLength = SonosGroupNames.MaxLength;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _keepOpen = TrayPopupWindow.FromXamlRoot(XamlRoot)?.KeepOpen();
        _operation = new CancellationTokenSource();
        _session = new CastPickerSession(DispatcherQueue);
        _ = LoadAsync(_operation.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _operation?.Cancel();
        _session?.Dispose();
        _session = null;
        _keepOpen?.Dispose();
        _keepOpen = null;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_session is not { } session)
            return;

        LoadingRow.Visibility = Visibility.Visible;
        try
        {
            _household = await session.GetSonosHouseholdAsync(cancellationToken);
            Populate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogService.Error("GroupSpeakersPage", "Could not read the Sonos household", ex);
            ShowMessage(ex.Message);
        }

        LoadingRow.Visibility = Visibility.Collapsed;
    }

    private void Populate()
    {
        List<(SonosRoom Room, SonosZoneGroup Group)> rooms = [];
        foreach (SonosZoneGroup group in _household)
        {
            foreach (SonosRoom room in group.Members)
            {
                // A room without an address can't be sent a join or leave.
                if (!string.IsNullOrWhiteSpace(room.Uuid) && !string.IsNullOrWhiteSpace(room.Location))
                    rooms.Add((room, group));
            }
        }

        if (rooms.Count < 2)
        {
            ShowMessage(LocalizationService.GetString(
                "GroupSpeakersPage_TooFew", "Grouping needs at least two Sonos speakers on this network."));
            return;
        }

        List<SonosRoomItem> items = [];
        foreach ((SonosRoom room, SonosZoneGroup group) in rooms.OrderBy(entry => entry.Room.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string[] others = [.. group.Members
                .Where(member => !string.Equals(member.Uuid, room.Uuid, StringComparison.OrdinalIgnoreCase))
                .Select(member => member.Name)];
            string details = others.Length == 0
                ? LocalizationService.GetString("GroupSpeakersPage_NotGrouped", "Not grouped")
                : string.Format(LocalizationService.GetString(
                    "GroupSpeakersPage_PlayingWith", "Playing with {0}"), string.Join(", ", others));
            items.Add(new SonosRoomItem(room, details));
        }

        // Start from what's playing: the rooms of the group being cast to, so changing it is
        // a matter of ticking or unticking, and the unchanged selection needs no work.
        string? castUuid = CastCoordinatorUuid;
        SonosZoneGroup? casting = castUuid is null
            ? null
            : _household.FirstOrDefault(group => string.Equals(group.CoordinatorUuid, castUuid, StringComparison.OrdinalIgnoreCase));

        _isPopulating = true;
        RoomList.ItemsSource = items;
        foreach (SonosRoomItem item in items)
        {
            if (casting is not null && casting.Members.Any(member =>
                    string.Equals(member.Uuid, item.Room.Uuid, StringComparison.OrdinalIgnoreCase)))
            {
                RoomList.SelectedItems.Add(item);
            }
        }
        _isPopulating = false;

        RoomList.Visibility = Visibility.Visible;
        WarningText.Visibility = Visibility.Visible;
        UpdateState();
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
        RoomList.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
        WarningText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The coordinator playback is cast to, so regrouping leaves it leading its group.</summary>
    private static string? CastCoordinatorUuid => RadioPlayerService.Instance.SonosTarget?.Uuid;

    private IReadOnlyList<SonosGroupingStep> CurrentPlan() =>
        SonosGroupingPolicy.Plan(
            _household,
            RoomList.SelectedItems.OfType<SonosRoomItem>().Select(item => item.Room.Uuid).ToArray(),
            CastCoordinatorUuid);

    private List<SonosRoomItem> SelectedRooms() => [.. RoomList.SelectedItems.OfType<SonosRoomItem>()];

    /// <summary>What the name box holds, as it would be saved.</summary>
    private string EnteredName => GroupNameBox.Text.Trim();

    /// <summary>Whether the name box differs from what is saved for the ticked rooms (two or more).</summary>
    private bool NameChanged(IReadOnlyList<SonosRoomItem> selected) =>
        selected.Count >= 2 &&
        !string.Equals(
            EnteredName,
            SonosGroupNames.Find(selected.Select(item => item.Room.Uuid)) ?? string.Empty,
            StringComparison.Ordinal);

    private void UpdateState()
    {
        List<SonosRoomItem> selected = SelectedRooms();

        bool isGroup = selected.Count >= 2;
        GroupNameBox.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        if (isGroup)
        {
            // Until it is named, a group is called after its rooms; show that as the placeholder.
            GroupNameBox.PlaceholderText = SonosPolicy.GroupDisplayName(selected.Select(item => item.Name).ToArray());
            if (!_nameEdited)
            {
                _isFillingName = true;
                GroupNameBox.Text = SonosGroupNames.Find(selected.Select(item => item.Room.Uuid)) ?? string.Empty;
                _isFillingName = false;
            }
        }

        ApplyButton.IsEnabled = !_isBusy && (CurrentPlan().Count > 0 || NameChanged(selected));
    }

    private void GroupNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isFillingName)
            return;

        _nameEdited = true;
        UpdateState();
    }

    private void RoomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isPopulating)
            UpdateState();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _session is not { } session || _operation is not { } operation)
            return;

        IReadOnlyList<SonosGroupingStep> steps = CurrentPlan();
        List<SonosRoomItem> selected = SelectedRooms();
        bool saveName = NameChanged(selected);
        if (steps.Count == 0 && !saveName)
            return;

        CancellationToken cancellationToken = operation.Token;
        _isBusy = true;
        GroupError.IsOpen = false;
        RoomList.IsEnabled = false;
        GroupNameBox.IsEnabled = false;
        UpdateState();
        bool succeeded = false;
        try
        {
            if (steps.Count > 0)
                await session.ApplySonosGroupingAsync(steps, cancellationToken);

            // Names follow the set of rooms, so this is right whichever room ends up leading.
            if (saveName)
                SonosGroupNames.Set(selected.Select(item => item.Room.Uuid), EnteredName);
            succeeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogService.Error("GroupSpeakersPage", "Changing the Sonos group failed", ex);
            GroupError.Message = string.Format(
                LocalizationService.GetString("GroupSpeakersPage_Failed", "Couldn't change the group: {0}"), ex.Message);
            GroupError.IsOpen = true;
        }
        finally
        {
            _isBusy = false;
            RoomList.IsEnabled = true;
            GroupNameBox.IsEnabled = true;
        }

        if (succeeded)
        {
            NavigationService.Instance.GoBack();
            return;
        }

        // A half-applied plan leaves the household somewhere in between; show where it really is.
        _ = ReloadAsync(cancellationToken);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_session is not { } session)
            return;

        try
        {
            _household = await session.GetSonosHouseholdAsync(cancellationToken);
            Populate();
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException)
        {
            // The error already on screen stays; nothing more to say.
        }
    }
}
