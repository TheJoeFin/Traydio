using System;
using Windows.System.UserProfile;

namespace Trdo.Services;

/// <summary>
/// Wraps the Windows "Country or region" setting so callers don't reach into WinRT directly.
/// </summary>
public static class UserRegionService
{
    /// <summary>
    /// The 2-letter ISO 3166-1 region code the user configured in Windows Settings, or null if
    /// it could not be determined. Matches the "countrycode" parameter Radio Browser expects.
    /// </summary>
    public static string? CurrentRegionCode
    {
        get
        {
            try
            {
                string region = GlobalizationPreferences.HomeGeographicRegion;
                return string.IsNullOrWhiteSpace(region) ? null : region;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
