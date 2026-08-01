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
public sealed record DeviceStatus(
    int Percentage,
    bool IsCharging,
    string Model,
    ConnectionKind Connection = ConnectionKind.Unknown,
    string? ConnectionName = null,
    string? FirmwareVersion = null);
