using PulsarBattery.Tools;
using System;

namespace PulsarBattery.Services;

internal sealed record AppSettings
{
    public double PollIntervalMinutes { get; init; } = 1.0;

    public double LogIntervalMinutes { get; init; } = 5.0;

    public int AlertThresholdUnlockedPercent { get; init; } = 5;

    public int AlertThresholdLockedPercent { get; init; } = 30;

    public bool EnableBeeps { get; init; } = true;

    public string? LowBatterySoundPath { get; init; }

    public double AlertCooldownMinutes { get; init; } = 20.0;

    public bool MinimizeToTrayOnClose { get; init; } = true;

    /// <summary>True renders the battery percentage as the tray icon; false shows the app icon.</summary>
    public bool ShowBatteryInTray { get; init; } = true;

    public bool StartWithWindows { get; init; } = false;

    /// <summary>Locale override (e.g. "de-DE"); null means follow the system language.</summary>
    public string? Language { get; init; }

    public static AppSettings CreateDefaultsFromEnvironment()
    {
        return new AppSettings
        {
            AlertThresholdUnlockedPercent = ReadIntFromEnvironment(
                "BATTERY_LEVEL_ALERT_THRESHOLD",
                fallback: 5),
            AlertThresholdLockedPercent = ReadIntFromEnvironment(
                "BATTERY_LEVEL_ALERT_THRESHOLD_LOCKED",
                fallback: 30),
        };
    }

    public static AppSettings Sanitize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings with
        {
            PollIntervalMinutes = ClampFinite(settings.PollIntervalMinutes, 1, 120, 1),
            LogIntervalMinutes = ClampFinite(settings.LogIntervalMinutes, 1, 240, 5),
            AlertThresholdUnlockedPercent = Math.Clamp(settings.AlertThresholdUnlockedPercent, 1, 100),
            AlertThresholdLockedPercent = Math.Clamp(settings.AlertThresholdLockedPercent, 1, 100),
            AlertCooldownMinutes = ClampFinite(settings.AlertCooldownMinutes, 0, 24 * 60, 20),
            LowBatterySoundPath = string.IsNullOrWhiteSpace(settings.LowBatterySoundPath)
                ? null
                : settings.LowBatterySoundPath,
            Language = LocalizationService.NormalizeLocale(settings.Language),
        };
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }

    private static int ReadIntFromEnvironment(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) ? parsed : fallback;
    }

}

