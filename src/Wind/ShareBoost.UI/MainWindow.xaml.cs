using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Storage;
using Windows.UI.ViewManagement;
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
    private AppWindow? _appWindow;
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private bool _highContrastChangedSubscribed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        appWindow.Resize(new(980, 820));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 640;
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            SetTitleBar(AppTitleBar);
            appWindow.Changed += (_, _) => UpdateTitleBarLayoutMetrics();
            try
            {
                _accessibilitySettings.HighContrastChanged += AccessibilitySettings_HighContrastChanged;
                _highContrastChangedSubscribed = true;
            }
            catch (COMException)
            {
                // Some Windows/WinUI environments do not expose this WinRT event.
                // Title-bar customization still works; theme changes continue to update it.
            }
            UpdateTitleBarLayoutMetrics();
            UpdateTitleBarTheme();
            LayoutRoot.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        }
        else
        {
            AppTitleBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        ViewModel = new MainViewModel(hwnd);
        LayoutRoot.DataContext = ViewModel;
        ViewModel.InvitationRequested += OnInvitationRequested;
        Closed += (_, _) =>
        {
            if (_highContrastChangedSubscribed)
            {
                _accessibilitySettings.HighContrastChanged -= AccessibilitySettings_HighContrastChanged;
            }
            ViewModel.Dispose();
        };
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

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            ViewModel.AddFiles(items
                .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .Select(item => item.Path));
        }
        catch (Exception ex)
        {
            ViewModel.MessageText = $"Impossible de lire les éléments déposés : {ex.Message}";
        }
    }

    private async void OnInvitationRequested(object? sender, WifiDirectInvitationEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = LayoutRoot.XamlRoot,
                Title = "Appairage entrant",
                Content = $"{e.Request.DeviceInformation.Name} souhaite s’appairer en Wi-Fi Direct. Autoriser ?",
                PrimaryButtonText = "Autoriser",
                CloseButtonText = "Refuser",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            await ViewModel.RespondToInvitationAsync(e, result == ContentDialogResult.Primary);
        }
        catch (Exception ex)
        {
            ViewModel.MessageText = $"Impossible d’afficher la demande d’appairage : {ex.Message}";
            await ViewModel.RespondToInvitationAsync(e, false);
        }
    }

    private void UpdateTitleBarTheme()
    {
        if (_appWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        bool isDark = LayoutRoot.ActualTheme == ElementTheme.Dark;
        Color foreground = isDark
            ? Colors.White
            : Color.FromArgb(255, 32, 32, 32);
        Color inactiveForeground = isDark
            ? Color.FromArgb(255, 210, 210, 210)
            : Color.FromArgb(255, 80, 80, 80);
        Color hoverBackground = isDark
            ? Color.FromArgb(255, 55, 55, 55)
            : Color.FromArgb(255, 225, 225, 225);
        Color pressedBackground = isDark
            ? Color.FromArgb(255, 75, 75, 75)
            : Color.FromArgb(255, 205, 205, 205);

        var titleBar = _appWindow.TitleBar;
        bool isHighContrast;
        try
        {
            isHighContrast = _accessibilitySettings.HighContrast;
        }
        catch (COMException)
        {
            // Preserve system-selected caption colors if accessibility settings are unavailable.
            ResetTitleBarButtonColors(titleBar);
            return;
        }

        if (isHighContrast)
        {
            ResetTitleBarButtonColors(titleBar);
            return;
        }

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private static void ResetTitleBarButtonColors(AppWindowTitleBar titleBar)
    {
        titleBar.ButtonBackgroundColor = null;
        titleBar.ButtonForegroundColor = null;
        titleBar.ButtonHoverBackgroundColor = null;
        titleBar.ButtonHoverForegroundColor = null;
        titleBar.ButtonPressedBackgroundColor = null;
        titleBar.ButtonPressedForegroundColor = null;
        titleBar.ButtonInactiveBackgroundColor = null;
        titleBar.ButtonInactiveForegroundColor = null;
    }

    private void AccessibilitySettings_HighContrastChanged(AccessibilitySettings sender, object args)
    {
        _dispatcherQueue.TryEnqueue(UpdateTitleBarTheme);
    }

    private void UpdateTitleBarLayoutMetrics()
    {
        if (_appWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppTitleBar.ColumnDefinitions[0].Width = new GridLength(_appWindow.TitleBar.LeftInset);
        AppTitleBar.ColumnDefinitions[2].Width = new GridLength(_appWindow.TitleBar.RightInset);
    }
}
