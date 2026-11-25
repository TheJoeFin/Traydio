using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services;
using Windows.Storage;

namespace Trdo.Helpers;

/// <summary>
/// The app's <see cref="CustomStationArtStore"/>, in its local data folder, plus the parts of
/// importing and discarding an image that need WinRT.
/// </summary>
internal static class CustomStationArt
{
    /// <summary>
    /// Longest edge of the stored copy: ample for anywhere a station logo is shown, without
    /// keeping a camera-sized original.
    /// </summary>
    private const int StoredEdge = 512;

    /// <summary>The extensions offered by the file picker.</summary>
    public static readonly string[] PickerExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    private static readonly Lazy<CustomStationArtStore> Store = new(() =>
        new CustomStationArtStore(Path.Combine(ApplicationData.Current.LocalFolder.Path, "station-art")));

    /// <summary>
    /// Copies <paramref name="file"/> into the app's data folder and returns the copy's URL.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is too large or is not an image; the message
    /// is ready to show to the user.</exception>
    public static async Task<string> ImportAsync(StorageFile file)
    {
        byte[] bytes;
        using (Stream source = await file.OpenStreamForReadAsync())
        {
            if (source.Length > CustomStationArtStore.MaxSourceBytes)
            {
                throw new InvalidDataException(string.Format(
                    LocalizationService.GetString(
                        "ManualStationWindow_ImageTooLarge", "That image is too large. Choose one under {0} MB."),
                    CustomStationArtStore.MaxSourceBytes / (1024 * 1024)));
            }

            using MemoryStream buffer = new();
            await source.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        if (CustomStationArtStore.ExtensionFor(bytes) is null)
        {
            throw new InvalidDataException(LocalizationService.GetString(
                "ManualStationWindow_ImageNotSupported", "That file isn't a supported image."));
        }

        return await Store.Value.SaveAsync(await ArtworkResizer.DownscaleAsync(bytes, StoredEdge));
    }

    /// <summary>
    /// Deletes the app's copy behind <paramref name="url"/>, and its cached art, once none of
    /// <paramref name="stations"/> uses it. Does nothing for any other kind of URL.
    /// </summary>
    public static void Release(string? url, IEnumerable<RadioStation> stations) =>
        Release(url, stations.Select(s => s.FaviconUrl));

    /// <inheritdoc cref="Release(string, IEnumerable{RadioStation})"/>
    public static void Release(string? url, IEnumerable<string?> urlsInUse)
    {
        if (Store.Value.DeleteIfUnused(url, urlsInUse))
        {
            ImageSourceFactory.Forget(url);
        }
    }
}
