namespace PulsarBattery.Device;

public enum ConnectionKind
{
    Unknown,
    Wired,
    Dongle,
}

public sealed record DeviceStatus(
    int Percentage,
    bool IsCharging,
    string Model,
    ConnectionKind Connection = ConnectionKind.Unknown);
