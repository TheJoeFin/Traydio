using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Trdo.Models;
using Trdo.Pages;
using Trdo.Services;

namespace Trdo.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly NavigationService _navigationService;

    public ShellViewModel()
    {
        _navigationService = NavigationService.Instance;
        _navigationService.PropertyChanged += (s, e) =>
              {
                  if (e.PropertyName == nameof(NavigationService.CanGoBack))
                  {
                      OnPropertyChanged(nameof(CanGoBack));
                  }
              };
    }

    public NavigationService NavigationService => _navigationService;

    public Frame? ContentFrame
    {
        get => _navigationService.Frame;
        set => _navigationService.Frame = value;
    }

    public bool CanGoBack => _navigationService.CanGoBack;

    [RelayCommand]
    public void NavigateToPlayingPage()
    {
        _navigationService.Navigate(typeof(PlayingPage));
    }

    /// <summary>
    /// Returns to the root page and drops any history, so a reopened window
    /// never starts out showing a back button.
    /// </summary>
    public void ResetToPlayingPage()
    {
        _navigationService.ResetTo(typeof(PlayingPage));
    }

    [RelayCommand]
    public void NavigateToSettingsPage()
    {
        _navigationService.Navigate(typeof(SettingsPage));
    }

    [RelayCommand]
    public void NavigateToSearchStationPage()
    {
        _navigationService.Navigate(typeof(SearchStation));
    }

    [RelayCommand]
    public void NavigateToAddStationPage(RadioStation? stationToEdit = null)
    {
        _navigationService.Navigate(typeof(AddStation), stationToEdit);
    }

    public void NavigateToAddStationPage(RadioBrowserStation? searchResult)
    {
        _navigationService.Navigate(typeof(AddStation), searchResult);
    }

    [RelayCommand]
    public void NavigateToAddWhiteNoisePage(RadioStation? stationToEdit = null)
    {
        _navigationService.Navigate(typeof(AddWhiteNoise), stationToEdit);
    }

    [RelayCommand]
    public void NavigateToAboutPage()
    {
        _navigationService.Navigate(typeof(AboutPage));
    }

    [RelayCommand]
    public void NavigateToNowPlayingPage()
    {
        // Suppressed so it doesn't play alongside the connected animation that
        // morphs the now-playing icon from the compact bar into the full card.
        _navigationService.Navigate(typeof(NowPlayingPage), null, new SuppressNavigationTransitionInfo());
    }

    [RelayCommand]
    public void NavigateToFavoritesPage()
    {
        _navigationService.Navigate(typeof(FavoritesPage));
    }

    [RelayCommand]
    public void GoBack()
    {
        // Leaving NowPlayingPage reverses the connected animation that brought us here;
        // the default slide-back transition would otherwise play at the same time.
        if (ContentFrame?.Content is NowPlayingPage)
        {
            _navigationService.GoBack(new SuppressNavigationTransitionInfo());
        }
        else
        {
            _navigationService.GoBack();
        }
    }
}
