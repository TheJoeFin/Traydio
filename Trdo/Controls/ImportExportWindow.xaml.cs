using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Trdo.Services;
using Trdo.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
using WinUIEx;

namespace Trdo.Controls;

/// <summary>
/// A full-size window for importing and exporting station lists. Unlike the tray flyout it
/// does not dismiss when it loses focus, accepts playlist files dropped onto it, and previews
/// every station with a checkbox so the user decides what is imported or exported. When it
/// closes - on completion or cancel - the tray flyout is shown again, like the other pop-out
/// windows.
/// </summary>
public sealed partial class ImportExportWindow : WindowEx
{
    public ImportExportViewModel ViewModel { get; } = new();

    public ImportExportWindow(bool startAsImport = true)
    {
        InitializeComponent();

        Title = LocalizationService.GetString("ImportExport_WindowTitle", "Import and export stations");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ModernTitlebar);

        ModeButtons.SelectedIndex = startAsImport ? 0 : 1;
        ViewModel.SetMode(startAsImport);

        this.CenterOnScreen();
    }

    private void ModeButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool import = ModeButtons.SelectedIndex == 0;
        if (import != ViewModel.IsImport)
        {
            ViewModel.SetMode(import);
        }
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.ExportAsPls = FormatComboBox.SelectedIndex == 1;
    }

    private async void ChooseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.PickFilesAsync(WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll(true);

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll(false);

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.GetString("ImportExport_DropCaption", "Preview stations");
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            List<StorageFile> files = [];
            foreach (IStorageItem item in items)
            {
                if (item is StorageFile file)
                {
                    files.Add(file);
                }
            }

            await ViewModel.AddFilesAsync(files);

            // A drop while on the export tab switches the view model to import; follow it.
            if (ModeButtons.SelectedIndex != 0)
            {
                ModeButtons.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = ViewModel.IsImport
                ? ViewModel.CommitImport()
                : await ViewModel.CommitExportAsync(WindowNative.GetWindowHandle(this));

            // Export returns 0 when the save dialog is cancelled: stay open so nothing is lost.
            if (count > 0)
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void WindowEx_Closed(object sender, WindowEventArgs args)
    {
        if (App.Current is App currentApp)
            currentApp.ShowFlyout();
    }
}
