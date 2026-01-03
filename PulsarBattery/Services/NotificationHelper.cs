using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Diagnostics;
using System.IO;

namespace PulsarBattery.Services;

internal static class NotificationHelper
{
    private static bool _initialized;
    private static bool _registered;

    public static void Init()
    {
        try
        {
            var manager = AppNotificationManager.Default;

            if (!_initialized)
            {
                // Per quickstart: always hook before Register() so handling stays in this process.
                manager.NotificationInvoked += (_, args) =>
                {
                    Debug.WriteLine($"Notification invoked: {args.Argument}");
                };

                _initialized = true;
            }

            if (_registered)
            {
                return;
            }

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
        Init();

        if (!_registered)
        {
            return;
        }

        try
        {
            var title = isCharging ? "Charging" : "Battery Update";

            // Build device info line
            var deviceLine = string.IsNullOrWhiteSpace(model) ? 
                $"Battery: {currentPercentage}%" : 
                $"{model}: {currentPercentage}%";

            var statusLine = isCharging
                ? $"Currently charging - {GetBatteryChangeText(previousPercentage, currentPercentage)}"
                : $"Not charging - {GetBatteryChangeText(previousPercentage, currentPercentage)}";

            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(deviceLine)
                .AddText(statusLine);

            var appLogoUri = TryGetNotificationLogoUri();
            if (appLogoUri is not null)
            {
                builder.SetAppLogoOverride(appLogoUri);
            }

            var notification = builder.BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Battery notification failed: {ex}");
        }
    }

    private static string GetBatteryChangeText(int previousPercentage, int currentPercentage)
    {
        var change = currentPercentage - previousPercentage;
        
        if (change > 0)
        {
            return $"+{change}% since last update";
        }
        else if (change < 0)
        {
            return $"{change}% since last update";
        }
        else
        {
            return "No change";
        }
    }

    private static Uri? TryGetNotificationLogoUri()
    {
        var assetDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (!Directory.Exists(assetDir))
        {
            return null;
        }

        var preferred = new[]
        {
            "icon.png",
            "AppIcon.png",
            "pulsar.png",
            "Square44x44Logo.scale-200.png",
            "Square44x44Logo.png",
        };

        string? assetPath = null;
        foreach (var name in preferred)
        {
            var candidate = Path.Combine(assetDir, name);
            if (File.Exists(candidate))
            {
                assetPath = candidate;
                break;
            }
        }

        if (assetPath is null)
        {
            foreach (var file in Directory.EnumerateFiles(assetDir, "*.png", SearchOption.TopDirectoryOnly))
            {
                assetPath = file;
                break;
            }
        }

        if (assetPath is null)
        {
            return null;
        }

        if (IsPackaged())
        {
            var fileName = Path.GetFileName(assetPath);
            return new Uri($"ms-appx:///Assets/{fileName}");
        }

        return new Uri(assetPath);
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void NotifyLowBattery(int batteryPercentage, int thresholdPercent, string? model)
    {
        Init();

        if (!_registered)
        {
            return;
        }

        try
        {
            var title = "Low Battery";

            var deviceLine = string.IsNullOrWhiteSpace(model)
                ? $"Battery: {batteryPercentage}%"
                : $"{model}: {batteryPercentage}%";

            var statusLine = $"Not charging (threshold: {thresholdPercent}%)";

            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(deviceLine)
                .AddText(statusLine);

            var appLogoUri = TryGetNotificationLogoUri();
            if (appLogoUri is not null)
            {
                builder.SetAppLogoOverride(appLogoUri);
            }

            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Low-battery notification failed: {ex}");
        }
    }
}
