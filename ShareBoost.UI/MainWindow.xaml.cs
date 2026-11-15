using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using ShareBoost.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Storage;
using WindowsWifiLibrary.Models;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ShareBoost.UI;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        var Width = 495;
        var Height = 595;
        appWindow.Resize(new(Width, Height));
        /*if(appWindow.Presenter is OverlappedPresenter p )
        {
            p.IsResizable = false;
        }*/
        ViewModel = new MainViewModel(hwnd);
        LayoutRoot.DataContext = ViewModel;
        ViewModel.InvitationRequested += OnInvitationRequested;
        Closed += (_, _) => ViewModel.Dispose();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            ViewModel.IsFileOverDropZone = true;
        }
}

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsFileOverDropZone = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        ViewModel.IsFileOverDropZone = false;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        ViewModel.AddFiles(items
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => item.Path));
    }

    private async void OnInvitationRequested(object? sender, WifiDirectInvitationEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = LayoutRoot.XamlRoot,
            Title = "Appairage entrant",
            Content = $"{e.Request.DeviceInformation.Name} souhaite s'appairer en Wi-Fi Direct. Autoriser ?",
            PrimaryButtonText = "Autoriser",
            CloseButtonText = "Refuser",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        await ViewModel.RespondToInvitationAsync(e, result == ContentDialogResult.Primary);
    }
}
