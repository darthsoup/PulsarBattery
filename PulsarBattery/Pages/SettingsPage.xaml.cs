using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.Services;

namespace PulsarBattery.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        RefreshNotificationStatus();
    }

    private void SendTestNotification_Click(object sender, RoutedEventArgs e)
    {
        NotificationHelper.Init();
        NotificationHelper.NotifyBatteryLevelChanged(50, 45, isCharging: false, model: "Device Test");
        RefreshNotificationStatus();
    }

    private void RefreshNotificationStatus_Click(object sender, RoutedEventArgs e)
    {
        RefreshNotificationStatus();
    }

    private void RefreshNotificationStatus()
    {
        NotificationHelper.Init();

        // Keep UI simple now that helper no longer exposes debug status strings.
        NotificationEnvText.Text = "";
        NotificationRegText.Text = "";
    }
}