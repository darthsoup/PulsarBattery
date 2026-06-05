using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.Tools;
using System;
using System.Diagnostics;

namespace PulsarBattery.Pages;

public sealed partial class HistoryPage : Page
{
    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    public HistoryPage()
    {
        InitializeComponent();
        PreviousButton.Content = Loc.T("Previous");
        AutomationProperties.SetName(PreviousButton, Loc.T("Previous history page"));
        NextButton.Content = Loc.T("Next");
        AutomationProperties.SetName(NextButton, Loc.T("Next history page"));
        ClearButton.Content = Loc.T("Clear");
        AutomationProperties.SetName(ClearButton, Loc.T("Clear history"));
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.PreviousHistoryPage();
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.NextHistoryPage();
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is ViewModels.MainViewModel viewModel)
            {
                await viewModel.ClearHistoryAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HistoryPage] ClearHistory_Click: {ex.Message}");
        }
    }
}