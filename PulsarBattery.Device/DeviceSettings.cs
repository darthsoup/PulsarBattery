namespace PulsarBattery.Device;

/// <summary>
/// On-device configuration values. Null means the device (or its protocol)
/// does not expose that value.
/// </summary>
public sealed record DeviceSettings(
    int? PollingRateHz = null,
    int? DebounceMs = null,
    bool? MotionSync = null,
    int? Dpi = null,
    int? DpiStage = null);
