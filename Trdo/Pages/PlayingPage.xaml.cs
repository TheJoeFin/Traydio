using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Controls;
using Trdo.Models;
using Trdo.Services;
using Trdo.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
// Aliased rather than imported: Windows.System also defines DispatcherQueueTimer, which
// collides with the Microsoft.UI.Dispatching one this page already uses.
using VirtualKey = Windows.System.VirtualKey;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Trdo.Pages;
/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class PlayingPage : Page
{
    private const int MinIndexForScrolling = 3;
    private static readonly TimeSpan NowPlayingMarqueeDelay = TimeSpan.FromSeconds(2);
    private const string FilledStar = "\uE735";
    private const string OutlineStar = "\uE734";

    private readonly FavoritesService _favoritesService = FavoritesService.Instance;
    private readonly PlaybackErrorService _errorService = PlaybackErrorService.Instance;
    private readonly StationMetadataBackfillService _backfillService = StationMetadataBackfillService.Instance;
    private readonly DispatcherQueueTimer _nowPlayingMarqueeDelayTimer;

    /// <summary>
    /// The playback error dialog currently on screen, kept so it can be taken back
    /// down if the failure it describes stops being true while the user is reading it.
    /// </summary>
    private ContentDialog? _playbackErrorDialog;

    /// <summary>
    /// Set while the page is pushing the view model's selection into the list, so the
    /// resulting <c>SelectionChanged</c> is not mistaken for the user picking a station.
    /// </summary>
    private bool _isSyncingSelection;

    public PlayerViewModel ViewModel { get; }
    private ShellViewModel? _shellViewModel;

    public PlayingPage()
    {
        Debug.WriteLine("=== PlayingPage Constructor START ===");

        InitializeComponent();
        // Use shared instance so all pages reference the same ViewModel
        ViewModel = PlayerViewModel.Shared;
        DataContext = ViewModel;
        Debug.WriteLine("[PlayingPage] ViewModel assigned and DataContext set");
        NowPlayingMarqueeText.MarqueeCompleted += NowPlayingMarqueeText_MarqueeCompleted;

        // Subscribe to property changes to update UI
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Act as the presenter for playback errors. PlaybackErrorService only shows an
        // error while something is subscribed here, so this pairs strictly with the
        // unsubscribe in Unloaded.
        _errorService.ErrorPresented += ErrorService_ErrorPresented;
        _errorService.ErrorWithdrawn += ErrorService_ErrorWithdrawn;

        // Subscribe to favorites changes
        _favoritesService.FavoritesChanged += FavoritesService_FavoritesChanged;

        // Wait for loaded to access named elements
        Loaded += PlayingPage_Loaded;
        Unloaded += PlayingPage_Unloaded;

        _nowPlayingMarqueeDelayTimer = DispatcherQueue.CreateTimer();
        _nowPlayingMarqueeDelayTimer.Interval = NowPlayingMarqueeDelay;
        _nowPlayingMarqueeDelayTimer.IsRepeating = false;
        _nowPlayingMarqueeDelayTimer.Tick += NowPlayingMarqueeDelayTimer_Tick;

        Debug.WriteLine("=== PlayingPage Constructor END ===");
    }

    private void PlayingPage_Loaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("=== PlayingPage_Loaded START ===");
        Debug.WriteLine($"[PlayingPage] Current IsPlaying: {ViewModel.IsPlaying}");
        Debug.WriteLine($"[PlayingPage] Current SelectedStation: {ViewModel.SelectedStation?.Name ?? "null"}");
        Debug.WriteLine($"[PlayingPage] Current StreamUrl: {ViewModel.StreamUrl}");

        SyncSelectedItem();
        UpdateFavoriteButtonState();
        UpdateDragAvailability();

        // Restore volume slider visibility from persisted setting
        VolumeControlGrid.Visibility = SettingsService.IsVolumeSliderVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Find the ShellViewModel from the parent page
        _shellViewModel = FindShellViewModel();
        Debug.WriteLine($"[PlayingPage] ShellViewModel found: {_shellViewModel != null}");

        Debug.WriteLine("=== PlayingPage_Loaded END ===");

        UpdateNowPlayingMarqueeState();

        // scroll to selected station
        if (ViewModel.SelectedStation is not null)
        {
            // Measured against the rows on screen, not the stored list: a station sitting
            // below a couple of folders may be far further down than its stored position.
            int index = ViewModel.DisplayRows.IndexOf(ViewModel.SelectedStation);
            if (index is >= 0 and > MinIndexForScrolling)
            {
                StationsListView.ScrollIntoView(ViewModel.SelectedStation);
                Debug.WriteLine($"[PlayingPage] Scrolled to selected station at row {index}");
            }
            // A negative index just means the station is inside a collapsed folder, which is
            // not something to scroll to.
        }
        else
        {
            Debug.WriteLine("[PlayingPage] No SelectedStation to scroll to");
        }
    }

    private void PlayingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _nowPlayingMarqueeDelayTimer.Stop();
        _nowPlayingMarqueeDelayTimer.Tick -= NowPlayingMarqueeDelayTimer_Tick;
        NowPlayingMarqueeText.MarqueeCompleted -= NowPlayingMarqueeText_MarqueeCompleted;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _errorService.ErrorPresented -= ErrorService_ErrorPresented;
        _errorService.ErrorWithdrawn -= ErrorService_ErrorWithdrawn;
        _favoritesService.FavoritesChanged -= FavoritesService_FavoritesChanged;
        SetNowPlayingScrolling(false);
    }

    private ShellViewModel? FindShellViewModel()
    {
        // Walk up the visual tree to find ShellPage
        DependencyObject current = this;
        while (current != null)
        {
            if (current is ShellPage shellPage)
            {
                return shellPage.ViewModel;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Debug.WriteLine($"[PlayingPage] ViewModel PropertyChanged: {e.PropertyName}");

        if (e.PropertyName is nameof(PlayerViewModel.SelectedStation))
        {
            SyncSelectedItem();
        }

        if (e.PropertyName is nameof(PlayerViewModel.SortMode) or nameof(PlayerViewModel.GroupByMode))
        {
            UpdateDragAvailability();
        }

        if (e.PropertyName is (nameof(PlayerViewModel.CurrentMetadata)) or
                 (nameof(PlayerViewModel.HasNowPlaying)))
        {
            UpdateFavoriteButtonState();
        }

        if (e.PropertyName is (nameof(PlayerViewModel.NowPlaying)) or
            (nameof(PlayerViewModel.HasNowPlaying)))
        {
            UpdateNowPlayingMarqueeState();
        }

        if (e.PropertyName is nameof(PlayerViewModel.SleepTimerProgress))
        {
            UpdateSleepTimerRing();
        }

        if (e.PropertyName is nameof(PlayerViewModel.IsSleepTimerActive) && ViewModel.IsSleepTimerActive)
        {
            // Reset the hover state so a freshly-started timer shows the ring rather than
            // whatever the pointer happened to be doing to the previous one.
            SleepTimerRingHost.Opacity = 1;
            SleepTimerCancelIcon.Opacity = 0;
            UpdateSleepTimerRing();
        }
    }

    private const double SleepTimerRingDiameter = 28;
    private const double SleepTimerRingStrokeThickness = 2.5;

    /// <summary>
    /// Redraws the countdown ring's arc via Ellipse.StrokeDashArray. XAML expresses
    /// dash lengths as multiples of the stroke thickness rather than device pixels, so the
    /// circle's circumference has to be converted into that unit before it can be split into a
    /// "time remaining" dash and a "time elapsed" gap.
    /// </summary>
    private void UpdateSleepTimerRing()
    {
        double progress = Math.Clamp(ViewModel.SleepTimerProgress, 0, 1);
        double radius = (SleepTimerRingDiameter - SleepTimerRingStrokeThickness) / 2;
        double circumferenceUnits = 2 * Math.PI * radius / SleepTimerRingStrokeThickness;

        SleepTimerRingProgress.StrokeDashArray = new DoubleCollection
        {
            circumferenceUnits * progress,
            circumferenceUnits
        };
    }

    private void SleepTimerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string minutesText } && int.TryParse(minutesText, out int minutes))
        {
            Debug.WriteLine($"[PlayingPage] Starting sleep timer: {minutes} minutes");
            ViewModel.StartSleepTimer(minutes);
        }
    }

    private void SleepTimerButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Sleep timer cancelled from countdown control");
        ViewModel.CancelSleepTimer();
    }

    private void SleepTimerButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SleepTimerRingHost.Opacity = 0;
        SleepTimerCancelIcon.Opacity = 1;
    }

    private void SleepTimerButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SleepTimerRingHost.Opacity = 1;
        SleepTimerCancelIcon.Opacity = 0;
    }

    private void UpdateFavoriteButtonState()
    {
        if (FavoriteIcon == null)
            return;

        bool isFavorited = _favoritesService.IsFavorited(ViewModel.CurrentMetadata);
        FavoriteIcon.Glyph = isFavorited ? FilledStar : OutlineStar;
        Debug.WriteLine($"[PlayingPage] Favorite button updated. IsFavorited: {isFavorited}");
    }

    /// <summary>
    /// Puts a playback error on screen. Only ever reached once
    /// <see cref="PlaybackErrorService"/> has established that the failure still
    /// describes reality and that this window is visible, so no further checking
    /// belongs here.
    /// </summary>
    private async void ErrorService_ErrorPresented(object? sender, string errorMessage)
    {
        Debug.WriteLine($"[PlayingPage] Presenting playback error: {errorMessage}");

        ContentDialog dialog = new()
        {
            Title = LocalizationService.GetString("PlayingPage_PlaybackErrorTitle", "Playback Error"),
            // A plain string Content renders as a TextBlock that can't be selected, which
            // makes an error impossible to copy into a bug report. TextBlock supports
            // selection directly - no need for a full TextBox just to allow copying.
            Content = new TextBlock
            {
                Text = errorMessage,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            CloseButtonText = LocalizationService.GetString("PlayingPage_PlaybackErrorClose", "OK"),
            XamlRoot = this.XamlRoot
        };

        try
        {
            _playbackErrorDialog = dialog;
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayingPage] EXCEPTION showing error dialog: {ex.Message}");
            // If dialog fails, silently ignore
        }
        finally
        {
            // ShowAsync also returns when the service withdraws the dialog, which clears
            // the field first. Only a genuine dismissal by the user leaves our dialog in
            // place — reporting the other case would clear an error we never showed.
            if (ReferenceEquals(_playbackErrorDialog, dialog))
            {
                _playbackErrorDialog = null;
                _errorService.NotifyErrorDismissed();
            }
        }
    }

    /// <summary>
    /// Takes the error dialog down when the service decides it no longer makes sense —
    /// the station recovered, or the user moved to another one.
    /// </summary>
    private void ErrorService_ErrorWithdrawn(object? sender, EventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Withdrawing playback error dialog");
        _playbackErrorDialog?.Hide();
        _playbackErrorDialog = null;
    }

    /// <summary>
    /// Pushes the view model's selection into the list control.
    /// <para>
    /// The row highlight itself is not done here - it is bound to
    /// <see cref="RadioStation.IsSelectedStation"/> on the model. Resolving containers to
    /// paint the highlight by hand only worked while the list was flat and fully realised;
    /// collapsing, sorting and virtualisation all produce rows with no container to find.
    /// </para>
    /// </summary>
    private void SyncSelectedItem()
    {
        if (StationsListView is null)
            return;

        if (ReferenceEquals(StationsListView.SelectedItem, ViewModel.SelectedStation))
            return;

        _isSyncingSelection = true;
        try
        {
            StationsListView.SelectedItem = ViewModel.SelectedStation;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Volume is already bound two-way, but we can handle additional logic here if needed
        Debug.WriteLine($"[PlayingPage] Volume slider changed: {e.NewValue}");
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("=== PlayButton_Click START ===");
        Debug.WriteLine($"[PlayingPage] Current IsPlaying: {ViewModel.IsPlaying}");
        Debug.WriteLine($"[PlayingPage] Current selected station: {ViewModel.SelectedStation?.Name ?? "null"}");
        Debug.WriteLine($"[PlayingPage] Current stream URL: {ViewModel.StreamUrl}");

        ViewModel.Toggle();

        Debug.WriteLine($"[PlayingPage] After Toggle - IsPlaying: {ViewModel.IsPlaying}");
        Debug.WriteLine("=== PlayButton_Click END ===");
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Pop-out button clicked");

        if (Application.Current is App app)
        {
            app.ShowMiniPlayerWindow();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Close button clicked");
        // Persist any pending per-station volume change before quitting.
        ViewModel.FlushStationsSave();
        Application.Current.Exit();
    }

    private void AddStationButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Add station button clicked");

        // Use navigation service if available
        if (_shellViewModel != null)
        {
            Debug.WriteLine("[PlayingPage] Navigating to Search Station page via ShellViewModel");
            _shellViewModel.NavigateToSearchStationPage();
        }
        else
        {
            Debug.WriteLine("[PlayingPage] ShellViewModel not available, showing fallback dialog");
            // Fallback to dialog
            ShowAddStationDialog();
        }
    }

    private async void ShowAddStationDialog()
    {
        ContentDialog dialog = new()
        {
            Title = "Add Station",
            Content = "Add station functionality coming soon!",
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Settings button clicked");
        // Use navigation service if available
        _shellViewModel?.NavigateToSettingsPage();
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Info button clicked");
        // Navigate to About page
        _shellViewModel?.NavigateToAboutPage();
    }

    private void NowPlayingInfo_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Now Playing info clicked");
        // Navigate to Now Playing details page
        _shellViewModel?.NavigateToNowPlayingPage();
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Favorite button clicked");

        if (ViewModel.CurrentMetadata?.HasMetadata != true)
        {
            Debug.WriteLine("[PlayingPage] No metadata to favorite");
            return;
        }

        string stationName = ViewModel.SelectedStation?.Name ?? "Unknown Station";
        bool isFavorited = _favoritesService.ToggleFavorite(ViewModel.CurrentMetadata, stationName);
        Debug.WriteLine($"[PlayingPage] Track favorite toggled. IsFavorited: {isFavorited}");

        // UpdateFavoriteButtonState will be called via the FavoritesChanged event
    }

    private void FavoritesButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[PlayingPage] Favorites button clicked");
        _shellViewModel?.NavigateToFavoritesPage();
    }

    private void VisitSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is RadioStation station)
        {
            Debug.WriteLine($"[PlayingPage] Visit Station Site clicked: {station.Name}");
            // Navigate to AddStation page in edit mode with the station data
            _ = ViewModel.VisitWebsite(station);
        }
    }

    private void EditStation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is RadioStation station)
        {
            Debug.WriteLine($"[PlayingPage] Edit station clicked: {station.Name}");

            switch (station.SourceKind)
            {
                case AudioSourceKind.WhiteNoise:
                    // White noise has no stream URL, homepage or favicon to edit, so it gets
                    // its own page rather than the generic manual-entry window.
                    _shellViewModel?.NavigateToAddWhiteNoisePage(station);
                    return;

                case AudioSourceKind.Files:
                    // Its own pop-out window, not page navigation: re-picking a folder opens a
                    // system FolderPicker dialog, which a page hosted in the shell frame does
                    // not survive.
                    AddLocalMusicWindow editLocalMusicWindow = new();
                    WindowHelper.Track(editLocalMusicWindow);
                    editLocalMusicWindow.LoadStationForEdit(station);
                    editLocalMusicWindow.Activate();
                    return;

                case AudioSourceKind.Radio:
                default:
                    break;
            }

            // Open a pop-out window for editing so the flyout closing doesn't clear the fields
            ManualStationWindow editWindow = new();
            WindowHelper.Track(editWindow);
            editWindow.LoadStationForEdit(station);
            editWindow.Activate();
        }
    }

    private void RemoveStation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is RadioStation station)
        {
            Debug.WriteLine($"[PlayingPage] Remove station clicked: {station.Name}");
            // Remove the station immediately - no dialog since it's in a flyout
            ViewModel.RemoveStation(station);
        }
    }

    private void StationsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // This fires for every drag, including ones that never landed - dropped outside the
        // list, cancelled with Esc, or dragged away to another app. In those cases the
        // collection is unchanged and there is nothing to persist.
        if (args.DropResult != DataPackageOperation.Move)
        {
            Debug.WriteLine($"[PlayingPage] DragItemsCompleted - drop not accepted ({args.DropResult}), ignoring");
            return;
        }

        Debug.WriteLine("[PlayingPage] DragItemsCompleted - Station order changed");

        // The list control has already rewritten the rows; turn that back into the
        // arrangement, then save. Deliberately not SaveStations(): a reorder changes where a
        // station sits, not what it points at, and must not restart the stream.
        //
        // args.Items is what was actually dragged - passing it through lets a folder tell a
        // row that was just carried past it apart from one that genuinely belongs to it, so a
        // station sitting below a folder is not swept into it by some unrelated drag.
        ViewModel.ApplyDisplayReorder(args.Items);
        ViewModel.PersistStationList();

        // Re-save the selection so its stored position keeps up with the new order
        ViewModel.UpdateSelectedStationId();
    }

    private void StationsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection)
            return;

        switch (StationsListView.SelectedItem)
        {
            case RadioStation station:
                ViewModel.SelectedStation = station;
                break;

            case null:
                // A rebuild can momentarily clear the selection. Losing what is playing
                // because a folder was expanded is not acceptable, so put it back.
                SyncSelectedItem();
                break;

            default:
                // A folder or divider row. Not something that can be played, and the tap
                // handlers on those rows have already done whatever the click meant.
                SyncSelectedItem();
                break;
        }
    }

    private void GroupRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StationGroup group })
        {
            group.IsExpanded = !group.IsExpanded;
            ViewModel.RebuildDisplayRows();

            // A synthetic "Group by" folder is never written to the layout file, so there is
            // nothing to persist - only a real folder's expanded state is worth remembering.
            if (!group.IsVirtual)
                ViewModel.PersistStationList();
        }

        // Stops the row being selected: a folder header is a control, not a destination.
        e.Handled = true;
    }

    private void DividerRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // A divider is decoration. Swallowing the tap stops it stealing the selection from
        // the station that is playing.
        e.Handled = true;
    }

    private void StationsListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // An expanded folder occupies one row but owns several. The built-in reorder moves
        // only the row being dragged, which would leave the contents behind. Collapsing it
        // first makes the folder a single row that moves as a unit.
        //
        // Cancelling rather than collapsing mid-drag is deliberate: removing rows while the
        // reorder is still working out its indices is how drops land in the wrong place.
        // The cost is one extra gesture on a rare operation.
        if (e.Items.Count == 1 && e.Items[0] is StationGroup { IsExpanded: true } group)
        {
            group.IsExpanded = false;
            ViewModel.RebuildDisplayRows();
            ViewModel.PersistStationList();
            e.Cancel = true;
        }
    }

    private void PreviousTrackButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviousLocalTrack();
    }

    private void NextTrackButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NextLocalTrack();
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.BeginSeekDrag();
    }

    private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.EndSeekDrag();
    }

    private void VolumeControl_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        double change = (delta / 120.0) * 0.02;
        ViewModel.Volume = Math.Clamp(ViewModel.Volume + change, 0, 2);
        e.Handled = true;
    }

    private void ToggleVolumeSlider_Click(object sender, RoutedEventArgs e)
    {
        SetVolumeSliderVisible(VolumeControlGrid.Visibility != Visibility.Visible);
    }

    private void SetVolumeSliderVisible(bool visible)
    {
        VolumeControlGrid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SettingsService.IsVolumeSliderVisible = visible;
    }

    private void PageContextMenu_Opening(object sender, object e)
    {
        // This used to hide the whole menu when the volume slider was already showing, which
        // worked while the menu held exactly one item. Now that it carries the list commands,
        // the volume entry just flips its own wording instead.
        ShowVolumeMenuItem.Text = VolumeControlGrid.Visibility == Visibility.Visible
            ? LocalizationService.GetString("PlayingPage_HideVolumeSlider", "Hide Volume Slider")
            : LocalizationService.GetString("PlayingPage_ShowVolume.Text", "Show Volume Slider");

        // A sorted or grouped list has no meaningful place to put a new folder or divider: the
        // user is not the one deciding positions while either is on.
        bool manual = !ViewModel.IsViewSorted && !ViewModel.IsGroupedView;
        NewGroupMenuItem.IsEnabled = manual;
        NewDividerMenuItem.IsEnabled = manual;

        RefreshAllInfoMenuItem.IsEnabled = ViewModel.Stations.Count > 0;

        BuildSortMenu();
        BuildGroupByMenu();
    }

    private async void RefreshStationInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: RadioStation station })
            return;

        try
        {
            // A single lookup, so it runs straight away - there is nothing to confirm about one
            // request the user just asked for.
            StationMetadataMatchPolicy.MetadataMatch? match =
                await _backfillService.RefreshOneAsync(station, overwriteExisting: true);

            if (match is null)
            {
                await ShowInfoDialogAsync(
                    "No match found",
                    $"radio-browser.info has no entry for this station's stream address, so there are no details to add.");
                return;
            }

            ViewModel.PersistStationList();

            await ShowInfoDialogAsync(
                $"Updated {station.Name}",
                StationMetadataBackfillService.DescribeStation(station));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayingPage] Station info refresh failed: {ex.Message}");
            await ShowInfoDialogAsync("Couldn't reach radio-browser.info", "Check your connection and try again.");
        }
    }

    private async void RefreshAllStationInfo_Click(object sender, RoutedEventArgs e)
    {
        List<RadioStation> withoutDetails =
            StationMetadataBackfillService.SelectCandidates(ViewModel.Stations, overwriteExisting: false);

        CheckBox overwriteCheck = new()
        {
            Content = "Also refresh stations that already have details",
            IsChecked = false
        };

        string body = withoutDetails.Count == 0
            ? "All your stations already have details. You can look them up again to pick up any changes."
            : withoutDetails.Count == 1
                ? "One station has no genre or country saved."
                : $"{withoutDetails.Count} stations have no genre or country saved.";

        ContentDialog preflight = new()
        {
            Title = "Look up station details",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        // Says plainly what leaves the machine. This is the app's only outbound
                        // request about the user's own saved stations.
                        Text = $"{body} Traydio will ask radio-browser.info about them. Only each station's stream address is sent.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    overwriteCheck
                }
            },
            PrimaryButtonText = "Look up",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await preflight.ShowAsync() != ContentDialogResult.Primary)
            return;

        bool overwrite = overwriteCheck.IsChecked == true;
        List<RadioStation> candidates =
            StationMetadataBackfillService.SelectCandidates(ViewModel.Stations, overwrite);

        if (candidates.Count == 0)
        {
            await ShowInfoDialogAsync(
                "Nothing to look up",
                "Every station has been checked recently. Try again in a few days.");
            return;
        }

        await RunBackfillAsync(candidates, overwrite);
    }

    private async Task RunBackfillAsync(List<RadioStation> candidates, bool overwrite)
    {
        using CancellationTokenSource cts = new();

        ProgressBar progress = new() { Minimum = 0, Maximum = candidates.Count, Value = 0 };
        TextBlock status = new() { Text = "Starting…", TextWrapping = TextWrapping.Wrap };

        ContentDialog progressDialog = new()
        {
            Title = "Looking up station details",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 280,
                Children = { progress, status }
            },
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        bool finished = false;
        progressDialog.Closing += (_, args) =>
        {
            // Closing the dialog is the cancel gesture; the worker hides it itself when done.
            if (!finished)
                cts.Cancel();
        };

        _ = progressDialog.ShowAsync();

        StationMetadataBackfillService.BackfillResult result;
        try
        {
            result = await _backfillService.RefreshManyAsync(
                candidates,
                overwrite,
                onProgress: (done, total, name) =>
                {
                    progress.Value = done;
                    status.Text = string.IsNullOrEmpty(name) ? "Finishing…" : $"{done} of {total} · {name}";
                },
                onPartialSave: () => ViewModel.PersistStationList(),
                cancellationToken: cts.Token);
        }
        finally
        {
            finished = true;
            progressDialog.Hide();
        }

        await ShowInfoDialogAsync("Station details", SummarizeBackfill(result));
    }

    private static string SummarizeBackfill(StationMetadataBackfillService.BackfillResult result)
    {
        List<string> lines = [$"Updated {result.Updated} of {result.Attempted} stations."];

        if (result.NotFound > 0)
            lines.Add($"{result.NotFound} had no entry on radio-browser.info.");
        if (result.Ambiguous > 0)
            lines.Add($"{result.Ambiguous} matched more than one entry; the closest was used.");
        if (result.Skipped > 0)
            lines.Add($"{result.Skipped} were left for next time to keep the request count reasonable.");
        if (result.AbortedUnreachable)
            lines.Add("Stopped early because radio-browser.info stopped responding.");
        if (result.Cancelled)
            lines.Add("Stopped at your request. What was found so far has been saved.");

        return string.Join("\n", lines);
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void BuildSortMenu()
    {
        SortBySubItem.Items.Clear();

        foreach (StationSortMode mode in Enum.GetValues<StationSortMode>())
        {
            RadioMenuFlyoutItem item = new()
            {
                Text = StationSortPolicy.DisplayName(mode),
                GroupName = "StationSort",
                IsChecked = mode == ViewModel.SortMode,
                Tag = mode
            };
            item.Click += SortMode_Click;
            SortBySubItem.Items.Add(item);
        }
    }

    private void SortMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: StationSortMode mode })
        {
            ViewModel.SortMode = mode;
        }
    }

    private void BuildGroupByMenu()
    {
        GroupBySubItem.Items.Clear();

        foreach (StationGroupByMode mode in Enum.GetValues<StationGroupByMode>())
        {
            RadioMenuFlyoutItem item = new()
            {
                Text = StationGroupingPolicy.DisplayName(mode),
                GroupName = "StationGroupBy",
                IsChecked = mode == ViewModel.GroupByMode,
                Tag = mode
            };
            item.Click += GroupByMode_Click;
            GroupBySubItem.Items.Add(item);
        }
    }

    private void GroupByMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: StationGroupByMode mode })
        {
            ViewModel.GroupByMode = mode;
        }
    }

    private void ResetSort_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SortMode = StationSortMode.Manual;
        ViewModel.GroupByMode = StationGroupByMode.None;
    }

    /// <summary>
    /// Switches dragging off entirely while a view sort or a view grouping is active.
    /// <para>
    /// Turning it off is better than trying to interpret the drop. Under either, the rows are
    /// not in the stored order, so there is no honest answer to where a dropped row should
    /// land - and silently rewriting the user's arrangement to match a temporary view would
    /// break the one promise this feature makes.
    /// </para>
    /// </summary>
    private void UpdateDragAvailability()
    {
        bool manual = !ViewModel.IsViewSorted && !ViewModel.IsGroupedView;
        StationsListView.CanDragItems = manual;
        StationsListView.CanReorderItems = manual;
        // Also suppresses the drop indicator, so nothing suggests a drag would work.
        StationsListView.AllowDrop = manual;
    }

    /// <summary>
    /// Swallows the right-click on a synthetic "Group by" folder header entirely: there is
    /// nothing to rename or delete on a folder that exists only for the current view.
    /// </summary>
    private void GroupRow_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: StationGroup { IsVirtual: true } })
        {
            args.Handled = true;
        }
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        StationGroup group = ViewModel.CreateGroup("New group");
        // Straight into rename: a folder called "New group" is not what anyone wanted.
        BeginRename(group, group.Name, "Group name", name =>
        {
            group.Name = name;
            ViewModel.CommitLayoutEdit();
        });
    }

    private void NewDivider_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CreateDivider();
    }

    private void InsertDividerAbove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: RadioStation station })
        {
            ViewModel.CreateDivider(insertBefore: station);
        }
    }

    /// <summary>
    /// Fills in the "move to group" submenu, which can only be built once the folders are
    /// known and has to be rebuilt each time in case they have changed.
    /// </summary>
    private void StationContextMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        RadioStation? station = null;
        foreach (MenuFlyoutItemBase item in flyout.Items)
        {
            if (item is MenuFlyoutItem { Tag: RadioStation tagged })
            {
                station = tagged;
                break;
            }
        }

        MenuFlyoutSubItem? subItem = null;
        foreach (MenuFlyoutItemBase item in flyout.Items)
        {
            if (item is MenuFlyoutSubItem candidate)
            {
                subItem = candidate;
                break;
            }
        }

        if (station is null || subItem is null)
            return;

        subItem.Items.Clear();
        StationGroup? currentGroup = ViewModel.FindParentGroup(station);

        RadioMenuFlyoutItem none = new()
        {
            Text = "(None)",
            GroupName = "MoveToGroup",
            IsChecked = currentGroup is null,
            Tag = station
        };
        none.Click += MoveToGroup_Click;
        subItem.Items.Add(none);

        foreach (StationGroup group in ViewModel.Groups)
        {
            RadioMenuFlyoutItem entry = new()
            {
                Text = group.Name,
                GroupName = "MoveToGroup",
                IsChecked = ReferenceEquals(group, currentGroup),
                // Both halves of the operation, since the menu item is the only thing the
                // click handler receives.
                Tag = new StationGroupMove(station, group)
            };
            entry.Click += MoveToGroup_Click;
            subItem.Items.Add(entry);
        }

        // Folders are not on screen under a view sort or a view grouping, so moving between
        // them would be an invisible change.
        subItem.IsEnabled = subItem.Items.Count > 1 && !ViewModel.IsViewSorted && !ViewModel.IsGroupedView;
    }

    private void MoveToGroup_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as MenuFlyoutItem)?.Tag)
        {
            case StationGroupMove move:
                ViewModel.MoveStationToGroup(move.Station, move.Group);
                break;
            case RadioStation station:
                ViewModel.MoveStationToGroup(station, null);
                break;
        }
    }

    /// <summary>Pairs a station with the folder it is being moved into, for a menu item's Tag.</summary>
    private sealed record StationGroupMove(RadioStation Station, StationGroup Group);

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: StationGroup group })
        {
            BeginRename(group, group.Name, "Group name", name =>
            {
                group.Name = name;
                ViewModel.CommitLayoutEdit();
            });
        }
    }

    private void EditDividerLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: StationDivider divider })
        {
            BeginRename(divider, divider.Label ?? string.Empty, "Label (optional)", label =>
            {
                divider.Label = label;
                ViewModel.CommitLayoutEdit();
            });
        }
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: StationGroup group })
        {
            ViewModel.DeleteGroup(group);
        }
    }

    private async void DeleteGroupAndStations_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: StationGroup group })
            return;

        int count = group.StationCount;
        if (count == 0)
        {
            ViewModel.DeleteGroup(group);
            return;
        }

        // Removing one station is immediate because it is one station. Removing several at
        // once is not something to discover after the fact.
        ContentDialog confirm = new()
        {
            Title = $"Delete \"{group.Name}\"?",
            Content = count == 1
                ? "The station in this group will be removed as well. This cannot be undone."
                : $"All {count} stations in this group will be removed as well. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteGroupAndStations(group);
        }
    }

    private void RemoveDivider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: StationDivider divider })
        {
            ViewModel.DeleteDivider(divider);
        }
    }

    /// <summary>
    /// Edits a single line of text in a flyout anchored to the row.
    /// <para>
    /// Editing a station opens a separate window because a multi-field form loses its contents
    /// when the flyout it lives in closes. That does not apply here: the menu closes first, and
    /// this flyout is opened afterwards as the editor in its own right.
    /// </para>
    /// </summary>
    private void BeginRename(object row, string initialValue, string placeholder, Action<string> commit)
    {
        TextBox input = new()
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            Width = 200,
            SelectionStart = 0,
            SelectionLength = initialValue.Length
        };

        Flyout flyout = new()
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { input }
            }
        };

        bool committed = false;
        void Commit()
        {
            if (committed)
                return;
            committed = true;
            commit(input.Text.Trim());
        }

        input.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                Commit();
                flyout.Hide();
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.Escape)
            {
                committed = true; // abandon whatever was typed
                flyout.Hide();
                args.Handled = true;
            }
        };

        // Clicking away accepts what was typed rather than discarding it: the field starts out
        // holding the current value, so dismissing it as a cancel would be the surprising read.
        flyout.Closing += (_, _) => Commit();

        FrameworkElement anchor = StationsListView.ContainerFromItem(row) as FrameworkElement ?? StationsListView;
        flyout.ShowAt(anchor);
        input.Focus(FocusState.Programmatic);
    }

    private void NowPlayingTextHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNowPlayingMarqueeState();
    }

    private void NowPlayingMarqueeText_MarqueeCompleted(object? sender, object args) =>
        RestartNowPlayingMarqueeAfterDelay();

    private void FavoritesService_FavoritesChanged(object? sender, EventArgs args) =>
        UpdateFavoriteButtonState();

    private void NowPlayingMarqueeDelayTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();

        if (!ShouldUseNowPlayingMarquee())
        {
            SetNowPlayingScrolling(false);
            return;
        }

        SetNowPlayingScrolling(true);
    }

    private void UpdateNowPlayingMarqueeState()
    {
        _nowPlayingMarqueeDelayTimer.Stop();
        SetNowPlayingScrolling(false);

        if (!ShouldUseNowPlayingMarquee())
        {
            return;
        }

        _nowPlayingMarqueeDelayTimer.Start();
    }

    private void RestartNowPlayingMarqueeAfterDelay()
    {
        if (!ShouldUseNowPlayingMarquee())
        {
            return;
        }

        _nowPlayingMarqueeDelayTimer.Stop();
        _nowPlayingMarqueeDelayTimer.Start();
    }

    private bool ShouldUseNowPlayingMarquee()
    {
        return IsLoaded &&
               ViewModel.HasNowPlaying &&
               !string.IsNullOrWhiteSpace(ViewModel.NowPlaying) &&
               DoesNowPlayingOverflow();
    }

    private bool DoesNowPlayingOverflow()
    {
        if (NowPlayingTextHost.ActualWidth <= 0)
        {
            return false;
        }

        TextBlock measurementTextBlock = new()
        {
            CharacterSpacing = NowPlayingText.CharacterSpacing,
            FontFamily = NowPlayingText.FontFamily,
            FontSize = NowPlayingText.FontSize,
            FontStretch = NowPlayingText.FontStretch,
            FontStyle = NowPlayingText.FontStyle,
            FontWeight = NowPlayingText.FontWeight,
            Text = ViewModel.NowPlaying,
            TextWrapping = TextWrapping.NoWrap
        };

        measurementTextBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        return measurementTextBlock.DesiredSize.Width > NowPlayingTextHost.ActualWidth;
    }

    private void SetNowPlayingScrolling(bool isScrolling)
    {
        if (isScrolling)
        {
            NowPlayingText.Visibility = Visibility.Collapsed;
            NowPlayingMarqueeText.Visibility = Visibility.Visible;
            if (IsLoaded)
            {
                NowPlayingMarqueeText.StartMarquee();
            }
            return;
        }

        NowPlayingMarqueeText.Visibility = Visibility.Collapsed;
        NowPlayingText.Visibility = Visibility.Visible;
        if (IsLoaded)
        {
            NowPlayingMarqueeText.StopMarquee();
        }
    }
}
