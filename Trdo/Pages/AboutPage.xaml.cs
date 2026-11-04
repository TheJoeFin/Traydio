using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Trdo.Controls;
using Trdo.ViewModels;

namespace Trdo.Pages;

public sealed partial class AboutPage : Page
{
    public AboutViewModel ViewModel { get; }

    public AboutPage()
    {
        InitializeComponent();
        ViewModel = new AboutViewModel();
        DataContext = ViewModel;
    }

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenGitHub();
    }

    private void DeveloperGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenDeveloperGitHub();
    }

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenRatingWindow();
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tagString && int.TryParse(tagString, out int rating))
        {
            ViewModel.SelectedRating = rating;
            UpdateStarVisuals(rating);

            // If 5 stars, immediately launch the store review
            if (rating >= 4)
            {
                _ = ViewModel.OpenRatingWindow();
            }
        }
    }

    private void UpdateStarVisuals(int selectedRating)
    {
        // Update all star buttons to show filled or outline based on rating
        UpdateStarButton("Star1", selectedRating >= 1);
        UpdateStarButton("Star2", selectedRating >= 2);
        UpdateStarButton("Star3", selectedRating >= 3);
        UpdateStarButton("Star4", selectedRating >= 4);
        UpdateStarButton("Star5", selectedRating >= 5);
    }

    private void UpdateStarButton(string buttonName, bool isFilled)
    {
        if (FindName(buttonName) is Button starButton && starButton.Content is FontIcon icon)
        {
            // E735 is filled star, E734 is outline star
            icon.Glyph = isFilled ? "\uE735" : "\uE734";
        }
    }

    private void ContactDeveloperButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.ContactDeveloper();
    }

    private void TutorialButton_Click(object sender, RoutedEventArgs e)
    {
        TutorialWindow tutorialWindow = new();
        tutorialWindow.Activate();
    }

    private void RadioBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenRadioBrowser();
    }

    private void WinUIExButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenWinUIEx();
    }

    private void CommunityToolkitButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenCommunityToolkit();
    }

    private void RoomRelayButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenRoomRelay();
    }

    private void BuyMeACoffeeButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.OpenBuyMeACoffee();
    }
}
