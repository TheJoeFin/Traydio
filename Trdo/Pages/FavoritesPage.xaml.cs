using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using Trdo.Models;
using Trdo.Services;
using Trdo.ViewModels;
using Windows.System;
using Windows.Win32;

namespace Trdo.Pages;

/// <summary>
/// A page that displays the user's favorited tracks.
/// </summary>
public sealed partial class FavoritesPage : Page
{
    private ListViewItem? _previouslySelectedContainer;
    private ShellViewModel? _shellViewModel;
    private static bool HasEnabledMusicServices =>
        SettingsService.IsSpotifyEnabled ||
        SettingsService.IsDiscogsEnabled ||
        SettingsService.IsAppleMusicEnabled ||
        SettingsService.IsYouTubeMusicEnabled ||
        SettingsService.IsBandcampEnabled ||
        SettingsService.IsQobuzEnabled;

    public FavoritesViewModel ViewModel { get; }

    public FavoritesPage()
    {
        Debug.WriteLine("=== FavoritesPage Constructor START ===");

        InitializeComponent();
        ViewModel = new FavoritesViewModel();
        DataContext = ViewModel;
        Loaded += FavoritesPage_Loaded;
        Unloaded += FavoritesPage_Unloaded;
        SettingsService.MusicSearchServicesChanged += SettingsService_MusicSearchServicesChanged;

        Debug.WriteLine($"[FavoritesPage] ViewModel created with {ViewModel.Favorites.Count} favorites");
        Debug.WriteLine("=== FavoritesPage Constructor END ===");
    }

    private void FavoritesPage_Loaded(object sender, RoutedEventArgs e)
    {
        _shellViewModel = FindShellViewModel();
        Debug.WriteLine($"[FavoritesPage] ShellViewModel found: {_shellViewModel != null}");
    }

