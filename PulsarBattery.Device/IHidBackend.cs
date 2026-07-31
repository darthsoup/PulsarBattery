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
}
