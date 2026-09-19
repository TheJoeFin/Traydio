using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Trdo.Services;
using Trdo.Services.Lastfm;
using Windows.System;
using WinUIEx;

namespace Trdo.Controls;

/// <summary>
/// Drives Last.fm's desktop authentication flow: requests a token, opens the browser for the
/// user to approve it, then exchanges the approved token for a session once they select
/// Continue. There is no protocol activation registered for this app (see Package.appxmanifest),
/// so "the user approved it" is a manual step here rather than an automatic callback.
/// </summary>
public sealed partial class LastfmAuthWindow : WindowEx
{
    private string? _token;

    public LastfmAuthWindow()
    {
        InitializeComponent();

        Title = LocalizationService.GetString("LastfmAuth_WindowTitle", "Connect to Last.fm");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ModernTitlebar);

        Activated += LastfmAuthWindow_Activated;
    }

    private async void LastfmAuthWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            return;

        Activated -= LastfmAuthWindow_Activated;
        WindowPlacementService.PositionWindowNearAnchor(this, 380, 320);

        await StartAuthAsync();
    }

    private async Task StartAuthAsync()
    {
        SetBusy(true, LocalizationService.GetString("LastfmAuth_RequestingToken", "Connecting to Last.fm..."));

        LastfmResult<string> tokenResult = await LastfmAuthService.RequestTokenAsync();
        if (!tokenResult.IsSuccess || string.IsNullOrWhiteSpace(tokenResult.Value))
        {
            ShowError(tokenResult.ErrorMessage ?? LocalizationService.GetString(
                "LastfmAuth_GenericError", "Could not connect to Last.fm. Please try again later."));
            SetBusy(false, null);
            return;
        }

        _token = tokenResult.Value;
        Uri? authorizeUri = LastfmAuthService.BuildAuthorizeUri(_token);
        if (authorizeUri is null)
        {
            ShowError(LocalizationService.GetString(
                "LastfmAuth_NotConfigured", "Last.fm is not configured for this build."));
            SetBusy(false, null);
            return;
        }

        await Launcher.LaunchUriAsync(authorizeUri);

        StatusTextBlock.Text = LocalizationService.GetString(
            "LastfmAuth_WaitingForApproval",
            "Approve access to your Last.fm account in the browser window that just opened, then come back here and select Continue.");
        SetBusy(false, null);
        ContinueButton.IsEnabled = true;
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_token is null)
            return;

        ContinueButton.IsEnabled = false;
        SetBusy(true, LocalizationService.GetString("LastfmAuth_Finishing", "Finishing up..."));

        LastfmResult<string> result = await LastfmAuthService.CompleteAuthAsync(_token);
        if (result.IsSuccess)
        {
            LastfmScrobbleService.Instance.FlushPendingQueue();
            Close();
            return;
        }

        ShowError(LocalizationService.GetString(
            "LastfmAuth_NotYetApproved",
            "Last.fm hasn't seen your approval yet. Make sure you approved access in the browser, then select Continue again."));
        SetBusy(false, null);
        ContinueButton.IsEnabled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool isBusy, string? statusText)
    {
        WorkingProgressRing.IsActive = isBusy;
        if (statusText is not null)
            StatusTextBlock.Text = statusText;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }
}
