namespace PulsarBattery.Device;

public interface IHidBackend
{
    string Name { get; }

    DeviceStatus? ReadBatteryStatus(bool debug);

    /// <summary>
    /// Reads on-device settings where the protocol supports it. Backends for
    /// protocols without a settings register space return null.
    /// </summary>
    DeviceSettings? ReadSettings(bool debug) => null;

    /// <summary>
    /// Reads settings together with the device-specific ranges and read/write
    /// capabilities needed to render a safe editor. Older read-only backends
    /// can keep implementing <see cref="ReadSettings"/> only.
    /// </summary>
    DeviceSettingsSnapshot? ReadSettingsSnapshot(bool debug)
    {
        var values = ReadSettings(debug);
        if (values is null)
        {
            return null;
        }

        var readable = DeviceSettingField.None;
        if (values.PollingRateHz is not null) readable |= DeviceSettingField.PollingRate;
        if (values.DebounceMs is not null) readable |= DeviceSettingField.Debounce;
        if (values.MotionSync is not null) readable |= DeviceSettingField.MotionSync;
        if (values.Dpi is not null) readable |= DeviceSettingField.Dpi;
        if (values.DpiStage is not null) readable |= DeviceSettingField.DpiStage;
        if (values.LodMm10 is not null) readable |= DeviceSettingField.Lod;
        if (values.AngleSnap is not null) readable |= DeviceSettingField.AngleSnap;
        if (values.RippleControl is not null) readable |= DeviceSettingField.RippleControl;
        if (values.SleepSeconds is not null) readable |= DeviceSettingField.Sleep;

        return new DeviceSettingsSnapshot(
            values,
            new DeviceSettingsCapabilities(
                readable,
                DeviceSettingField.None,
                [],
                [],
                [],
                DpiStageCount: 0,
                DebounceMinimumMs: 0,
                DebounceMaximumMs: 0,
                SleepValuesSeconds: []));
    }

    bool SupportsSettingsWrite => false;

    /// <summary>
    /// Applies every non-null field of <paramref name="changes"/> to the
    /// device and verifies each by reading it back. Returns true only when
    /// all requested fields were applied and confirmed.
    /// </summary>
    bool ApplySettings(DeviceSettings changes, bool debug) => false;
}
