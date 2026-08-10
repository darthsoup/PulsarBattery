using System;
using System.Collections.Generic;

namespace PulsarBattery.Device;

[Flags]
public enum DeviceSettingField
{
    None = 0,
    PollingRate = 1 << 0,
    Debounce = 1 << 1,
    MotionSync = 1 << 2,
    Dpi = 1 << 3,
    DpiStage = 1 << 4,
    Lod = 1 << 5,
    AngleSnap = 1 << 6,
    RippleControl = 1 << 7,
    Sleep = 1 << 8,
}

/// <summary>
/// Describes where a backend's write implementation has been validated. This
/// is deliberately separate from <see cref="DeviceSettingsCapabilities.Writable"/>:
/// a vendor-derived implementation can be enabled while still telling the UI
/// that it has not been exercised on matching hardware yet.
/// </summary>
public enum DeviceSettingsWriteTrust
{
    Unavailable = 0,
    VendorDerived,
    HardwareVerified,
}

public sealed record DeviceValueRange(int Minimum, int Maximum, int Step)
{
    public bool Contains(int value) =>
        Step > 0
        && value >= Minimum
        && value <= Maximum
        && (value - Minimum) % Step == 0;
}

public sealed record DeviceSettingsCapabilities(
    DeviceSettingField Readable,
    DeviceSettingField Writable,
    IReadOnlyList<int> PollingRatesHz,
    IReadOnlyList<int> LodValuesMm10,
    IReadOnlyList<DeviceValueRange> DpiRanges,
    int DpiStageCount,
    int DebounceMinimumMs,
    int DebounceMaximumMs,
    IReadOnlyList<int> SleepValuesSeconds,
    DeviceSettingsWriteTrust WriteTrust = DeviceSettingsWriteTrust.Unavailable,
    bool HasPersistentBackup = false)
{
    public bool CanRead(DeviceSettingField field) =>
        field != DeviceSettingField.None && (Readable & field) == field;

    public bool CanWrite(DeviceSettingField field) =>
        field != DeviceSettingField.None && (Writable & field) == field;
}

public sealed record DeviceSettingsSnapshot(
    DeviceSettings Values,
    DeviceSettingsCapabilities Capabilities);
