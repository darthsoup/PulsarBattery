using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PulsarBattery.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            await viewModel.RetryConnectionAsync();
        }
    }
}