using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Trdo.Services;

public enum AppUpdateStatus
{
    Unknown,
    UpToDate,
    Available,
    Error,
}

/// <summary>
/// Update support for copies installed from GitHub through the .appinstaller feed. Store copies
/// and unpackaged runs have no App Installer info, so <see cref="IsGitHubInstall"/> is false and
/// the About page hides the update section.
/// </summary>
public static class AppInstallerUpdateService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    private static Uri? GetFeedUri()
    {
        try
        {
            return Package.Current.GetAppInstallerInfo()?.Uri;
        }
        catch (Exception)
        {
            // Unpackaged, or older than Windows 10 2004.
            return null;
        }
    }

    public static bool IsGitHubInstall => GetFeedUri() is not null;

    public static async Task<AppUpdateStatus> CheckAsync()
    {
        try
        {
            PackageUpdateAvailabilityResult result = await Package.Current.CheckUpdateAvailabilityAsync();
            return result.Availability switch
            {
                PackageUpdateAvailability.NoUpdates => AppUpdateStatus.UpToDate,
                PackageUpdateAvailability.Available or PackageUpdateAvailability.Required => AppUpdateStatus.Available,
                _ => AppUpdateStatus.Error,
            };
        }
        catch (Exception ex)
        {
            LogService.Warn("Update", $"Check failed: {ex.Message}");
            return AppUpdateStatus.Error;
        }
    }

    /// <summary>
    /// Installs the update and restarts the app. Windows shuts the app down to replace the package;
    /// RegisterApplicationRestart asks it to relaunch afterwards.
    /// </summary>
    public static async Task<bool> RestartToUpdateAsync()
    {
        Uri? feed = GetFeedUri();
        if (feed is null)
            return false;

        try
        {
            _ = RegisterApplicationRestart(null, 0);
            PackageManager manager = new();
            DeploymentResult result = await manager.RequestAddPackageByAppInstallerFileAsync(
                feed, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, null);
            if (!string.IsNullOrEmpty(result.ErrorText))
            {
                LogService.Warn("Update", $"Install failed: {result.ErrorText}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("Update", $"Install failed: {ex.Message}");
            return false;
        }
    }
}
