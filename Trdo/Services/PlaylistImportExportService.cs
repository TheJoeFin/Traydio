using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Trdo.Models;

namespace Trdo.Services;

public static class PlaylistImportExportService
{
    /// <summary>
    /// Parses radio stations from the contents of a playlist file.
    /// Supports M3U/M3U8 and PLS formats.
    /// </summary>
    public static List<RadioStation> ImportFromFile(string filePath, string content)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".pls" => ParsePls(content),
            _ => ParseM3u(content), // .m3u, .m3u8, or fallback
        };
    }

    /// <summary>File extensions the import window accepts.</summary>
    public static readonly string[] SupportedExtensions = [".m3u", ".m3u8", ".pls"];

    /// <summary>True when <paramref name="path"/> has an extension <see cref="ImportFromFile"/> understands.</summary>
    public static bool IsSupportedFile(string path) =>
        Array.Exists(SupportedExtensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="candidate"/> is already in <paramref name="existing"/>. Matches
    /// on every imported field, so a re-import of an unchanged export adds nothing while a
    /// renamed or re-pointed station still counts as new.
    /// </summary>
    public static bool IsDuplicate(IEnumerable<RadioStation> existing, RadioStation candidate)
    {
        foreach (RadioStation station in existing)
        {
            if (string.Equals(station.Name, candidate.Name, StringComparison.Ordinal) &&
                string.Equals(station.StreamUrl, candidate.StreamUrl, StringComparison.Ordinal) &&
                string.Equals(station.Homepage, candidate.Homepage, StringComparison.Ordinal) &&
                string.Equals(station.FaviconUrl, candidate.FaviconUrl, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Exports to PLS when <paramref name="asPls"/> is set, otherwise M3U.</summary>
    public static string Export(IEnumerable<RadioStation> stations, bool asPls) =>
        asPls ? ExportToPls(stations) : ExportToM3u(stations);

    /// <summary>
    /// Exports radio stations to M3U format.
    /// </summary>
    public static string ExportToM3u(IEnumerable<RadioStation> stations)
    {
        StringBuilder sb = new();
        sb.AppendLine("#EXTM3U");

        foreach (RadioStation station in stations)
        {
            sb.AppendLine($"#EXTINF:-1,{station.Name}");

            if (!string.IsNullOrWhiteSpace(station.Homepage))
                sb.AppendLine($"#EXTVLCOPT:url={station.Homepage}");

            if (!string.IsNullOrWhiteSpace(station.FaviconUrl))
                sb.AppendLine($"#EXTIMG:{station.FaviconUrl}");

            sb.AppendLine(station.StreamUrl);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Exports radio stations to PLS format.
    /// </summary>
    public static string ExportToPls(IEnumerable<RadioStation> stations)
    {
        List<RadioStation> list = [.. stations];
        StringBuilder sb = new();
        sb.AppendLine("[playlist]");
        sb.AppendLine($"NumberOfEntries={list.Count}");

        for (int i = 0; i < list.Count; i++)
        {
            int num = i + 1;
            sb.AppendLine($"File{num}={list[i].StreamUrl}");
            sb.AppendLine($"Title{num}={list[i].Name}");
            sb.AppendLine($"Length{num}=-1");
        }

        sb.AppendLine("Version=2");
        return sb.ToString();
    }

    private static List<RadioStation> ParseM3u(string content)
    {
        List<RadioStation> stations = [];
        string[] lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        string? currentName = null;
        string? currentHomepage = null;
        string? currentFavicon = null;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                // Format: #EXTINF:-1,Station Name
                int commaIndex = line.IndexOf(',');
                if (commaIndex >= 0 && commaIndex < line.Length - 1)
                    currentName = line[(commaIndex + 1)..].Trim();

                continue;
            }

            if (line.StartsWith("#EXTVLCOPT:url=", StringComparison.OrdinalIgnoreCase))
            {
                currentHomepage = line["#EXTVLCOPT:url=".Length..].Trim();
                continue;
            }

            if (line.StartsWith("#EXTIMG:", StringComparison.OrdinalIgnoreCase))
            {
                currentFavicon = line["#EXTIMG:".Length..].Trim();
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            // This should be a URL line
            if (!string.IsNullOrWhiteSpace(line))
            {
                string streamUrl = line;
                string name = currentName ?? GetNameFromUrl(streamUrl);

                stations.Add(new RadioStation
                {
                    Name = name,
                    StreamUrl = streamUrl,
                    Homepage = currentHomepage,
                    FaviconUrl = currentFavicon,
                });

                currentName = null;
                currentHomepage = null;
                currentFavicon = null;
            }
        }

        return stations;
    }

    private static List<RadioStation> ParsePls(string content)
    {
        List<RadioStation> stations = [];
        string[] lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        Dictionary<int, string> files = [];
        Dictionary<int, string> titles = [];

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                (int index, string value) = ParsePlsEntry(line, "File");
                if (index > 0)
                    files[index] = value;
            }
            else if (line.StartsWith("Title", StringComparison.OrdinalIgnoreCase))
            {
                (int index, string value) = ParsePlsEntry(line, "Title");
                if (index > 0)
                    titles[index] = value;
            }
        }

        foreach (KeyValuePair<int, string> kvp in files)
        {
            string streamUrl = kvp.Value;
            string name = titles.TryGetValue(kvp.Key, out string? title) && !string.IsNullOrWhiteSpace(title)
                ? title
                : GetNameFromUrl(streamUrl);

            stations.Add(new RadioStation
            {
                Name = name,
                StreamUrl = streamUrl,
            });
        }

        return stations;
    }

    private static (int Index, string Value) ParsePlsEntry(string line, string prefix)
    {
        // Format: File1=http://... or Title1=Station Name
        int eqIndex = line.IndexOf('=');
        if (eqIndex < 0)
            return (0, string.Empty);

        string key = line[..eqIndex];
        string value = line[(eqIndex + 1)..].Trim();

        if (int.TryParse(key[prefix.Length..], out int index))
            return (index, value);

        return (0, string.Empty);
    }

    private static string GetNameFromUrl(string url)
    {
        try
        {
            Uri uri = new(url);
            return uri.Host;
        }
        catch
        {
            return "Unknown Station";
        }
    }
}
