using PulsarBattery.Device;
using System.Collections.Generic;

namespace PulsarBattery.Services;

public sealed class PulsarBatteryReader
{
    public record BatteryStatus(
        int Percentage,
        bool IsCharging,
        string Model,
        ConnectionKind Connection = ConnectionKind.Unknown,
        string? ConnectionName = null);

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
                    return new BatteryStatus(status.Percentage, status.IsCharging, status.Model, status.Connection, status.ConnectionName);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Applies the non-null fields of <paramref name="changes"/> to the first
    /// backend that supports writes. Returns null when no writable device is
    /// present, otherwise whether every change was applied and confirmed.
    /// </summary>
    public bool? ApplyDeviceSettings(DeviceSettings changes, bool debug = false)
    {
        lock (GlobalReadLock)
        {
            foreach (var backend in _backends)
            {
                if (backend.SupportsSettingsWrite)
                {
                    return backend.ApplySettings(changes, debug);
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