    private void FavoritesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.MusicSearchServicesChanged -= SettingsService_MusicSearchServicesChanged;
    }

    private void SettingsService_MusicSearchServicesChanged(object? sender, EventArgs e)
    {
        if (_previouslySelectedContainer != null)
        {
            ApplyMusicServiceVisibility(_previouslySelectedContainer);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Favorites.Count == 0)
        {
            ShowInfo(InfoBarSeverity.Informational, "Nothing to export", "Favorite some songs first.");
            return;
        }

        int unarchived = ViewModel.UnarchivedCount;
        int total = ViewModel.Favorites.Count;

        ToggleSwitch exportAllToggle = new()
        {
            Header = "Export all favorites",
            OffContent = $"New only ({unarchived} of {total})",
            OnContent = $"All favorites ({total})",
            // Nothing new to send means the only useful export is a full one.
            IsOn = unarchived == 0,
        };

        CheckBox archiveCheckBox = new()
        {
            Content = "Archive exported tracks",
            IsChecked = true,
        };

        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "CSV works with playlist transfer services like Soundiiz or TuneMyMusic. "
                 + "XSPF is a playlist file for players such as VLC. Pick the format when you save.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(exportAllToggle);
        content.Children.Add(archiveCheckBox);
        content.Children.Add(new TextBlock
        {
            Text = "Archived tracks stay in your favorites but are skipped by later exports.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Export favorites",
            Content = content,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        bool exportAll = exportAllToggle.IsOn;
        bool archive = archiveCheckBox.IsChecked == true;

        if (!exportAll && unarchived == 0)
        {
            ShowInfo(InfoBarSeverity.Informational, "Nothing new to export",
                "Every favorite has already been exported. Turn on \"Export all favorites\" or clear the export history.");
            return;
        }

        try
        {
            FavoritesExportResult result = await ViewModel.ExportAsync(PInvoke.GetActiveWindow(), exportAll, archive);

            if (!result.Succeeded)
                return;

            string message = $"Exported {result.ExportedCount} track{(result.ExportedCount == 1 ? "" : "s")} to {result.FileName}.";
            if (result.ArchivedCount > 0)
                message += $" {result.ArchivedCount} archived and will be skipped next time.";

            ShowInfo(InfoBarSeverity.Success, "Export complete", message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FavoritesPage] Export failed: {ex.Message}");
            ShowInfo(InfoBarSeverity.Error, LocalizationService.GetString("FavoritesPage_ExportFailed", "Export failed"), ex.Message);
        }
    }

    private void ClearExportHistory_Click(object sender, RoutedEventArgs e)
    {
        int cleared = ViewModel.ClearAllArchived();
        ShowInfo(InfoBarSeverity.Informational, "Export history cleared",
            $"{cleared} track{(cleared == 1 ? "" : "s")} will be included in the next export.");
    }

    private void ClearArchivedForTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is FavoriteTrack track)
        {
            ViewModel.ClearArchived(track);
        }
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        ExportInfoBar.Severity = severity;
        ExportInfoBar.Title = title;
        ExportInfoBar.Message = message;
        ExportInfoBar.IsOpen = true;
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is FavoriteTrack track)
        {
            Debug.WriteLine($"[FavoritesPage] Remove clicked for: {track.DisplayText}");
            ViewModel.RemoveFavorite(track);
        }
    }

    private void FavoritesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Collapse the previously selected item
        if (_previouslySelectedContainer != null)
        {
            Grid? previousExpandedContent = FindDescendant<Grid>(_previouslySelectedContainer, "ExpandedContent");
            previousExpandedContent?.Visibility = Visibility.Collapsed;
        }

        // Expand the newly selected item
        if (sender is ListView listView && listView.SelectedItem is FavoriteTrack)
        {
            ListViewItem? container = listView.ContainerFromItem(listView.SelectedItem) as ListViewItem;
            if (container != null)
            {
                ApplyMusicServiceVisibility(container);
                _previouslySelectedContainer = container;
            }
        }
        else
        {
            _previouslySelectedContainer = null;
        }
    }

    private async void SpotifyLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.Tag is FavoriteTrack track)
        {
            Debug.WriteLine($"[FavoritesPage] Spotify search for: {track.DisplayText}");
            string searchQuery = Uri.EscapeDataString(track.DisplayText);

            // Try Spotify app first
            string spotifyAppUri = $"spotify:search:{searchQuery}";
            try
            {
                bool success = await Launcher.LaunchUriAsync(new Uri(spotifyAppUri));
                if (!success)
                {
                    // Fall back to web
                    string webUrl = $"https://open.spotify.com/search/{searchQuery}";
                    await Launcher.LaunchUriAsync(new Uri(webUrl));
                }
            }
            catch
            {
                // Fall back to web
                string webUrl = $"https://open.spotify.com/search/{searchQuery}";
                await Launcher.LaunchUriAsync(new Uri(webUrl));
            }
        }
    }

    private async void DiscogsLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.Tag is FavoriteTrack track)
        {
            Debug.WriteLine($"[FavoritesPage] Discogs search for: {track.DisplayText}");
            string searchQuery = Uri.EscapeDataString(track.DisplayText);
            string url = $"https://www.discogs.com/search?q={searchQuery}";
            await Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private async void AppleMusicLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.Tag is FavoriteTrack track)
        {
            Debug.WriteLine($"[FavoritesPage] Apple Music search for: {track.DisplayText}");
            await MusicSearchLinkService.LaunchAppleMusicWebSearchAsync(track.DisplayText);
        }
    }

    private async void YouTubeMusicLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.Tag is FavoriteTrack track)
        {
            Debug.WriteLine($"[FavoritesPage] YouTube Music search for: {track.DisplayText}");
            string searchQuery = Uri.EscapeDataString(track.DisplayText);
            string url = $"https://music.youtube.com/search?q={searchQuery}";
            await Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private async void BandcampLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.Tag is FavoriteTrack track)
        {
            Debug.WriteLine($"[FavoritesPage] Bandcamp search for: {track.DisplayText}");
            string searchQuery = Uri.EscapeDataString(track.DisplayText);
            string url = $"https://bandcamp.com/search?q={searchQuery}";
            await Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private async void QobuzLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.Tag is FavoriteTrack track)
        {
            Debug.WriteLine($"[FavoritesPage] Qobuz search for: {track.DisplayText}");
            string searchQuery = Uri.EscapeDataString(track.DisplayText);
            string url = $"https://www.qobuz.com/us-en/search?q={searchQuery}";
            await Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[FavoritesPage] Music service settings button clicked");
        _shellViewModel?.NavigateToSettingsPage();
    }

    private void SetButtonVisibility(DependencyObject container, string buttonName, bool isVisible)
    {
        HyperlinkButton? button = container is FrameworkElement element
            ? element.FindName(buttonName) as HyperlinkButton
            : null;

        button?.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyMusicServiceVisibility(ListViewItem container)
    {
        Grid? expandedContent = FindDescendant<Grid>(container, "ExpandedContent");
        if (expandedContent == null)
        {
            return;
        }

        expandedContent.Visibility = HasEnabledMusicServices
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetButtonVisibility(expandedContent, "SpotifyButton", SettingsService.IsSpotifyEnabled);
        SetButtonVisibility(expandedContent, "DiscogsButton", SettingsService.IsDiscogsEnabled);
        SetButtonVisibility(expandedContent, "AppleMusicButton", SettingsService.IsAppleMusicEnabled);
        SetButtonVisibility(expandedContent, "YouTubeMusicButton", SettingsService.IsYouTubeMusicEnabled);
        SetButtonVisibility(expandedContent, "BandcampButton", SettingsService.IsBandcampEnabled);
        SetButtonVisibility(expandedContent, "QobuzButton", SettingsService.IsQobuzEnabled);
    }

    private T? FindDescendant<T>(DependencyObject parent, string name = "") where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            if (child is T typedChild)
            {
                if (string.IsNullOrEmpty(name) || (child is FrameworkElement fe && fe.Name == name))
                {
                    return typedChild;
                }
            }

            T? result = FindDescendant<T>(child, name);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private ShellViewModel? FindShellViewModel()
    {
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
}
