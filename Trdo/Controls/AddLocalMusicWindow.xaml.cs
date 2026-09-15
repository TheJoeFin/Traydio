using Trdo.Models;
using Trdo.Services;
using Trdo.ViewModels;
using WinRT.Interop;
using WinUIEx;

namespace Trdo.Controls;

/// <summary>
/// A small standalone window for creating or editing a local music "station". Opens as a
/// pop-out window, like <see cref="ManualStationWindow"/>, rather than a page navigated to in
/// the main shell's frame - picking a folder opens a system <c>FolderPicker</c> dialog, and a
/// page hosted in the shell frame does not survive that the way an independent top-level
/// window does.
/// </summary>
public sealed partial class AddLocalMusicWindow : WindowEx
{
    public AddLocalMusicViewModel ViewModel { get; }

    public AddLocalMusicWindow()
    {
        InitializeComponent();

        ViewModel = new AddLocalMusicViewModel();
        ViewModel.SetPlayerViewModel(PlayerViewModel.Shared);

        Title = LocalizationService.GetString("ManualStationWindow_Title", "Traydio");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ModernTitlebar);

        Activated += AddLocalMusicWindow_Activated;
    }

    /// <summary>Opens the window pre-filled with the given station's data for editing.</summary>
    public void LoadStationForEdit(RadioStation station)
    {
        ViewModel.LoadStationForEdit(station);
    }

    private void AddLocalMusicWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
        {
            WindowPlacementService.PositionWindowNearAnchor(this, 400, 420);
            StationNameTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            Activated -= AddLocalMusicWindow_Activated;
        }
    }

    private async void BrowseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        await ViewModel.PickFolderAsync(hwnd);
    }

    private void SaveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.Save())
        {
            Close();
        }
    }

    private void CancelButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Close();
    }

    private void WindowEx_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (App.Current is App currentApp)
            currentApp.ShowFlyout();
    }
}
