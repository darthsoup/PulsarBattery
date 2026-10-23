using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class Sonix64BackendTests
{
    private const DeviceSettingField AllWritable = DeviceSettingField.PollingRate
        | DeviceSettingField.Debounce | DeviceSettingField.MotionSync | DeviceSettingField.Dpi
        | DeviceSettingField.DpiStage | DeviceSettingField.Lod | DeviceSettingField.AngleSnap
        | DeviceSettingField.RippleControl;

    [Fact]
    public void BackendRequiresSettingsDiscoveryBeforeWriting()
    {
        var backend = new Sonix64Backend(new DeviceDescriptor("Test", 0, [], []));

        Assert.False(backend.SupportsSettingsWrite);
        Assert.False(backend.ApplySettings(new DeviceSettings(MotionSync: true), false));
    }

    [Fact]
    public void ValidBatchIncludesEverySupportedSetting()
    {
        var settings = new DeviceSettings(8000, 30, true, 26000, 8, 20, true, false);

        Assert.True(Sonix64Backend.ValidateChanges(settings, AllWritable));
        Assert.Equal(AllWritable, Sonix64Backend.GetWritableFields(settings));
    }

    [Fact]
    public void PartialDiscoveryCannotAuthorizeAnUnreadSetting()
    {
        var available = Sonix64Backend.GetWritableFields(new DeviceSettings(MotionSync: false));

        Assert.Equal(DeviceSettingField.MotionSync, available);
        Assert.True(Sonix64Backend.ValidateChanges(new DeviceSettings(MotionSync: true), available));
        Assert.False(Sonix64Backend.ValidateChanges(new DeviceSettings(MotionSync: true, Dpi: 800), available));
    }

    [Fact]
    public void UnknownWriteDomainsRemainReadableButCannotAuthorizeWrites()
    {
        var settings = new DeviceSettings(PollingRateHz: 16000, DebounceMs: 255, MotionSync: true,
            Dpi: 35000, DpiStage: 10, LodMm10: 30, SleepSeconds: 60);

        Assert.Equal(DeviceSettingField.MotionSync, Sonix64Backend.GetWritableFields(settings));
        Assert.Equal(DeviceSettingField.None, Sonix64Backend.GetWritableFields(new DeviceSettings()));
    }

    [Fact]
    public void InvalidLaterFieldRejectsTheWholeBatchBeforeAnyWrite()
    {
        var invalid = new[]
        {
            new DeviceSettings(MotionSync: true, DebounceMs: -1),
            new DeviceSettings(MotionSync: true, DebounceMs: 31),
            new DeviceSettings(MotionSync: true, Dpi: 49),
            new DeviceSettings(MotionSync: true, Dpi: 26001),
            new DeviceSettings(MotionSync: true, DpiStage: 0),
            new DeviceSettings(MotionSync: true, DpiStage: 9),
            new DeviceSettings(MotionSync: true, LodMm10: 11),
            new DeviceSettings(MotionSync: true, PollingRateHz: 3000),
            new DeviceSettings(MotionSync: true, SleepSeconds: 60),
        };

        foreach (var settings in invalid)
        {
            Assert.False(Sonix64Backend.ValidateChanges(settings, AllWritable));
        }
    }

    [Theory]
    [InlineData(125)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(4000)]
    [InlineData(8000)]
    public void DocumentedPollingRatesPassPreflight(int rate)
        => Assert.True(Sonix64Backend.ValidateChanges(new DeviceSettings(PollingRateHz: rate), DeviceSettingField.PollingRate));

    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(20)]
    public void DocumentedLodValuesPassPreflight(int lod)
        => Assert.True(Sonix64Backend.ValidateChanges(new DeviceSettings(LodMm10: lod), DeviceSettingField.Lod));
}
