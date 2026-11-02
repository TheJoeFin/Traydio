using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Windows.Storage;

namespace Trdo.Services.Playback;

/// <summary>
/// Backs <see cref="EngineHealthStore"/> with WinRT local settings. Kept separate from the
/// store itself so the selection rules stay free of WinRT and can be unit tested.
/// </summary>
public sealed class LocalSettingsEngineHealthStorage : IEngineHealthStorage
{
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            try
            {
                // Copy: the caller removes entries while enumerating.
                return (List<string>)[.. ApplicationData.Current.LocalSettings.Values.Keys];
            }
            catch
            {
                return [];
            }
        }
    }

    public bool TryRead(string key, [NotNullWhen(true)] out string? value)
    {
        value = null;

        try
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? stored) &&
                stored is string text)
            {
                value = text;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public void Write(string key, string value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            // ignore
        }
    }

    public void Remove(string key)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values.Remove(key);
        }
        catch
        {
            // ignore
        }
    }
}
