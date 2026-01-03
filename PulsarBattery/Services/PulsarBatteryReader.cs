using PulsarBattery.Device;
using System;

namespace PulsarBattery.Services;

public sealed class PulsarBatteryReader
{
    public record BatteryStatus(int Percentage, bool IsCharging, string Model);

    private static readonly object GlobalReadLock = new();

    private readonly IHidBackend[] _backends;

    public PulsarBatteryReader()
    {
        _backends = new IHidBackend[]
        {
            new X2ClBackend(),
            new X2V1Backend(),
        };
    }

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
}
