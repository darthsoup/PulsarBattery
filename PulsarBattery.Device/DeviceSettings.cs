namespace PulsarBattery.Device;

/// <summary>
/// On-device configuration values. Null means the device (or its protocol)
/// does not expose that value — and in <c>ApplySettings</c> requests, null
/// means "leave unchanged".
/// </summary>
public sealed record DeviceSettings(
    int? PollingRateHz = null,
    int? DebounceMs = null,
    bool? MotionSync = null,
    int? Dpi = null,
    int? DpiStage = null,
    int? LodMm10 = null,
    bool? AngleSnap = null,
    bool? RippleControl = null);
