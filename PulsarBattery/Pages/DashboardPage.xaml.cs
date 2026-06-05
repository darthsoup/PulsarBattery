using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.Tools;
using System;
using System.Diagnostics;

namespace PulsarBattery.Pages;

public sealed partial class DashboardPage : Page
{
    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    public DashboardPage()
    {
        InitializeComponent();
        RetryButton.Content = Loc.T("Retry");
        AutomationProperties.SetName(RetryButton, Loc.T("Retry connection"));
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
}