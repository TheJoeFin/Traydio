using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Trdo.Models;
using Trdo.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Trdo.ViewModels;

/// <summary>
/// Backs the page that creates or edits a local music "station" - one that plays a folder of
/// local audio files instead of connecting to a stream. See <see cref="RadioStation.SourceKind"/>.
/// </summary>
public sealed partial class AddLocalMusicViewModel : INotifyPropertyChanged
{
    private string _stationName = string.Empty;
    private string? _folderPath;
    private double _volumePercent = 100;
    private string _pageTitle = LocalizationService.GetString("AddLocalMusic_AddPageTitle", "Add Local Music");
    private PlayerViewModel? _playerViewModel;
    private RadioStation? _editingStation;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetPlayerViewModel(PlayerViewModel playerViewModel) => _playerViewModel = playerViewModel;

    public void LoadStationForEdit(RadioStation station)
    {
        _editingStation = station;
        StationName = station.Name;
        FolderPath = station.LocalFolderPath;
        VolumePercent = station.Volume * 100;
        PageTitle = LocalizationService.GetString("AddLocalMusic_EditPageTitle", "Edit Local Music");
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set
        {
            if (value == _pageTitle) return;
            _pageTitle = value;
            OnPropertyChanged();
        }
    }

    public string StationName
    {
        get => _stationName;
        set
        {
            if (value == _stationName) return;
            _stationName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public string? FolderPath
    {
        get => _folderPath;
        private set
        {
            if (value == _folderPath) return;
            _folderPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FolderPathDisplay));
            OnPropertyChanged(nameof(TrackCountDescription));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>The folder path, or a placeholder prompt when none has been picked yet.</summary>
    public string FolderPathDisplay => string.IsNullOrWhiteSpace(_folderPath)
        ? LocalizationService.GetString("AddLocalMusic_NoFolderSelected", "No folder selected")
        : _folderPath;

    /// <summary>
    /// A one-line summary of what was found in the picked folder: tracks directly inside it,
    /// or - when it has none of its own - subfolders (e.g. albums) that do, in which case
    /// saving creates a folder of one station per subfolder rather than a single station. See
    /// <see cref="Save"/>.
    /// </summary>
    public string TrackCountDescription
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_folderPath))
                return string.Empty;

            int trackCount = LocalMusicFolderScanner.ScanTracks(_folderPath).Count;
            if (trackCount > 0)
            {
                return trackCount switch
                {
                    1 => LocalizationService.GetString("AddLocalMusic_OneTrackFound", "1 track found."),
                    _ => string.Format(
                        LocalizationService.GetString("AddLocalMusic_TracksFound", "{0} tracks found."), trackCount),
                };
            }

            int albumCount = LocalMusicFolderScanner.GetImmediateSubfoldersWithTracks(_folderPath).Count;
            return albumCount switch
            {
                0 => LocalizationService.GetString("AddLocalMusic_NoTracksFound", "No audio files found in this folder."),
                1 => LocalizationService.GetString(
                    "AddLocalMusic_OneAlbumFound", "1 subfolder with music found. It will become its own station inside a new folder."),
                _ => string.Format(
                    LocalizationService.GetString(
                        "AddLocalMusic_AlbumsFound", "{0} subfolders with music found. Each will become its own station inside a new folder."),
                    albumCount),
            };
        }
    }

    /// <summary>Playback volume as a percentage, matching the range of the player's own volume control.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            value = Math.Clamp(value, 0, 200);
            if (value == _volumePercent) return;
            _volumePercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeDescription));
        }
    }

    public string VolumeDescription => $"{_volumePercent:0}%";

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(StationName) &&
        !string.IsNullOrWhiteSpace(FolderPath) &&
        Directory.Exists(FolderPath);

    /// <summary>
    /// Opens a folder picker and, on success, sets <see cref="FolderPath"/> and - if the
    /// station name hasn't been touched yet - defaults <see cref="StationName"/> to the
    /// folder's leaf name.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> PickFolderAsync(nint windowHandle)
    {
        FolderPicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.FileTypeFilter.Add("*");

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return false;

        FolderPath = folder.Path;
        if (string.IsNullOrWhiteSpace(StationName))
            StationName = folder.Name;

        return true;
    }

    public bool Save()
    {
        if (!CanSave)
            return false;

        if (_editingStation != null)
        {
            _editingStation.Name = StationName.Trim();
            _editingStation.LocalFolderPath = FolderPath;
            _editingStation.Volume = VolumePercent / 100;
            _editingStation.FaviconUrl = ResolveCoverUrl(FolderPath);

            _playerViewModel?.SaveStations();
            return true;
        }

        // A folder with no tracks of its own but subfolders that do (e.g. an artist folder of
        // album subfolders) becomes a new folder of one station per subfolder, rather than a
        // single unplayable station - see TrackCountDescription, which tells the user this is
        // about to happen before they save.
        bool hasOwnTracks = LocalMusicFolderScanner.ScanTracks(FolderPath).Count > 0;
        IReadOnlyList<string> albumFolders = hasOwnTracks
            ? []
            : LocalMusicFolderScanner.GetImmediateSubfoldersWithTracks(FolderPath);

        if (albumFolders.Count > 0)
        {
            List<RadioStation> albumStations = new(albumFolders.Count);
            foreach (string albumFolder in albumFolders)
            {
                albumStations.Add(new RadioStation
                {
                    Name = Path.GetFileName(albumFolder),
                    StreamUrl = RadioStation.LocalMusicStreamUrl,
                    SourceKind = AudioSourceKind.Files,
                    LocalFolderPath = albumFolder,
                    Volume = VolumePercent / 100,
                    FaviconUrl = ResolveCoverUrl(albumFolder),
                });
            }

            _playerViewModel?.AddStationsToNewFolder(StationName.Trim(), albumStations);
        }
        else
        {
            RadioStation newStation = new()
            {
                Name = StationName.Trim(),
                StreamUrl = RadioStation.LocalMusicStreamUrl,
                SourceKind = AudioSourceKind.Files,
                LocalFolderPath = FolderPath,
                Volume = VolumePercent / 100,
                FaviconUrl = ResolveCoverUrl(FolderPath),
            };

            _playerViewModel?.AddStation(newStation);
        }

        return true;
    }

    /// <summary>A cover-art file directly in the folder, as an absolute URI the station row's image binding can load, or null if none is found.</summary>
    private static string? ResolveCoverUrl(string? folderPath) =>
        LocalMusicFolderScanner.FindCoverImage(folderPath) is { } path ? new Uri(path).AbsoluteUri : null;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
