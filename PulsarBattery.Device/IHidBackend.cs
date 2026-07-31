namespace PulsarBattery.Device;

public interface IHidBackend
{
    string Name { get; }

    DeviceStatus? ReadBatteryStatus(bool debug);

    /// <summary>
    /// Reads on-device settings where the protocol supports it. Backends for
    /// protocols without a settings register space return null.
    /// </summary>
    DeviceSettings? ReadSettings(bool debug) => null;

    bool SupportsSettingsWrite => false;

    /// <summary>
    /// Applies every non-null field of <paramref name="changes"/> to the
    /// device and verifies each by reading it back. Returns true only when
    /// all requested fields were applied and confirmed.
    /// </summary>
    bool ApplySettings(DeviceSettings changes, bool debug) => false;
}
