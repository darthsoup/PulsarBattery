using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using PulsarBattery.Tools;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using System;

namespace PulsarBattery.Services;

internal static class NotificationHelper
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _registered;
    private static bool _shuttingDown;
    private static MediaPlayer? _alertPlayer;

    public static void Init()
    {
        lock (Gate)
        {
            EnsureRegistered();
        }
    }

    private static bool EnsureRegistered()
    {
        if (_shuttingDown)
        {
            return false;
        }

        try
        {
            var manager = AppNotificationManager.Default;

            if (!_initialized)
            {
                // Per quickstart: always hook before Register() so handling stays in this process.
                manager.NotificationInvoked += (_, args) =>
                {
                    Log.Info(nameof(NotificationHelper), $"Notification invoked: {args.Argument}");
                };

                _initialized = true;
            }

            if (_registered)
            {
                return true;
            }

            manager.Register();
            _registered = true;
            return true;
        }
        catch (Exception ex)
        {
            _registered = false;
            Log.Error(nameof(NotificationHelper), $"Notification init/register failed: {ex}");
            return false;
        }
    }

    public static void Unregister()
    {
        lock (Gate)
        {
            _shuttingDown = true;
            StopLowBatterySound();
            try
            {
                _alertPlayer?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(nameof(NotificationHelper), $"Alert player disposal failed: {ex}");
            }
            finally
            {
                _alertPlayer = null;
            }

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
                Log.Error(nameof(NotificationHelper), $"Notification unregister failed: {ex}");
            }
            finally
            {
                _registered = false;
            }
        }
    }

    public static void NotifyBatteryLevelChanged(int previousPercentage, int currentPercentage, bool isCharging, string? model)
    {
        lock (Gate)
        {
            if (!EnsureRegistered())
            {
                return;
            }

            try
            {
                var title = isCharging ? Loc.T("Charging") : Loc.T("Battery Update");

                var deviceLine = string.IsNullOrWhiteSpace(model) ?
                    string.Format(Loc.T("Battery: {0}%"), currentPercentage) :
                    $"{model}: {currentPercentage}%";

                var changeText = GetBatteryChangeText(previousPercentage, currentPercentage);
                var statusLine = isCharging
                    ? string.Format(Loc.T("Currently charging - {0}"), changeText)
                    : string.Format(Loc.T("Not charging - {0}"), changeText);

                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(deviceLine)
                    .AddText(statusLine);
                if (!AppSettingsService.Current.EnableBeeps)
                {
                    builder.MuteAudio();
                }

                var notification = builder.BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(NotificationHelper), $"Battery notification failed: {ex}");
            }
        }
    }

    private static string GetBatteryChangeText(int previousPercentage, int currentPercentage)
    {
        var change = currentPercentage - previousPercentage;
        
        if (change > 0)
        {
            return string.Format(Loc.T("+{0}% since last update"), change);
        }
        else if (change < 0)
        {
            return string.Format(Loc.T("{0}% since last update"), change);
        }
        else
        {
            return Loc.T("No change");
        }
    }

    public static bool NotifyLowBattery(int batteryPercentage, int thresholdPercent, string? model)
    {
        lock (Gate)
        {
            if (!EnsureRegistered())
            {
                return false;
            }

            try
            {
                var title = Loc.T("Low Battery");

                var deviceLine = string.IsNullOrWhiteSpace(model)
                    ? string.Format(Loc.T("Battery: {0}%"), batteryPercentage)
                    : $"{model}: {batteryPercentage}%";

                var statusLine = string.Format(Loc.T("Not charging (threshold: {0}%)"), thresholdPercent);
                var settings = AppSettingsService.Current;
                var customSoundUri = settings.EnableBeeps
                    ? GetCustomLowBatterySoundUri(settings.LowBatterySoundPath)
                    : null;

                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(deviceLine)
                    .AddText(statusLine);
                if (!settings.EnableBeeps || customSoundUri is not null)
                {
                    builder.MuteAudio();
                }
                else
                {
                    builder.SetAudioEvent(AppNotificationSoundEvent.Alarm2);
                }

                var notification = builder.BuildNotification();
                AppNotificationManager.Default.Show(notification);
                StopLowBatterySound();
                if (customSoundUri is not null)
                {
                    PlayCustomLowBatterySound(customSoundUri);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(nameof(NotificationHelper), $"Low-battery notification failed: {ex}");
                return false;
            }
        }
    }

    public static void StopLowBatterySound()
    {
        lock (Gate)
        {
            try
            {
                if (_alertPlayer is null)
                {
                    return;
                }

                var previousSource = _alertPlayer.Source;
                _alertPlayer.Pause();
                _alertPlayer.Source = null;
                (previousSource as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(nameof(NotificationHelper), $"Stopping low-battery sound failed: {ex}");
            }
        }
    }

    private static void PlayCustomLowBatterySound(Uri soundUri)
    {
        try
        {
            if (_alertPlayer is null)
            {
                _alertPlayer = new MediaPlayer
                {
                    AudioCategory = MediaPlayerAudioCategory.Alerts
                };
                _alertPlayer.MediaFailed += (_, args) =>
                    Log.Error(nameof(NotificationHelper), $"Low-battery sound playback failed: {args.ErrorMessage}");
            }

            _alertPlayer.Source = MediaSource.CreateFromUri(soundUri);
            _alertPlayer.Play();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(NotificationHelper), $"Low-battery sound failed: {ex}");
        }
    }

    private static Uri? GetCustomLowBatterySoundUri(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath) &&
            Uri.TryCreate(Path.GetFullPath(customPath), UriKind.Absolute, out var soundUri) && soundUri.IsFile)
        {
            return soundUri;
        }

        return null;
    }
}
