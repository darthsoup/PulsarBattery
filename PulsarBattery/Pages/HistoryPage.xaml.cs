using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PulsarBattery.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
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
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            await viewModel.ClearHistoryAsync();
        }
    }
}