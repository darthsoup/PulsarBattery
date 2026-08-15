namespace PulsarBattery.Device;

/// <summary>
/// On-device configuration. Null means the protocol does not expose the value; in
/// <c>ApplySettings</c> requests it means "leave unchanged".
/// </summary>
/// <param name="SleepSeconds">Idle delay before sleep; stored on-device in 10-second units.</param>
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
