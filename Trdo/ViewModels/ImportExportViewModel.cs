using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Trdo.ViewModels;

/// <summary>One row in the import/export preview: a station and whether it will be included.</summary>
public sealed class ImportExportItem(RadioStation station, string source, bool isDuplicate, bool isSelected)
    : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after <see cref="IsSelected"/> changes so the owner can refresh its totals.</summary>
    public event EventHandler? SelectionChanged;

    public RadioStation Station { get; } = station;

    /// <summary>The file this row came from, or empty when exporting.</summary>
    public string Source { get; } = source;

    /// <summary>True when the station is already in the library (import only).</summary>
    public bool IsDuplicate { get; } = isDuplicate;

    public string Name => Station.Name;

    public string StreamUrl => Station.StreamUrl;

    public string Detail
    {
        get
        {
            string status = IsDuplicate
                ? LocalizationService.GetString("ImportExport_AlreadyInLibrary", "Already in library")
                : string.Empty;
            string from = string.IsNullOrEmpty(Source) ? string.Empty : Source;
            return string.Join("  ·  ", new[] { status, from }.Where(s => s.Length > 0));
        }
    }

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Rows already in the library are dimmed so the new ones stand out.</summary>
    public double RowOpacity => IsDuplicate ? 0.6 : 1.0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value == _isSelected) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// State for the import/export window: a previewable, checkable list of stations that is
/// filled from dropped/picked playlist files (import) or from the library (export), and is
/// only committed when the user confirms.
/// </summary>
public sealed class ImportExportViewModel : INotifyPropertyChanged
{
    private bool _isImport = true;
    private bool _exportAsPls;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ImportExportItem> Items { get; } = [];

    public bool IsImport => _isImport;

    public bool IsExport => !_isImport;

