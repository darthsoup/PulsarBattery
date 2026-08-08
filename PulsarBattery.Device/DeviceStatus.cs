namespace PulsarBattery.Device;

public enum ConnectionKind
{
    Unknown,
    Wired,
    Dongle,
}

/// <param name="ConnectionName">
/// HID product string of the transport device (e.g. "8K Dongle"); null when
/// unavailable or when the mouse is connected directly by cable.
/// </param>
/// <param name="FirmwareVersion">
/// The mouse's firmware version formatted "01.25"-style; null when the device
/// offers no way to read it (e.g. legacy protocol over the dongle, where only
/// the dongle's own version is visible).
/// </param>
/// <param name="LinkRateHz">
/// Live link rate of the current connection in Hz (wireless: 1000/2000/4000/
/// 8000, wired: 1000/8000); null when the protocol doesn't expose it.
/// </param>
/// <param name="VoltageMv">
/// Battery pack voltage in millivolts; null when the device doesn't report it.
/// </param>
/// <param name="SignalStrength">
/// Radio signal strength as a small bar count (roughly 0-4, higher is better),
/// not a percentage. Null when wired, unsupported, or unreadable.
/// </param>
/// <param name="DongleFirmwareVersion">
/// Firmware version of the receiver, distinct from the mouse's own; null when
/// wired or unsupported.
/// </param>
public sealed record DeviceStatus(
    int Percentage,
    bool IsCharging,
    string Model,
    ConnectionKind Connection = ConnectionKind.Unknown,
    string? ConnectionName = null,
    string? FirmwareVersion = null,
    int? LinkRateHz = null,
    int? VoltageMv = null,
    int? SignalStrength = null,
    string? DongleFirmwareVersion = null);
