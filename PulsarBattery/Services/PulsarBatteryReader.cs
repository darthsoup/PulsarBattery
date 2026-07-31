using PulsarBattery.Device;
using System.Collections.Generic;

namespace PulsarBattery.Services;

public sealed class PulsarBatteryReader
{
    public record BatteryStatus(int Percentage, bool IsCharging, string Model);

    private static readonly object GlobalReadLock = new();

    private readonly IReadOnlyList<IHidBackend> _backends = DeviceRegistry.CreateBackends();

    public BatteryStatus? ReadBatteryStatus(bool debug = false)
    {
        lock (GlobalReadLock)
        {
            foreach (var backend in _backends)
            {
                var status = backend.ReadBatteryStatus(debug);
                if (status is not null)
                {
                    return new BatteryStatus(status.Percentage, status.IsCharging, status.Model);
                }
            }

            return null;
        }
    }

    public DeviceSettings? ReadDeviceSettings(bool debug = false)
    {
        lock (GlobalReadLock)
        {
            foreach (var backend in _backends)
            {
                var settings = backend.ReadSettings(debug);
                if (settings is not null)
                {
                    return settings;
                }
            }

            return null;
        }
    }
}
