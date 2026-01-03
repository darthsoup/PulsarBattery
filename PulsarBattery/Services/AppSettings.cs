using System;

namespace PulsarBattery.Services;

internal sealed record AppSettings
{
    public double PollIntervalMinutes { get; init; } = 1.0;

    public double LogIntervalMinutes { get; init; } = 5.0;

    public int AlertThresholdUnlockedPercent { get; init; } = 5;

    public int AlertThresholdLockedPercent { get; init; } = 30;

    public bool EnableBeeps { get; init; } = true;

    public double AlertCooldownMinutes { get; init; } = 20.0;

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
            PollIntervalMinutes = Clamp(settings.PollIntervalMinutes, min: 0.1, max: 120),
            LogIntervalMinutes = Clamp(settings.LogIntervalMinutes, min: 0.1, max: 240),
            AlertThresholdUnlockedPercent = Clamp(settings.AlertThresholdUnlockedPercent, min: 1, max: 100),
            AlertThresholdLockedPercent = Clamp(settings.AlertThresholdLockedPercent, min: 1, max: 100),
            AlertCooldownMinutes = Clamp(settings.AlertCooldownMinutes, min: 0, max: 24 * 60),
        };
    }

    private static int ReadIntFromEnvironment(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}