    public bool ExportAsPls
    {
        get => _exportAsPls;
        set
        {
            if (value == _exportAsPls) return;
            _exportAsPls = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (value == _statusMessage) return;
            _statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set
        {
            if (value == _statusIsError) return;
            _statusIsError = value;
            OnPropertyChanged();
        }
    }

    public bool HasStatus => StatusMessage.Length > 0;

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public int SelectedCount => Items.Count(i => i.IsSelected);

    public bool CanConfirm => SelectedCount > 0;

    public string ConfirmLabel => _isImport
        ? string.Format(LocalizationService.GetString("ImportExport_ImportCount", "Import {0}"), SelectedCount)
        : string.Format(LocalizationService.GetString("ImportExport_ExportCount", "Export {0}"), SelectedCount);

    public string Summary => string.Format(
        LocalizationService.GetString("ImportExport_Summary", "{0} of {1} selected"),
        SelectedCount,
        Items.Count);

    public string EmptyHint => _isImport
        ? LocalizationService.GetString("ImportExport_DropHint", "Drop .pls, .m3u or .m3u8 files here, or choose files to preview their stations.")
        : LocalizationService.GetString("ImportExport_NoStations", "There are no stations to export.");

    /// <summary>Switches direction and resets the list: import starts empty, export starts with the whole library ticked.</summary>
    public void SetMode(bool import)
    {
        _isImport = import;
        ClearItems();
        StatusMessage = string.Empty;

        if (!import)
        {
            foreach (RadioStation station in PlayerViewModel.Shared.Stations)
            {
                AddItem(new ImportExportItem(station, string.Empty, isDuplicate: false, isSelected: true));
            }
        }

        OnPropertyChanged(nameof(IsImport));
        OnPropertyChanged(nameof(IsExport));
        OnPropertyChanged(nameof(EmptyHint));
        RaiseTotalsChanged();
    }

    public void SelectAll(bool selected)
    {
        foreach (ImportExportItem item in Items)
        {
            item.IsSelected = selected;
        }
    }

    /// <summary>Opens the system picker for playlist files and previews whatever was chosen.</summary>
    public async Task PickFilesAsync(nint windowHandle)
    {
        FileOpenPicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        foreach (string extension in PlaylistImportExportService.SupportedExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            await AddFilesAsync(files);
        }
    }

    /// <summary>
    /// Parses each playlist file and appends its stations to the preview. Stations already in
    /// the library, or already previewed from another file, are listed but start unticked.
    /// </summary>
    public async Task AddFilesAsync(IEnumerable<StorageFile> files)
    {
        if (!_isImport)
        {
            SetMode(import: true);
        }

        int skippedFiles = 0;
        int failedFiles = 0;
        int added = 0;

        foreach (StorageFile file in files)
        {
            if (!PlaylistImportExportService.IsSupportedFile(file.Name))
            {
                skippedFiles++;
                continue;
            }

            try
            {
                string content = await FileIO.ReadTextAsync(file);
                foreach (RadioStation station in PlaylistImportExportService.ImportFromFile(file.Name, content))
                {
                    bool duplicate =
                        PlaylistImportExportService.IsDuplicate(PlayerViewModel.Shared.Stations, station) ||
                        PlaylistImportExportService.IsDuplicate(Items.Select(i => i.Station), station);

                    AddItem(new ImportExportItem(station, file.Name, duplicate, isSelected: !duplicate));
                    added++;
                }
            }
            catch (Exception ex)
            {
                failedFiles++;
                LogService.Warn("ImportExport", $"Could not read {file.Name}: {ex.Message}");
            }
        }

        RaiseTotalsChanged();

        if (failedFiles > 0 || skippedFiles > 0 || added == 0)
        {
            StatusIsError = true;
            StatusMessage = failedFiles == 0 && skippedFiles == 0
                ? LocalizationService.GetString("ImportExport_NoStationsFound", "No stations were found in that file.")
                : DescribeSkippedFiles(skippedFiles, failedFiles);
        }
        else
        {
            StatusIsError = false;
            StatusMessage = string.Empty;
        }
    }

    /// <summary>Adds the ticked stations to the library and returns how many were added.</summary>
    public int CommitImport()
    {
        PlayerViewModel player = PlayerViewModel.Shared;
        int added = 0;

        foreach (ImportExportItem item in Items.Where(i => i.IsSelected))
        {
            RadioStation station = item.Station;

            // This adds to the collection directly rather than going through
            // PlayerViewModel.AddStation, so stamp what that path would have stamped.
            // Imported stations carry no directory details, which makes them exactly the
            // candidates the "refresh station info" command exists for.
            station.Id = StationIdentityPolicy.NewId();
            station.DateAdded ??= DateTimeOffset.UtcNow;

            player.Stations.Add(station);
            added++;
        }

        if (added > 0)
        {
            RadioStationService.Instance.SaveStations(player.Stations);
        }

        return added;
    }

    /// <summary>Asks where to save, then writes the ticked stations. Returns the count written, or 0 if cancelled.</summary>
    public async Task<int> CommitExportAsync(nint windowHandle)
    {
        List<RadioStation> selected = Items.Where(i => i.IsSelected).Select(i => i.Station).ToList();
        if (selected.Count == 0)
        {
            return 0;
        }

        FileSavePicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.SuggestedFileName = "Traydio Stations";
        if (_exportAsPls)
        {
            picker.FileTypeChoices.Add("PLS Playlist", [".pls"]);
            picker.FileTypeChoices.Add("M3U Playlist", [".m3u"]);
        }
        else
        {
            picker.FileTypeChoices.Add("M3U Playlist", [".m3u"]);
            picker.FileTypeChoices.Add("PLS Playlist", [".pls"]);
        }

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return 0;
        }

        // The extension the user ended up with wins over the toggle, so the content matches the file.
        bool asPls = string.Equals(Path.GetExtension(file.Name), ".pls", StringComparison.OrdinalIgnoreCase);
        await FileIO.WriteTextAsync(file, PlaylistImportExportService.Export(selected, asPls));
        return selected.Count;
    }

    public void ReportError(string message)
    {
        StatusIsError = true;
        StatusMessage = message;
    }

    /// <summary>One sentence for each kind of file that was passed over, omitting a kind with none.</summary>
    private static string DescribeSkippedFiles(int unsupported, int unreadable)
    {
        List<string> sentences = [];
        if (unsupported > 0)
        {
            sentences.Add(string.Format(
                LocalizationService.GetString("ImportExport_FilesUnsupported", "{0} file(s) were unsupported."),
                unsupported));
        }

        if (unreadable > 0)
        {
            sentences.Add(string.Format(
                LocalizationService.GetString("ImportExport_FilesUnreadable", "{0} file(s) could not be read."),
                unreadable));
        }

        return string.Join(Environment.NewLine, sentences);
    }

    private void AddItem(ImportExportItem item)
    {
        item.SelectionChanged += OnItemSelectionChanged;
        Items.Add(item);
    }

    private void ClearItems()
    {
        foreach (ImportExportItem item in Items)
        {
            item.SelectionChanged -= OnItemSelectionChanged;
        }

        Items.Clear();
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e) => RaiseTotalsChanged();

    private void RaiseTotalsChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
