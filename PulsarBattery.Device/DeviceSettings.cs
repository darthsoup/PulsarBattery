namespace PulsarBattery.Device;

/// <summary>
/// On-device configuration values. Null means the device (or its protocol)
/// does not expose that value — and in <c>ApplySettings</c> requests, null
/// means "leave unchanged".
/// </summary>
/// <param name="SleepSeconds">
/// Idle delay before the mouse sleeps, in seconds. Stored on-device as a count
/// of 10-second units, so the values the vendor tool offers are 10s, 30s, 1min,
/// 5min, 10min and 30min.
/// </param>
public sealed record DeviceSettings(
    int? PollingRateHz = null,
    int? DebounceMs = null,
    bool? MotionSync = null,
    int? Dpi = null,
    int? DpiStage = null,
    int? LodMm10 = null,
    bool? AngleSnap = null,
    bool? RippleControl = null,
    int? SleepSeconds = null);
