using System;
using System.Security.Cryptography;
using System.Text;

namespace Trdo.Services;

/// <summary>
/// Chooses the GUID the notification-area icon is registered under (<c>NIF_GUID</c>).
/// </summary>
/// <remarks>
/// <para>
/// Windows 11 remembers whether an icon is promoted to the taskbar, per icon, in
/// <c>HKCU\Control Panel\NotifyIconSettings</c>. An icon registered by <c>uID</c> alone is keyed on
/// the executable's path, and an MSIX update installs to a new <c>WindowsApps\…_&lt;version&gt;_…</c>
/// folder, so every update looked like a brand new icon and lost its promoted state. An icon
/// registered with a GUID keeps its entry across a path change.
/// </para>
/// <para>
/// A GUID can only be held by one process at a time and must never change once shipped
/// (changing it orphans the stored placement), so the packaged app uses one constant. Unpackaged
/// builds (a dev F5 run beside an installed copy) would collide with it, so they get a GUID
/// derived from their own executable path instead.
/// </para>
/// </remarks>
internal static class TrayIconIdentityPolicy
{
    /// <summary>
    /// The identity of the packaged (Store / appinstaller) app. Never change this value.
    /// </summary>
    public static readonly Guid PackagedIconGuid = new("f290d6ce-783f-41ac-8b14-b1edfb468c2d");

    public static Guid Choose(bool isPackaged, string? executablePath)
    {
        if (isPackaged || string.IsNullOrWhiteSpace(executablePath))
            return PackagedIconGuid;

        return ForExecutable(executablePath);
    }

    /// <summary>
    /// Derives a stable GUID from an executable path, case-insensitively, so each unpackaged copy
    /// has its own identity but the same copy always gets the same one.
    /// </summary>
    public static Guid ForExecutable(string executablePath)
    {
        string normalized = executablePath.Trim().ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return new Guid(hash.AsSpan(0, 16));
    }
}
