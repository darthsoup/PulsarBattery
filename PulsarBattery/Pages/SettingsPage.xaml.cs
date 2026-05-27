using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Services;
using PulsarBattery.Tools;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace PulsarBattery.Pages;

public sealed partial class SettingsPage : Page
{
    public string AppVersion { get; } = GetAppVersion();

    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    private bool _isUpdatingStartWithWindowsToggle;

    private static string GetAppVersion()
    {
        try
        {
            var v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "develop" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
    }

    public SettingsPage()
    {
        InitializeComponent();
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        PollIntervalCard.Header = Loc.T("Battery check interval");
        PollIntervalCard.Description = Loc.T("How often the app polls the device and updates the dashboard");

        LogIntervalCard.Header = Loc.T("Log interval");
        LogIntervalCard.Description = Loc.T("How often readings are added to history");

        LowBatteryExpander.Header = Loc.T("Low battery alerts");
        LowBatteryExpander.Description = Loc.T("Configure thresholds, sound, and cooldown for low-battery notifications");

        AlertThresholdUnlockedCard.Header = Loc.T("Alert threshold (unlocked)");
        AlertThresholdLockedCard.Header = Loc.T("Alert threshold (locked)");
        AlertCooldownCard.Header = Loc.T("Alert cooldown");

        EnableBeepsCard.Header = Loc.T("Enable beeps");
        AutomationProperties.SetName(EnableBeepsToggle, Loc.T("Enable beeps"));

        AlertSoundCard.Header = Loc.T("Alert sound");

        ChooseSoundButton.Content = Loc.T("Choose");
        AutomationProperties.SetName(ChooseSoundButton, Loc.T("Choose alert sound file"));

        ClearSoundButton.Content = Loc.T("Clear");
        AutomationProperties.SetName(ClearSoundButton, Loc.T("Clear alert sound"));

        QuickActionsCard.Header = Loc.T("Quick actions");
        QuickActionsCard.Description = Loc.T("Send a test Windows notification or refresh the battery reading");

        SendLowBatteryTestButton.Content = Loc.T("Send low battery test");
        AutomationProperties.SetName(SendLowBatteryTestButton, Loc.T("Send low battery test notification"));

        RefreshBatteryStatusButton.Content = Loc.T("Refresh battery status");
        AutomationProperties.SetName(RefreshBatteryStatusButton, Loc.T("Refresh battery status"));

        MinimizeToTrayCard.Header = Loc.T("Minimize to tray on close");
        MinimizeToTrayCard.Description = Loc.T("When enabled, clicking the window close button minimizes to system tray. When disabled, the application will exit.");
        AutomationProperties.SetName(MinimizeToTrayToggle, Loc.T("Minimize to tray on close"));

        StartWithWindowsCard.Header = Loc.T("Start with Windows");
        StartWithWindowsCard.Description = Loc.T("Launches Pulsar Battery in the background when you sign in.");
        AutomationProperties.SetName(StartWithWindowsToggle, Loc.T("Start with Windows"));

        ViewOnGitHubCard.Header = Loc.T("View on GitHub");
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
        try
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] ChooseLowBatterySound_Click: {ex.Message}");
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
        try
        {
            await HandleStartWithWindowsToggledAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] StartWithWindowsToggle_Toggled: {ex.Message}");
        }
    }

    private async Task HandleStartWithWindowsToggledAsync()
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
            if (!SelfInstallService.IsCurrentExecutableBundled())
            {
                await ShowBundledBuildRequiredDialogAsync();
                ApplyStartWithWindowsToggleState(viewModel.StartWithWindows);
                return;
            }

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

        if (desiredState && !SelfInstallService.IsCurrentExecutableBundled())
        {
            await ShowBundledBuildRequiredDialogAsync();
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
        var installedExeTargetPath = string.IsNullOrWhiteSpace(sourceExe)
            ? Path.Combine(installDirectory, "PulsarBattery.exe")
            : Path.Combine(installDirectory, Path.GetFileName(sourceExe));

        var hasExistingAutostart = StartupRegistrationService.TryGetRegistrationState(out var autostartState)
            && autostartState.IsEnabled;
        var existingAutostartPath = hasExistingAutostart
            ? autostartState.ExecutablePath ?? "(unknown path)"
            : string.Empty;
        var isSameAutostartTarget = hasExistingAutostart &&
            ArePathsEqual(existingAutostartPath, installedExeTargetPath);
        var primaryButtonText = !hasExistingAutostart
            ? "Install and Enable"
            : isSameAutostartTarget
                ? "Update and Enable"
                : "Replace and Enable";
        var titleText = !hasExistingAutostart
            ? "Enable Autostart"
            : isSameAutostartTarget
                ? "Update installed autostart version?"
                : "Replace existing autostart?";
        var autostartSection = hasExistingAutostart
            ? isSameAutostartTarget
                ? "Windows autostart is already configured for the installed location.\n" +
                  $"Current autostart target:\n{existingAutostartPath}\n\n" +
                  "If you continue, the installed executable at this path will be updated.\n\n"
                : "Windows autostart is already configured.\n" +
                  $"Current autostart target:\n{existingAutostartPath}\n\n" +
                  $"If you continue, it will be replaced with:\n{installedExeTargetPath}\n\n"
            : string.Empty;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = titleText,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                Text =
                    "To enable autostart reliably, Pulsar Battery must be installed in a stable location.\n\n" +
                    autostartSection +
                    "If you continue:\n" +
                    $"1. Only this executable is copied to:\n{installedExeTargetPath}\n\n" +
                    "2. Windows autostart is registered for that installed executable.\n" +
                    $"3. The current executable is closed and deleted:\n{sourceExe}",
                TextWrapping = TextWrapping.Wrap
            }
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private static bool ArePathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            var fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private Task ShowErrorDialogAsync(string message)
    {
        ShowInstallInfoBar(InfoBarSeverity.Error, "Installation failed", message);
        return Task.CompletedTask;
    }

    private Task ShowBundledBuildRequiredDialogAsync()
    {
        ShowInstallInfoBar(
            InfoBarSeverity.Informational,
            "Autostart unavailable",
            "Autostart can only be enabled from a bundled single-file PulsarBattery.exe. " +
            "Please launch the published single-file build and try again.");
        return Task.CompletedTask;
    }

    private void ShowInstallInfoBar(InfoBarSeverity severity, string title, string message)
    {
        InstallInfoBar.IsOpen = false;
        InstallInfoBar.Severity = severity;
        InstallInfoBar.Title = title;
        InstallInfoBar.Message = message;
        InstallInfoBar.IsOpen = true;
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

    private async void GitHubCard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri("https://github.com/darthsoup/PulsarBattery"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] GitHubCard_Click: {ex.Message}");
        }
    }
}
