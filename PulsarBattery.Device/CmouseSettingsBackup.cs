namespace PulsarBattery.Device;

/// <summary>
/// Byte-for-byte copy of the 0x0000..0x00FF core settings region plus the PAW3955 DPI region when
/// present. Configuration data, not a dump of the MCU firmware.
/// </summary>
public sealed record CmouseSettingsBackup(
    string Model,
    int VendorId,
    int ProductId,
    byte Cid,
    byte Mid,
    string DevicePath,
    ConnectionKind Connection,
    int? LinkRateHz,
    string? FirmwareVersion,
    string? DongleFirmwareVersion,
    byte[] CoreSettingsRegion,
    ushort? ExtendedDpiAddress,
    byte[]? ExtendedDpiRegion);
