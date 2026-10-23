using PulsarBattery.Services;

namespace PulsarBattery.Device.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidIntervalsFallBackToFiniteDefaults(double invalidValue)
    {
        var result = AppSettings.Sanitize(new AppSettings
        {
            PollIntervalMinutes = invalidValue,
            LogIntervalMinutes = invalidValue,
            AlertCooldownMinutes = invalidValue,
        });

        Assert.Equal(1, result.PollIntervalMinutes);
        Assert.Equal(5, result.LogIntervalMinutes);
        Assert.Equal(20, result.AlertCooldownMinutes);
    }

    [Fact]
    public void OutOfRangeValuesAreClampedAndLocaleIsNormalized()
    {
        var result = AppSettings.Sanitize(new AppSettings
        {
            PollIntervalMinutes = 0,
            LogIntervalMinutes = 1000,
            AlertCooldownMinutes = -20,
            AlertThresholdUnlockedPercent = -1,
            AlertThresholdLockedPercent = 101,
            LowBatterySoundPath = "  ",
            Language = "  DE-de  ",
        });

        Assert.Equal(1, result.PollIntervalMinutes);
        Assert.Equal(240, result.LogIntervalMinutes);
        Assert.Equal(0, result.AlertCooldownMinutes);
        Assert.Equal(1, result.AlertThresholdUnlockedPercent);
        Assert.Equal(100, result.AlertThresholdLockedPercent);
        Assert.Null(result.LowBatterySoundPath);
        Assert.Equal("de-DE", result.Language);
        Assert.Equal(result, AppSettings.Sanitize(result));
    }

    [Fact]
    public void ValidSettingsRemainUnchanged()
    {
        var settings = new AppSettings
        {
            PollIntervalMinutes = 2.5,
            LogIntervalMinutes = 7.5,
            AlertCooldownMinutes = 0,
            LowBatterySoundPath = @"C:\Sounds\battery.wav",
            Language = "en-US",
            EnableBeeps = false,
            ShowBatteryInTray = false,
        };

        Assert.Equal(settings, AppSettings.Sanitize(settings));
    }

    [Fact]
    public void UnsupportedLocaleFallsBackToSystemLanguage()
    {
        Assert.Null(AppSettings.Sanitize(new AppSettings { Language = "unsupported" }).Language);
    }
}
