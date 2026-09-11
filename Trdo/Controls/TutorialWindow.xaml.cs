using Trdo.Services;
using WinUIEx;

namespace Trdo.Controls;
/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class TutorialWindow : WindowEx
{
    public TutorialWindow()
    {
        InitializeComponent();

        Title = LocalizationService.GetString("TutorialWindow_Title", "Traydio");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ModernTitlebar);
        Activated += TutorialWindow_Activated;
    }

    private void TutorialWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            return;

        WindowPlacementService.PositionWindowNearAnchor(this, 400, 600);
        Activated -= TutorialWindow_Activated;
    }

    private void Button_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Mark first run as complete
        SettingsService.MarkFirstRunComplete();

        Close();

        if (App.Current is App currentApp)
            currentApp.ShowFlyout();
    }
}
