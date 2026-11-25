using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using Trdo.Helpers;
using Trdo.Models;
using Trdo.Services;
using Trdo.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUIEx;

namespace Trdo.Controls;

/// <summary>
/// A small standalone window for manually adding or editing a radio station.
/// Opens as a pop-out window so that closing the tray flyout does not clear the form fields.
/// </summary>
public sealed partial class ManualStationWindow : WindowEx
{
    public AddStationViewModel ViewModel { get; }

    public ManualStationWindow()
    {
        InitializeComponent();

        ViewModel = new AddStationViewModel();
        ViewModel.SetPlayerViewModel(PlayerViewModel.Shared);

        Title = LocalizationService.GetString("ManualStationWindow_Title", "Traydio");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ModernTitlebar);

        // Registered here to see handled events too: a TextBox claims drags over itself for
        // text, which would otherwise refuse an image dropped on the logo URL box.
        RootGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(Window_DragOver), handledEventsToo: true);
        RootGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(Window_Drop), handledEventsToo: true);

        Activated += ManualStationWindow_Activated;
    }

    /// <summary>
    /// Opens the window pre-filled with the given station's data for editing.
    /// </summary>
    public void LoadStationForEdit(RadioStation station)
    {
        ViewModel.LoadStationForEdit(station);
    }

    private void ManualStationWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        // Focus the station name field once the window is ready
        if (args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
        {
            WindowPlacementService.PositionWindowNearAnchor(this, 400, 620);
            StationNameTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            Activated -= ManualStationWindow_Activated;
        }
    }

    private void SaveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.Save())
        {
            Close();
        }
    }

    private void CancelButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Close();
    }

    private async void ChooseImageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileOpenPicker picker = new();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (string extension in CustomStationArt.PickerExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                await ViewModel.SetImageFromFileAsync(file);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportImageFailure(ex);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.CanImportImage && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.GetString("ManualStationWindow_DropImageCaption", "Use as station logo");
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
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
            StorageFile? file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is null)
            {
                ViewModel.ReportImageError(LocalizationService.GetString(
                    "ManualStationWindow_ImageNotSupported", "That file isn't a supported image."));
                return;
            }

            await ViewModel.SetImageFromFileAsync(file);
        }
        catch (Exception ex)
        {
            ViewModel.ReportImageFailure(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Collapses the hero preview when the logo URL points at something that is
    /// not a loadable image. Only the view can detect this — the URL is
    /// well-formed, so the view model has no way to know until the download or
    /// decode fails.
    /// </summary>
    private void HeroImage_ImageFailed(object sender, Microsoft.UI.Xaml.ExceptionRoutedEventArgs e)
    {
        ViewModel.NotifyHeroImageFailed();
    }

    private void WindowEx_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        ViewModel.DiscardUnsavedImages();

        if (App.Current is App currentApp)
            currentApp.ShowFlyout();
    }
}
