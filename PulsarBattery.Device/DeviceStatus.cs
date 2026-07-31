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
public sealed record DeviceStatus(
    int Percentage,
    bool IsCharging,
    string Model,
    ConnectionKind Connection = ConnectionKind.Unknown,
    string? ConnectionName = null);
