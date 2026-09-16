using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
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
    public string AppDescription => "A simple, elegant internet radio player for Windows";
    public string Version
    {
        get
        {
            PackageVersion version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string GitHubUrl => "https://github.com/TheJoeFin/Trdo";
    public string GitHubDisplayText => "github.com/TheJoeFin/Trdo";

    public string DeveloperName => "Joe Finney (TheJoeFin)";
    public string DeveloperGitHub => "https://github.com/TheJoeFin";

    public string RadioBrowserName => "Radio Browser";
    public string RadioBrowserUrl => "https://www.radio-browser.info";
    public string RadioBrowserDisplayText => "radio-browser.info";
    public string RadioBrowserDescription => "Community-driven internet radio database with thousands of stations worldwide";

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
