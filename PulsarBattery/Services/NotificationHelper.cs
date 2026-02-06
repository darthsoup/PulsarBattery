using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using System;
using System.Diagnostics;

namespace PulsarBattery.Services;

internal static class NotificationHelper
{
    private static bool _initialized;
    private static bool _registered;
    private const string DefaultLowBatterySoundUri = "ms-winsoundevent:Notification.Looping.Alarm2";
    private static MediaPlayer? _alertPlayer;

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

            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
            PlayLowBatterySound();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Low-battery notification failed: {ex}");
        }
    }

    public static void PlayLowBatterySound()
    {
        if (!AppSettingsService.Current.EnableBeeps)
        {
            return;
        }

        try
        {
            var soundUri = GetLowBatterySoundUri();
            if (soundUri is null)
            {
                return;
            }

            _alertPlayer ??= new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Alerts
            };
            _alertPlayer.Source = MediaSource.CreateFromUri(soundUri);
            _alertPlayer.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Low-battery sound failed: {ex}");
        }
    }

    private static Uri? GetLowBatterySoundUri()
    {
        var customPath = AppSettingsService.Current.LowBatterySoundPath;
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return new Uri(customPath);
        }

        return new Uri(DefaultLowBatterySoundUri);
    }
}
