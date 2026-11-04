using System;
using System.Linq;
using Windows.Security.Credentials;

namespace Trdo.Services.Lastfm;

/// <summary>
/// Stores the Last.fm session key in <see cref="PasswordVault"/> rather than the
/// <c>LocalSettings</c> bag every other setting in this app uses. Unlike a UI preference, the
/// session key is a durable, infinite-lifetime account credential - closer to a password than a
/// toggle - so it gets proper encrypted-at-rest storage even though that is a new pattern here.
/// The connected username is not secret and lives in <see cref="SettingsService.LastfmUsername"/>
/// instead, so Settings can show "Connected as X" without a vault read on every bind.
/// </summary>
internal static class LastfmAccountStore
{
    private const string VaultResource = "Traydio.Lastfm";

    /// <summary>Reads the stored session, if any. False if the user has never connected or has disconnected.</summary>
    public static bool TryGetSession(out string? username, out string? sessionKey)
    {
        try
        {
            PasswordVault vault = new();
            PasswordCredential? credential = vault.FindAllByResource(VaultResource).FirstOrDefault();
            if (credential is null)
            {
                username = null;
                sessionKey = null;
                return false;
            }

            // FindAllByResource does not populate the password for security reasons.
            credential.RetrievePassword();
            username = credential.UserName;
            sessionKey = credential.Password;
            return true;
        }
        catch
        {
            // Thrown when nothing is stored for this resource, or the vault is unavailable -
            // either way there is no session to report.
            username = null;
            sessionKey = null;
            return false;
        }
    }

    /// <summary>Stores a freshly obtained session, replacing any previous one.</summary>
    public static void SaveSession(string username, string sessionKey)
    {
        try
        {
            RemoveStoredCredential();

            PasswordVault vault = new();
            vault.Add(new PasswordCredential(VaultResource, username, sessionKey));

            LogService.Info("Lastfm", $"Saved session for '{username}'");
        }
        catch (Exception ex)
        {
            LogService.Warn("Lastfm", $"Failed to save session: {ex.Message}");
            return;
        }

        // Connecting - whether for the first time or after a disconnect - implies the user
        // wants scrobbling on, so the toggle does not silently stay off after they just
        // finished the connect flow. Set before LastfmUsername below, since that setter is what
        // raises LastfmConnectionChanged - listeners need the new value to already be in place.
        SettingsService.IsLastfmScrobblingEnabled = true;

        // Raises SettingsService.LastfmConnectionChanged.
        SettingsService.LastfmUsername = username;
    }

    /// <summary>Forgets the stored session - the user disconnected, or a call reported it as no longer valid.</summary>
    public static void ClearSession()
    {
        RemoveStoredCredential();

        // Scrobbling cannot happen without a session, so keep the toggle in sync rather than
        // leaving it on for an account that is no longer connected.
        SettingsService.IsLastfmScrobblingEnabled = false;

        // Raises SettingsService.LastfmConnectionChanged even if nothing was stored, so a
        // caller that only knows "make sure we are disconnected" does not need to check first.
        SettingsService.LastfmUsername = null;
    }

    private static void RemoveStoredCredential()
    {
        try
        {
            PasswordVault vault = new();
            foreach (PasswordCredential credential in vault.FindAllByResource(VaultResource))
                vault.Remove(credential);
        }
        catch
        {
            // Nothing stored yet, or the vault is unavailable - either way there is nothing to remove.
        }
    }
}
