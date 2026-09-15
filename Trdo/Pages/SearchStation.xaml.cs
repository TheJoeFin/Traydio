using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Controls;
using Trdo.Models;
using Trdo.Services;
using Trdo.ViewModels;

namespace Trdo.Pages;

/// <summary>
/// Page for searching radio stations from RadioBrowser API.
/// </summary>
public sealed partial class SearchStation : Page
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    public SearchStationViewModel ViewModel { get; }
    private ShellViewModel? _shellViewModel;
    private Button? _activePreviewButton;
    private string? _previewingStationUrl;
    private CancellationTokenSource? _previewTransitionCts;

    public SearchStation()
    {
        InitializeComponent();
        ViewModel = new SearchStationViewModel();
        DataContext = ViewModel;

        Loaded += SearchStation_Loaded;
        Unloaded += SearchStation_Unloaded;
    }

    private void SearchStation_Loaded(object sender, RoutedEventArgs e)
    {
        // Find the ShellViewModel from the parent page
        _shellViewModel = FindShellViewModel();
        SearchTextBox.Focus(FocusState.Programmatic);

        RadioPlayerService.Instance.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    private async void SearchStation_Unloaded(object sender, RoutedEventArgs e)
    {
        RadioPlayerService.Instance.PlaybackStateChanged -= OnPlaybackStateChanged;
        await StopPreviewAsync();
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

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not RadioBrowserStation station)
            return;

        string stationUrl = station.GetStreamUrl();

        if (_previewingStationUrl == stationUrl &&
            (RadioPlayerService.Instance.IsPlaying || RadioPlayerService.Instance.IsBuffering))
        {
            // Same station is playing, pause it
            await StopPreviewAsync();
            return;
        }

        // Reset old preview button icon if switching stations
        if (_activePreviewButton != null && _activePreviewButton != button)
        {
            SetButtonGlyph(_activePreviewButton, PlayGlyph);
        }

        _activePreviewButton = button;
        _previewingStationUrl = stationUrl;
        SetButtonGlyph(button, PauseGlyph);

        _previewTransitionCts?.Cancel();
        CancellationTokenSource transitionCts = new();
        _previewTransitionCts = transitionCts;

        try
        {
            await RadioPlayerService.Instance.TransitionToStationAsync(
                stationUrl,
                station.Name,
                station.Favicon,
                RadioPlayerService.Instance.Volume,
                playAfterSwitch: true,
                cancellationToken: transitionCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_previewTransitionCts, transitionCts))
            {
                SetButtonGlyph(button, PlayGlyph);
                _activePreviewButton = null;
                _previewingStationUrl = null;
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_previewTransitionCts, transitionCts))
            {
                _previewTransitionCts = null;
            }

            transitionCts.Dispose();
        }
    }

    private void OnPlaybackStateChanged(object? sender, bool isPlaying)
    {
        if (_activePreviewButton == null)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activePreviewButton != null)
            {
                SetButtonGlyph(_activePreviewButton, isPlaying ? PauseGlyph : PlayGlyph);
            }
        });
    }

    private static void SetButtonGlyph(Button button, string glyph)
    {
        if (button.Content is FontIcon icon)
        {
            icon.Glyph = glyph;
        }
    }

    private async Task StopPreviewAsync()
    {
        if (_previewingStationUrl != null)
        {
            Button? previewButton = _activePreviewButton;
            _activePreviewButton = null;
            _previewingStationUrl = null;
            if (previewButton is not null)
            {
                SetButtonGlyph(previewButton, PlayGlyph);
            }

            _previewTransitionCts?.Cancel();
            CancellationTokenSource transitionCts = new();
            _previewTransitionCts = transitionCts;

            try
            {
                RadioStation? selectedStation = PlayerViewModel.Shared.SelectedStation;
                if (selectedStation is not null)
                {
                    await RadioPlayerService.Instance.TransitionToStationAsync(
                        selectedStation.StreamUrl,
                        selectedStation.Name,
                        selectedStation.FaviconUrl,
                        selectedStation.Volume,
                        playAfterSwitch: false,
                        sourceKind: selectedStation.SourceKind,
                        whiteNoiseColor: selectedStation.WhiteNoiseColor,
                        cancellationToken: transitionCts.Token);
                }
                else
                {
                    await RadioPlayerService.Instance.FadeOutAndPauseAsync(transitionCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_previewTransitionCts, transitionCts))
                {
                    _previewTransitionCts = null;
                }

                transitionCts.Dispose();
            }
        }
    }

    private async void AddStationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is RadioBrowserStation station)
        {
            await StopPreviewAsync();

            PlayerViewModel.Shared.AddStation(station.ToRadioStation());

            // Save right away and return to the main page
            _shellViewModel?.NavigateToPlayingPage();
        }
    }

    private async void EditStationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is RadioBrowserStation station)
        {
            await StopPreviewAsync();
            // Navigate to AddStation page pre-filled so details can be edited before saving
            _shellViewModel?.NavigateToAddStationPage(station);
        }
    }

    private async void FilterButton_Checked(object sender, RoutedEventArgs e)
    {
        // Focus the picker's own box so the panel is ready to be typed into: reaching a value
        // by typing is the whole point of the panel, and a click to focus first would undo it.
        FilterQueryTextBox.Focus(FocusState.Programmatic);

        // Fetch the country/language/genre lists the first time the panel opens.
        await ViewModel.LoadFilterOptionsAsync();
    }

    /// <summary>
    /// Picking a suggestion applies it as a chip. The panel deliberately stays open: filters
    /// are usually stacked ("Germany" then "jazz"), and closing after each one would mean
    /// reopening to add the next.
    /// </summary>
    private void FilterSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is StationFilterOption option)
        {
            ViewModel.AddFilter(option);
        }
    }

    private void RemoveFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is IStationFilterChip chip)
        {
            ViewModel.RemoveChip(chip);
        }
    }

    private void DoneFilteringButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsFilterPanelOpen = false;
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearFilters();
    }

    private async void ManualEntryButton_Click(object sender, RoutedEventArgs e)
    {
        await StopPreviewAsync();
        // Open a pop-out window for manual station entry so the flyout closing doesn't clear the fields
        ManualStationWindow addWindow = new();
        WindowHelper.Track(addWindow);
        addWindow.Activate();
    }

    private async void AddWhiteNoiseButton_Click(object sender, RoutedEventArgs e)
    {
        await StopPreviewAsync();
        _shellViewModel?.NavigateToAddWhiteNoisePage();
    }

    private async void AddLocalMusicButton_Click(object sender, RoutedEventArgs e)
    {
        await StopPreviewAsync();

        // Pop-out window, not page navigation: picking a folder opens a system FolderPicker
        // dialog, which a page hosted in the shell frame does not survive.
        AddLocalMusicWindow addWindow = new();
        WindowHelper.Track(addWindow);
        addWindow.Activate();
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        await StopPreviewAsync();
        // Navigate back without adding
        _shellViewModel?.GoBack();
    }
}
