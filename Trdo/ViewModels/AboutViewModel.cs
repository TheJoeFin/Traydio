using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Trdo.Services;
using Windows.ApplicationModel;
using Windows.System;

namespace Trdo.ViewModels;

public partial class AboutViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private int _selectedRating;
    public int SelectedRating
    {
        get => _selectedRating;
        set
        {
            if (_selectedRating != value)
            {
                _selectedRating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowContactDeveloper));
            }
        }
    }

    public bool ShowContactDeveloper => SelectedRating > 0 && SelectedRating <= 3;

    public string AppName => "Traydio";
    public string AppDescription => Trdo.Services.LocalizationService.GetString(
        "AboutPage_AppDescription", "A simple, elegant internet radio player for Windows");
    public string Version
    {
        get
        {
            PackageVersion version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    // ---- Updates (GitHub installs only) ----
    public bool IsGitHubInstall { get; } = AppInstallerUpdateService.IsGitHubInstall;

    private AppUpdateStatus _updateStatus = AppUpdateStatus.Unknown;
    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCheck));
            OnPropertyChanged(nameof(CanRestart));
        }
    }

    public bool CanCheck => !IsBusy;
    public bool CanRestart => !IsBusy && _updateStatus == AppUpdateStatus.Available;

    public string UpdateStatusText => _updateStatus switch
    {
        AppUpdateStatus.UpToDate => LocalizationService.GetString("AboutPage_UpdateUpToDate", "You're up to date."),
        AppUpdateStatus.Available => LocalizationService.GetString("AboutPage_UpdateAvailable", "An update is available."),
        AppUpdateStatus.Error => LocalizationService.GetString("AboutPage_UpdateError", "Couldn't check for updates."),
        _ => string.Empty,
    };

    public async Task CheckForUpdates()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            _updateStatus = await AppInstallerUpdateService.CheckAsync();
        }
        finally
        {
            OnPropertyChanged(nameof(UpdateStatusText));
            IsBusy = false;
        }
    }

    public async Task RestartToUpdate()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        bool ok = await AppInstallerUpdateService.RestartToUpdateAsync();
        if (!ok)
        {
            _updateStatus = AppUpdateStatus.Error;
            OnPropertyChanged(nameof(UpdateStatusText));
            IsBusy = false;
        }
        // On success Windows shuts the app down and relaunches it on the new version.
    }

    public string GitHubUrl =>"https://github.com/TheJoeFin/Trdo";
    public string GitHubDisplayText => "github.com/TheJoeFin/Trdo";

    public string DeveloperName => "Joe Finney (TheJoeFin)";
    public string DeveloperGitHub => "https://github.com/TheJoeFin";

    public string RadioBrowserName => "Radio Browser";
    public string RadioBrowserUrl => "https://www.radio-browser.info";
    public string RadioBrowserDisplayText => "radio-browser.info";
    public string RadioBrowserDescription => LocalizationService.GetString("AboutPage_RadioBrowserDescription", "Community-driven internet radio database with thousands of stations worldwide");

    public string WinUIExUrl => "https://github.com/dotMorten/WinUIEx";
    public string WinUIExDisplayText => "WinUIEx";

    public string CommunityToolkitUrl => "https://github.com/CommunityToolkit/dotnet";
    public string CommunityToolkitDisplayText => "CommunityToolkit.Mvvm";

    public string RoomRelayUrl => "https://github.com/guicn555/RoomRelay";
    public string RoomRelayDisplayText => "RoomRelay by guicn555";

    public string BuyMeACoffeeUrl => "https://buymeacoffee.com/thejoefin";
    public string BuyMeACoffeeDisplayText => "buymeacoffee.com/thejoefin";

    public async Task OpenGitHub()
    {
        await Launcher.LaunchUriAsync(new Uri(GitHubUrl));
    }

    public async Task OpenDeveloperGitHub()
    {
        await Launcher.LaunchUriAsync(new Uri(DeveloperGitHub));
    }

    public async Task OpenRatingWindow()
    {
        _ = await Launcher.LaunchUriAsync(new Uri("ms-windows-store://review/?ProductId=9NXT4TGJVHVV"));
    }

    public async Task ContactDeveloper()
    {
        string subject = Uri.EscapeDataString($"Traydio Feedback - {Version}");
        string mailToUri = $"mailto:joe@joefinapps.com?subject={subject}";
        await Launcher.LaunchUriAsync(new Uri(mailToUri));
    }

    public async Task OpenRadioBrowser()
    {
        await Launcher.LaunchUriAsync(new Uri(RadioBrowserUrl));
    }

    public async Task OpenWinUIEx()
    {
        await Launcher.LaunchUriAsync(new Uri(WinUIExUrl));
    }

    public async Task OpenRoomRelay()
    {
        await Launcher.LaunchUriAsync(new Uri(RoomRelayUrl));
    }

    public async Task OpenCommunityToolkit()
    {
        await Launcher.LaunchUriAsync(new Uri(CommunityToolkitUrl));
    }

    public async Task OpenBuyMeACoffee()
    {
        await Launcher.LaunchUriAsync(new Uri(BuyMeACoffeeUrl));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
