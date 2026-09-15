using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Trdo.Services;
using Trdo.Services.Audio;
using Trdo.Services.Playback;
using Trdo.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Win32;


namespace Trdo.Pages;

public sealed partial class SettingsPage : Page
{
    private float _displayLevel;
    private bool _isUpdatingAutoPlayToggle;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RadioPlayerService.Instance.Watchdog.AudioLevelUpdated += OnAudioLevelUpdated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RadioPlayerService.Instance.Watchdog.AudioLevelUpdated -= OnAudioLevelUpdated;
    }

    /// <summary>
    /// Pops the song change popup up on demand. The popup never takes
    /// activation (WS_EX_NOACTIVATE), so this does not light-dismiss the
    /// Traydio window the Settings page is hosted in.
    /// </summary>
    private void SongChangePopupDemoButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.ShowSongChangePopupDemo();
    }

    /// <summary>
    /// Previews the buffering static so the user can hear what the toggle does without waiting
    /// for a slow stream. Deliberately independent of the toggle - this is what it sounds like,
    /// whether or not it is switched on.
    /// </summary>
    private async void RadioStaticTestButton_Click(object sender, RoutedEventArgs e)
    {
        RadioStaticTestButton.IsEnabled = false;

        try
        {
            await RadioStaticService.Instance.PlayTestBurstAsync();
        }
        finally
        {
            RadioStaticTestButton.IsEnabled = true;
        }
    }

    private void OnAudioLevelUpdated(float rms)
    {
        // Marshal to UI thread — the event fires on the NAudio capture thread
        DispatcherQueue.TryEnqueue(() => UpdateLevelBars(rms));
    }

    private void UpdateLevelBars(float rms)
    {
        // Logarithmic (dB) normalisation  –  maps silence..full-scale to 0..1
        // Using a 50 dB range gives more visible movement in the mid-levels
        float db = rms > 0 ? 20f * MathF.Log10(rms) : -50f;
        float normalized = Math.Clamp((db + 50f) / 50f, 0f, 1f);

        // Fast attack, quick decay so bars visibly bounce with every beat
        const float decayFactor = 0.55f;
        _displayLevel = normalized > _displayLevel
            ? normalized
            : _displayLevel * (1f - decayFactor) + normalized * decayFactor;

        // Each bar has a threshold; opacity ramps proportionally above it
        // so even small fluctuations produce visible movement.
        const float dimOpacity = 0.12f;
        Bar1.Opacity = BarOpacity(_displayLevel, 0.05f, dimOpacity);
        Bar2.Opacity = BarOpacity(_displayLevel, 0.22f, dimOpacity);
        Bar3.Opacity = BarOpacity(_displayLevel, 0.42f, dimOpacity);
        Bar4.Opacity = BarOpacity(_displayLevel, 0.62f, dimOpacity);
        Bar5.Opacity = BarOpacity(_displayLevel, 0.80f, dimOpacity);
    }

    /// <summary>
    /// Returns a proportional opacity for a single bar.
    /// Below <paramref name="threshold"/> the bar is dim; above it the bar ramps
    /// smoothly from dim → fully lit over a short range so small level changes
    /// are clearly visible.
    /// </summary>
    private static double BarOpacity(float level, float threshold, float dim)
    {
        if (level <= threshold) return dim;
        // Ramp from dim → 1.0 over 0.15 of level range above the threshold
        float t = Math.Min((level - threshold) / 0.15f, 1f);
        return dim + t * (1.0 - dim);
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            nint hwnd = PInvoke.GetActiveWindow();
            int count = await ViewModel.ImportStationsAsync(hwnd);

            if (count > 0)
            {
                ImportExportInfoBar.Severity = InfoBarSeverity.Success;
                ImportExportInfoBar.Message = $"Imported {count} station{(count == 1 ? "" : "s")}.";
            }
            else
            {
                ImportExportInfoBar.Severity = InfoBarSeverity.Informational;
                ImportExportInfoBar.Message = "No stations were imported.";
            }

            ImportExportInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ImportExportInfoBar.Severity = InfoBarSeverity.Error;
            ImportExportInfoBar.Message = string.Format(
                LocalizationService.GetString("SettingsPage_ImportFailed", "Import failed: {0}"), ex.Message);
            ImportExportInfoBar.IsOpen = true;
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            nint hwnd = PInvoke.GetActiveWindow();
            bool exported = await ViewModel.ExportStationsAsync(hwnd);

            if (exported)
            {
                ImportExportInfoBar.Severity = InfoBarSeverity.Success;
                ImportExportInfoBar.Message = "Stations exported successfully.";
            }
            else
            {
                ImportExportInfoBar.Severity = InfoBarSeverity.Informational;
                ImportExportInfoBar.Message = "Export cancelled or no stations to export.";
            }

            ImportExportInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ImportExportInfoBar.Severity = InfoBarSeverity.Error;
            ImportExportInfoBar.Message = string.Format(
                LocalizationService.GetString("SettingsPage_ExportFailed", "Export failed: {0}"), ex.Message);
            ImportExportInfoBar.IsOpen = true;
        }
    }

    private async void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = LogService.LogFolderPath;
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            {
                DiagnosticsInfoBar.Severity = InfoBarSeverity.Informational;
                DiagnosticsInfoBar.Message = "No logs have been written yet.";
                DiagnosticsInfoBar.IsOpen = true;
                return;
            }

            bool launched = await Windows.System.Launcher.LaunchFolderPathAsync(folder);
            if (launched)
            {
                DiagnosticsInfoBar.Severity = InfoBarSeverity.Success;
                DiagnosticsInfoBar.Message = "Opened the logs folder.";
            }
            else
            {
                DiagnosticsInfoBar.Severity = InfoBarSeverity.Error;
                DiagnosticsInfoBar.Message = string.Format(
                    LocalizationService.GetString(
                        "SettingsPage_OpenLogsFolderFailedLocation",
                        "Couldn't open the logs folder. It's located at: {0}"),
                    folder);
            }

            DiagnosticsInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            DiagnosticsInfoBar.Severity = InfoBarSeverity.Error;
            DiagnosticsInfoBar.Message = string.Format(
                LocalizationService.GetString("SettingsPage_OpenLogsFolderFailed", "Couldn't open the logs folder: {0}"),
                ex.Message);
            DiagnosticsInfoBar.IsOpen = true;
        }
    }

    /// <summary>
    /// Forgets which engine each station was proven to play on. The records live in local
    /// settings rather than on the player, so this needs no reference to the running service.
    /// </summary>
    private void ResetEngineMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EngineHealthStore store = new(new LocalSettingsEngineHealthStorage());
            int removed = store.Clear();

            LogService.Info("SettingsPage", $"User reset engine memory ({removed} record(s) removed)");

            EngineMemoryInfoBar.Severity = InfoBarSeverity.Success;
            EngineMemoryInfoBar.Message = removed == 0
                ? "There were no remembered engines to reset."
                : $"Forgot the remembered engine for {removed} station{(removed == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            EngineMemoryInfoBar.Severity = InfoBarSeverity.Error;
            EngineMemoryInfoBar.Message = string.Format(
                LocalizationService.GetString("SettingsPage_ResetEngineMemoryFailed", "Couldn't reset remembered engines: {0}"),
                ex.Message);
        }

        EngineMemoryInfoBar.IsOpen = true;
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string diagnostics = LogService.ReadRecentText();

            DataPackage package = new();
            package.SetText(diagnostics);
            Clipboard.SetContent(package);

            DiagnosticsInfoBar.Severity = InfoBarSeverity.Success;
            DiagnosticsInfoBar.Message = "Copied recent diagnostics to the clipboard.";
            DiagnosticsInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            DiagnosticsInfoBar.Severity = InfoBarSeverity.Error;
            DiagnosticsInfoBar.Message = string.Format(
                LocalizationService.GetString("SettingsPage_CopyDiagnosticsFailed", "Couldn't copy diagnostics: {0}"),
                ex.Message);
            DiagnosticsInfoBar.IsOpen = true;
        }
    }

    private void ResetLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LogService.ClearLogs();
            LogService.Info("SettingsPage", "User reset the diagnostic logs");

            DiagnosticsInfoBar.Severity = InfoBarSeverity.Success;
            DiagnosticsInfoBar.Message = LocalizationService.GetString(
                "SettingsPage_ResetLogsSuccess", "Cleared the log files.");
        }
        catch (Exception ex)
        {
            DiagnosticsInfoBar.Severity = InfoBarSeverity.Error;
            DiagnosticsInfoBar.Message = string.Format(
                LocalizationService.GetString("SettingsPage_ResetLogsFailed", "Couldn't reset the logs: {0}"),
                ex.Message);
        }

        DiagnosticsInfoBar.IsOpen = true;
    }

    private void AutoPlayOnStartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingAutoPlayToggle)
        {
            return;
        }

        bool isEnabled = ViewModel.IsAutoPlayOnStartupEnabled;
        bool requestedState = AutoPlayOnStartupToggle.IsOn;

        if (requestedState == isEnabled)
        {
            if (!requestedState)
            {
                AutoPlayWarningInfoBar.IsOpen = false;
            }

            return;
        }

        if (!requestedState)
        {
            ViewModel.IsAutoPlayOnStartupEnabled = false;
            AutoPlayWarningInfoBar.IsOpen = false;
            return;
        }

        SetAutoPlayToggle(false);
        AutoPlayWarningInfoBar.IsOpen = true;
    }

    private void ConfirmAutoPlayButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsAutoPlayOnStartupEnabled = true;
        SetAutoPlayToggle(true);
        AutoPlayWarningInfoBar.IsOpen = false;
    }

    private void SetAutoPlayToggle(bool value)
    {
        _isUpdatingAutoPlayToggle = true;
        AutoPlayOnStartupToggle.IsOn = value;
        _isUpdatingAutoPlayToggle = false;
    }
}
