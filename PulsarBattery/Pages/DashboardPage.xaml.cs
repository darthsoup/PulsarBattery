using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PulsarBattery.Pages;

public sealed partial class DashboardPage : Page
{
    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    public DashboardPage()
    {
        InitializeComponent();
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RetryConnectionAsync();
        }
    }
}