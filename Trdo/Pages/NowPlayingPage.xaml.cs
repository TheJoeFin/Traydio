using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Trdo.Models;
using Trdo.ViewModels;

namespace Trdo.Pages;

/// <summary>
/// A page that displays detailed stream metadata (now playing) information.
/// </summary>
public sealed partial class NowPlayingPage : Page
{
    private ShellViewModel? _shellViewModel;

    public NowPlayingViewModel ViewModel { get; }

    public NowPlayingPage()
    {
        Debug.WriteLine("=== NowPlayingPage Constructor START ===");

        InitializeComponent();
        ViewModel = new NowPlayingViewModel();
        DataContext = ViewModel;
        Loaded += NowPlayingPage_Loaded;

        Debug.WriteLine("[NowPlayingPage] ViewModel created and DataContext set");
        Debug.WriteLine($"[NowPlayingPage] Current metadata: {ViewModel.DisplayText}");
        Debug.WriteLine("=== NowPlayingPage Constructor END ===");
    }

    private void NowPlayingPage_Loaded(object sender, RoutedEventArgs e)
    {
        _shellViewModel = FindShellViewModel();
        Debug.WriteLine($"[NowPlayingPage] ShellViewModel found: {_shellViewModel != null}");
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

    private async void DiscogsLink_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NowPlayingPage] Discogs link clicked");
        await ViewModel.SearchOnDiscogs();
    }

    private async void SpotifyLink_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NowPlayingPage] Spotify link clicked");
        await ViewModel.SearchOnSpotify();
    }

    private async void AppleMusicLink_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NowPlayingPage] Apple Music link clicked");
        await ViewModel.SearchOnAppleMusic();
    }

    private async void YouTubeMusicLink_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NowPlayingPage] YouTube Music link clicked");
        await ViewModel.SearchOnYouTubeMusic();
    }

    private async void BandcampLink_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NowPlayingPage] Bandcamp link clicked");
        await ViewModel.SearchOnBandcamp();
    }

    private void FavoriteCurrentTrack_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NowPlayingPage] Favorite current track clicked");
        ViewModel.ToggleCurrentTrackFavorite();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[NowPlayingPage] Music service settings button clicked");
        _shellViewModel?.NavigateToSettingsPage();
    }

    private void FavoriteHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is PlaylistHistoryItem item)
        {
            Debug.WriteLine($"[NowPlayingPage] Favorite history item clicked: {item.DisplayText}");
            ViewModel.ToggleHistoryItemFavorite(item);
        }
    }

    private void LocalTrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedIndex: >= 0 } listView &&
            listView.SelectedIndex != ViewModel.CurrentLocalTrackIndex)
        {
            Debug.WriteLine($"[NowPlayingPage] Local track row selected: {listView.SelectedIndex}");
            ViewModel.PlayLocalTrackAtIndex(listView.SelectedIndex);
        }
    }
}
