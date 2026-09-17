using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Trdo.Models;
using Trdo.Services;
using Trdo.ViewModels;

namespace Trdo.Pages;

/// <summary>
/// Page for creating or editing a white noise "station".
/// </summary>
public sealed partial class AddWhiteNoise : Page
{
    public AddWhiteNoiseViewModel ViewModel { get; }
    private ShellViewModel? _shellViewModel;

    public AddWhiteNoise()
    {
        InitializeComponent();
        ViewModel = new AddWhiteNoiseViewModel();
        DataContext = ViewModel;

        ViewModel.SetPlayerViewModel(PlayerViewModel.Shared);

        Loaded += AddWhiteNoise_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is RadioStation station)
        {
            ViewModel.LoadStationForEdit(station);
        }
    }

    private void AddWhiteNoise_Loaded(object sender, RoutedEventArgs e)
    {
        _shellViewModel = FindShellViewModel();
        StationNameTextBox.Focus(FocusState.Programmatic);
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Save())
        {
            _shellViewModel?.NavigateToPlayingPage();
            NavigationService.Instance.ClearBackStack();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _shellViewModel?.GoBack();
    }
}
