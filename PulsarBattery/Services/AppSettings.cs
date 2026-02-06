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
        return settings with
        {
            PollIntervalMinutes = Math.Clamp(settings.PollIntervalMinutes, 1, 120),
            LogIntervalMinutes = Math.Clamp(settings.LogIntervalMinutes, 1, 240),
            AlertThresholdUnlockedPercent = Math.Clamp(settings.AlertThresholdUnlockedPercent, 1, 100),
            AlertThresholdLockedPercent = Math.Clamp(settings.AlertThresholdLockedPercent, 1, 100),
            AlertCooldownMinutes = Math.Clamp(settings.AlertCooldownMinutes, 0, 24 * 60),
            LowBatterySoundPath = string.IsNullOrWhiteSpace(settings.LowBatterySoundPath)
                ? null
                : settings.LowBatterySoundPath,
        };
    }

    private static int ReadIntFromEnvironment(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) ? parsed : fallback;
    }

}

