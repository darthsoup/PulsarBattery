namespace PulsarBattery.Device;

public enum ConnectionKind
{
    Unknown,
    Wired,
    Dongle,
}

/// <param name="ConnectionName">HID product string of the transport (e.g. "8K Dongle"); null when wired.</param>
/// <param name="FirmwareVersion">Mouse firmware, "01.25"-style; null when the protocol cannot read it.</param>
/// <param name="LinkRateHz">Live link rate in Hz; null when the protocol does not expose it.</param>
/// <param name="VoltageMv">Battery pack voltage in millivolts; null when not reported.</param>
/// <param name="SignalStrength">Bar count (roughly 0-4, higher is better), not a percentage.</param>
/// <param name="DongleFirmwareVersion">Receiver firmware, distinct from the mouse's own.</param>
/// <param name="ProtocolModelId">
/// Protocol-level model identity; for cMouse this is CID in the high byte and MID in the low byte.
/// Kept separate from the display name because several editions share that name.
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
    string? DongleFirmwareVersion = null,
    int? ProtocolModelId = null);
