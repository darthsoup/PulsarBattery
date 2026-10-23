using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class CmouseSettingsWriteValidationTests
{
    private static readonly CmouseDeviceProfile Profile = new(
        0x57,
        0x01,
        "X2 CrazyLight",
        Legacy17SensorKind.PulsarXs1,
        DeviceSettingsWriteTrust.HardwareVerified);

    private const DeviceSettingField AllFields = DeviceSettingField.PollingRate
        | DeviceSettingField.Debounce
        | DeviceSettingField.MotionSync
        | DeviceSettingField.Dpi
        | DeviceSettingField.DpiStage
        | DeviceSettingField.Lod
        | DeviceSettingField.AngleSnap
        | DeviceSettingField.RippleControl
        | DeviceSettingField.Sleep;

    public static TheoryData<DeviceSettings, DeviceSettingField> ValidChanges => new()
    {
        { new(PollingRateHz: 1000), DeviceSettingField.PollingRate },
        { new(DebounceMs: 0), DeviceSettingField.Debounce },
        { new(MotionSync: false), DeviceSettingField.MotionSync },
        { new(Dpi: 800), DeviceSettingField.Dpi },
        { new(DpiStage: 1), DeviceSettingField.DpiStage },
        { new(LodMm10: 10), DeviceSettingField.Lod },
        { new(AngleSnap: false), DeviceSettingField.AngleSnap },
        { new(RippleControl: false), DeviceSettingField.RippleControl },
        { new(SleepSeconds: 60), DeviceSettingField.Sleep },
    };

    [Theory]
    [MemberData(nameof(ValidChanges))]
    public void EveryChangedFieldRequiresItsOwnWritableCapability(
        DeviceSettings changes,
        DeviceSettingField requiredField)
    {
        Assert.True(CmouseLegacyBackend.ValidateChanges(Profile, changes, requiredField));
        Assert.False(CmouseLegacyBackend.ValidateChanges(Profile, changes, AllFields & ~requiredField));
    }

    [Fact]
    public void UnreadableDpiCannotBeOverwrittenAlongsideAReadableSetting()
    {
        var changes = new DeviceSettings(DebounceMs: 4, Dpi: 800);

        Assert.False(CmouseLegacyBackend.ValidateChanges(Profile, changes, DeviceSettingField.Debounce));
        Assert.True(CmouseLegacyBackend.ValidateChanges(
            Profile,
            changes,
            DeviceSettingField.Debounce | DeviceSettingField.Dpi));
    }

    [Fact]
    public void EmptyNullAndUnavailableWritesAreRejected()
    {
        Assert.False(CmouseLegacyBackend.ValidateChanges(Profile, null, AllFields));
        Assert.False(CmouseLegacyBackend.ValidateChanges(Profile, new DeviceSettings(), AllFields));
        Assert.False(CmouseLegacyBackend.ValidateChanges(
            Profile with { WriteTrust = DeviceSettingsWriteTrust.Unavailable },
            new DeviceSettings(Dpi: 800),
            AllFields));
    }

    public static TheoryData<DeviceSettings> InvalidChanges => new()
    {
        new(PollingRateHz: 0),
        new(PollingRateHz: 1500),
        new(DebounceMs: -1),
        new(DebounceMs: 16),
        new(DpiStage: 0),
        new(DpiStage: 7),
        new(LodMm10: 8),
        new(SleepSeconds: 20),
        new(Dpi: 0),
        new(Dpi: 801),
        new(Dpi: 32_100),
    };

    [Theory]
    [MemberData(nameof(InvalidChanges))]
    public void WritableCapabilitiesDoNotBypassValueValidation(DeviceSettings changes)
    {
        Assert.False(CmouseLegacyBackend.ValidateChanges(Profile, changes, AllFields));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(42_000, true)]
    [InlineData(42_001, false)]
    public void DpiValidationUsesTheIdentifiedSensor(int dpi, bool expected)
    {
        var profile = Profile with { Sensor = Legacy17SensorKind.Paw3955 };

        Assert.Equal(expected, CmouseLegacyBackend.ValidateChanges(
            profile,
            new DeviceSettings(Dpi: dpi),
            DeviceSettingField.Dpi));
    }
}
