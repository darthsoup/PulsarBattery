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
        string? ConnectionName = null,
        string? FirmwareVersion = null,
        int? LinkRateHz = null,
        int? VoltageMv = null,
        int? SignalStrength = null,
        string? DongleFirmwareVersion = null);

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
                    return new BatteryStatus(status.Percentage, status.IsCharging, status.Model, status.Connection, status.ConnectionName, status.FirmwareVersion, status.LinkRateHz, status.VoltageMv, status.SignalStrength, status.DongleFirmwareVersion);
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
            // Route to the backend whose device actually answered, not merely
            // the first one in the registry that can write: with only a
            // read-only device attached (e.g. an X2 V1) the old scan handed
            // every write to the Sonix backend, which then failed against
            // hardware that was not even present.
            foreach (var backend in _backends)
            {
                if (backend.ReadBatteryStatus(debug) is null)
                {
                    continue;
                }

                return backend.SupportsSettingsWrite
                    ? backend.ApplySettings(changes, debug)
                    : null;
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
