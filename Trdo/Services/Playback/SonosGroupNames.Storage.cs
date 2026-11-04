using System;
using System.Collections.Generic;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Trdo.Services.Playback;

public static partial class SonosGroupNames
{
    private const string SettingsKey = "SonosGroupNames";

    /// <summary>The name given to exactly this set of rooms, or null if it has none.</summary>
    public static string? Find(IEnumerable<string> roomUuids)
    {
        try
        {
            string key = KeyFor(roomUuids);
            if (key.Length > 0 &&
                ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingsKey, out object? stored) &&
                stored is ApplicationDataCompositeValue names &&
                names.TryGetValue(key, out object? name) &&
                name is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        catch (Exception)
        {
            // Not running with an application data store (tests, an unpackaged host): no names.
        }

        return null;
    }

    /// <summary>Names a set of rooms, or clears its name when <paramref name="name"/> is blank.</summary>
    public static void Set(IEnumerable<string> roomUuids, string? name)
    {
        string key = KeyFor(roomUuids);
        if (key.Length == 0)
            return;

        IPropertySet values = ApplicationData.Current.LocalSettings.Values;
        ApplicationDataCompositeValue names = new();
        if (values.TryGetValue(SettingsKey, out object? stored) && stored is ApplicationDataCompositeValue existing)
        {
            foreach (KeyValuePair<string, object> pair in existing)
                names[pair.Key] = pair.Value;
        }

        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            names.Remove(key);
        else
            names[key] = trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;

        // The composite has to be assigned back for the change to be saved.
        values[SettingsKey] = names;
    }
}
