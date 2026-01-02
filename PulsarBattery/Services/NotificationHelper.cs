using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PulsarBattery.Services;

internal static class NotificationHelper
{
    private static bool _initialized;
    private static bool _registered;

    public static void Init()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            var manager = AppNotificationManager.Default;

            // Per quickstart: always hook before Register() so handling stays in this process.
            manager.NotificationInvoked += (_, args) =>
            {
                Debug.WriteLine($"Notification invoked: {args.Argument}");
            };

            manager.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            _registered = false;
            Debug.WriteLine($"Notification init/register failed: {ex}");
        }
    }

    public static void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Notification unregister failed: {ex}");
        }
        finally
        {
            _registered = false;
        }
    }

    public static void NotifyBatteryLevelChanged(int previousPercentage, int currentPercentage, bool isCharging, string? model)
    {
        if (!_initialized)
        {
            Init();
        }

        if (!_registered)
        {
            return;
        }

        try
        {
            var title = "Battery level changed";
            var line1 = string.IsNullOrWhiteSpace(model) ? $"From {previousPercentage}% to {currentPercentage}%" : $"{model}: From {previousPercentage}% to {currentPercentage}%";
            var line2 = isCharging ? "Charging" : "Not charging";

            var notification = new AppNotificationBuilder()
                .AddArgument("action", "batteryChanged")
                .AddArgument("source", "PulsarBattery")
                .AddArgument("from", previousPercentage.ToString())
                .AddArgument("to", currentPercentage.ToString())
                .AddText(title)
                .AddText(line1)
                .AddText(line2)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Battery notification failed: {ex}");
        }
    }
}