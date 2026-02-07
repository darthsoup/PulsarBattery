using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Services;
using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PulsarBattery.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _isUpdatingStartWithWindowsToggle;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // Don't steal focus from inputs. Most controls (buttons, toggles, etc.) will naturally take focus on click.
        if (FindAncestor<NumberBox>(source) is not null || FindAncestor<TextBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ButtonBase>(source) is not null || FindAncestor<ToggleSwitch>(source) is not null)
        {
            return;
        }

        try
        {
            FocusSink.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        catch
        {
            // ignore
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void RefreshBatteryStatus_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.RefreshNow();
        }
    }

    private async void ChooseLowBatterySound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        var window = App.MainWindow;
        if (window is null)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary
        };
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            viewModel.LowBatterySoundPath = file.Path;
        }
    }

    private void ClearLowBatterySound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.LowBatterySoundPath = null;
        }
    }

    private void SendLowBatteryTest_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            NotificationHelper.NotifyLowBattery(
                batteryPercentage: Math.Max(1, viewModel.AlertThresholdUnlockedPercent - 1),
                thresholdPercent: viewModel.AlertThresholdUnlockedPercent,
                model: viewModel.ModelName);
        }
        else
        {
            NotificationHelper.NotifyLowBattery(10, 15, model: "Test Device");
        }
    }

    private async void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingStartWithWindowsToggle)
        {
            return;
        }

        if (DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        var desiredState = StartWithWindowsToggle.IsOn;
        if (desiredState == viewModel.StartWithWindows)
        {
            return;
        }

        if (desiredState && !SelfInstallService.IsRunningFromInstallDirectory())
        {
            var confirmed = await ShowAutostartInstallDialogAsync();
            if (!confirmed)
            {
                ApplyStartWithWindowsToggleState(viewModel.StartWithWindows);
                return;
            }

            StartWithWindowsToggle.IsEnabled = false;
            var result = SelfInstallService.InstallCurrentBuildAndEnableAutostart();
            StartWithWindowsToggle.IsEnabled = true;

            if (!result.Success)
            {
                await ShowErrorDialogAsync(result.ErrorMessage ?? "Installation failed.");
                ApplyStartWithWindowsToggleState(viewModel.StartWithWindows);
                return;
            }

            if (result.RequiresRestart)
            {
                App.ExitApplication();
                return;
            }

            viewModel.StartWithWindows = true;
            ApplyStartWithWindowsToggleState(viewModel.StartWithWindows);
            return;
        }

        viewModel.StartWithWindows = desiredState;
        ApplyStartWithWindowsToggleState(viewModel.StartWithWindows);
    }

    private async Task<bool> ShowAutostartInstallDialogAsync()
    {
        var sourceExe = SelfInstallService.GetCurrentExecutablePath() ?? "Unknown";
        var installDirectory = SelfInstallService.GetInstallDirectory();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Enable Autostart",
            PrimaryButtonText = "Install and Enable",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                Text =
                    "To enable autostart reliably, Pulsar Battery must be installed in a stable location.\n\n" +
                    "If you continue:\n" +
                    $"1. Only the executable is copied to:\n{installDirectory}\n\n" +
                    "2. Windows autostart is registered for that installed executable.\n" +
                    $"3. The current executable is closed and deleted:\n{sourceExe}",
                TextWrapping = TextWrapping.Wrap
            }
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task ShowErrorDialogAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Installation failed",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private void ApplyStartWithWindowsToggleState(bool value)
    {
        _isUpdatingStartWithWindowsToggle = true;
        try
        {
            StartWithWindowsToggle.IsOn = value;
        }
        finally
        {
            _isUpdatingStartWithWindowsToggle = false;
        }
    }
}
