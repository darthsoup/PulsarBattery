using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Tools;
using PulsarBattery.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace PulsarBattery.Pages;

public sealed partial class DashboardPage : Page
{
    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    private MainViewModel? _subscribedViewModel;

    public DashboardPage()
    {
        InitializeComponent();
        RetryButton.Content = Loc.T("Retry");
        AutomationProperties.SetName(RetryButton, Loc.T("Retry connection"));

        Loaded += DashboardPage_Loaded;
        Unloaded += DashboardPage_Unloaded;
        ActualThemeChanged += DashboardPage_ActualThemeChanged;
    }

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _subscribedViewModel = viewModel;
        }

        ApplyBatteryState();
    }

    // The view model outlives this page (pages are rebuilt on every navigation),
    // so unhooking here is what prevents handler leaks.
    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.BatteryVisualState))
        {
            ApplyBatteryState();
        }
    }

    // The success/critical fills differ between light and dark.
    private void DashboardPage_ActualThemeChanged(FrameworkElement sender, object args) => ApplyBatteryState();

    /// <summary>
    /// Applied imperatively rather than bound so a runtime theme switch can
    /// re-resolve the brushes — a binding would keep the stale theme's instances.
    /// </summary>
    private void ApplyBatteryState()
    {
        var resources = Application.Current.Resources;
        switch (ViewModel?.BatteryVisualState)
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
                BatteryBar.ClearValue(Control.ForegroundProperty); // restore the template's accent
                break;
        }
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel is not null)
            {
                await ViewModel.RetryConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DashboardPage] RetryConnection_Click: {ex.Message}");
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
