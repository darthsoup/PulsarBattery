using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Services;

namespace PulsarBattery.Pages;

public sealed partial class SettingsPage : Page
{
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

    private void SendTestNotification_Click(object sender, RoutedEventArgs e)
    {
        NotificationHelper.Init();
        
        // Get actual battery data from the ViewModel
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            // Simulate a battery level drop of 5%
            var currentLevel = viewModel.BatteryPercentage;
            var previousLevel = currentLevel + 5;
            
            NotificationHelper.NotifyBatteryLevelChanged(
                previousLevel, 
                currentLevel, 
                viewModel.IsCharging, 
                viewModel.ModelName);
        }
        else
        {
            // Fallback to test data if ViewModel is not available
            NotificationHelper.NotifyBatteryLevelChanged(50, 45, isCharging: false, model: "Test Device");
        }
    }

    private void RefreshBatteryStatus_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.RefreshNow();
        }
    }
}
