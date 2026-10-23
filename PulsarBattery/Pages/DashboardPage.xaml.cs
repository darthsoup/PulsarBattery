using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Tools;
using PulsarBattery.ViewModels;
using System;
using System.ComponentModel;

namespace PulsarBattery.Pages;

public sealed partial class DashboardPage : Page
{
    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    private MainViewModel? _subscribedViewModel;

    public DashboardPage()
    {
        InitializeComponent();
        RetryButton.Content = Loc.T("Retry");
        LastUpdatedLabel.Text = $"{Loc.T("Last updated")}: ";
        StaleDataInfoBar.Title = Loc.T("Device unavailable");
        AutomationProperties.SetName(RetryButton, Loc.T("Retry connection"));
        AutomationProperties.SetName(BatteryBar, Loc.T("Battery level"));
        RefreshIdleMeasure.Text = Loc.T("Refresh");
        RefreshBusyMeasure.Text = Loc.T("Updating...");
        ApplyRefreshState();

        Loaded += DashboardPage_Loaded;
        Unloaded += DashboardPage_Unloaded;
        ActualThemeChanged += DashboardPage_ActualThemeChanged;
    }

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeViewModel();
        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _subscribedViewModel = viewModel;
        }

        ApplyBatteryState();
        ApplyRefreshState();
    }

    // The view model outlives this page (rebuilt on every navigation), so unhooking here prevents leaks.
    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        RefreshProgress.IsActive = false;
        UnsubscribeViewModel();
    }

    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsLoading))
        {
            ApplyRefreshState();
        }

        if (e.PropertyName is nameof(MainViewModel.BatteryVisualState)
            or nameof(MainViewModel.BatteryPercentage)
            or nameof(MainViewModel.HasStaleData))
        {
            ApplyBatteryState();
        }
    }

    private void ApplyRefreshState()
    {
        var busy = ViewModel?.IsLoading == true;
        RefreshProgress.IsActive = busy;
        RefreshIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        RefreshButtonLabel.Text = busy ? "Updating..." : "Refresh";
        var description = Loc.T(busy ? "Updating..." : "Refresh battery status");
        AutomationProperties.SetName(RefreshButton, description);
        ToolTipService.SetToolTip(RefreshButton, busy ? description : $"{description} (F5)");
    }

    // The success/critical fills differ between light and dark.
    private void DashboardPage_ActualThemeChanged(FrameworkElement sender, object args) => ApplyBatteryState();

    // Re-resolve state brushes after theme changes so the previous theme's instances are not retained.
    private void ApplyBatteryState()
    {
        var viewModel = ViewModel;
        var isStale = viewModel?.HasStaleData == true;
        BatteryLevelLabel.Text = isStale
            ? "Last known battery level"
            : viewModel?.BatteryVisualState == MainViewModel.BatteryState.Low
                ? "Low Battery"
                : "Battery level";

        AutomationProperties.SetName(BatteryBar, Loc.T(BatteryLevelLabel.Text));
        if (viewModel is not null)
        {
            AutomationProperties.SetName(PercentageText,
                $"{Loc.T(BatteryLevelLabel.Text)}: {viewModel.BatteryPercentage}%");
        }

        var resources = Application.Current.Resources;
        if (isStale)
        {
            PercentageText.Foreground = (Brush)resources["TextFillColorSecondaryBrush"];
            BatteryBar.Foreground = (Brush)resources["TextFillColorSecondaryBrush"];
            return;
        }

        switch (viewModel?.BatteryVisualState)
        {
            case MainViewModel.BatteryState.Charging:
                PercentageText.Foreground = (Brush)resources["SystemFillColorSuccessBrush"];
                BatteryBar.Foreground = (Brush)resources["SystemFillColorSuccessBrush"];
                break;
            case MainViewModel.BatteryState.Low:
                PercentageText.Foreground = (Brush)resources["SystemFillColorCriticalBrush"];
                BatteryBar.Foreground = (Brush)resources["SystemFillColorCriticalBrush"];
                break;
            default:
                PercentageText.Foreground = (Brush)resources["AccentTextFillColorPrimaryBrush"];
                BatteryBar.ClearValue(Control.ForegroundProperty);
                break;
        }
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel is { CanRefresh: true } viewModel)
            {
                await viewModel.RetryConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(nameof(DashboardPage), ex);
        }
    }

    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.SelectHistoryTab();
        }
    }
}
