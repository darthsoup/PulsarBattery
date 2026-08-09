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
        string? DongleFirmwareVersion = null,
        int? ProtocolModelId = null);

    private static readonly object GlobalReadLock = new();

    private readonly IReadOnlyList<IHidBackend> _backends = DeviceRegistry.CreateBackends();
    private IHidBackend? _activeBackend;

    public BatteryStatus? ReadBatteryStatus(bool debug = false)
    {
        lock (GlobalReadLock)
        {
            foreach (var backend in _backends)
            {
                var status = backend.ReadBatteryStatus(debug);
                if (status is not null)
                {
                    _activeBackend = backend;
                    return new BatteryStatus(status.Percentage, status.IsCharging, status.Model, status.Connection, status.ConnectionName, status.FirmwareVersion, status.LinkRateHz, status.VoltageMv, status.SignalStrength, status.DongleFirmwareVersion, status.ProtocolModelId);
                }
            }

            _activeBackend = null;
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
            // Status, settings and writes must stay on the same backend. Shared
            // dongle PIDs make an independent rescan unsafe when two Pulsar
            // devices are connected.
            if (_activeBackend is not null)
            {
                return _activeBackend.SupportsSettingsWrite
                    ? _activeBackend.ApplySettings(changes, debug)
                    : null;
            }

            foreach (var backend in _backends)
            {
                if (backend.ReadBatteryStatus(debug) is null)
                {
                    continue;
                }

                _activeBackend = backend;
                return backend.SupportsSettingsWrite
                    ? backend.ApplySettings(changes, debug)
                    : null;
            }

            return null;
        }
    }

    public DeviceSettings? ReadDeviceSettings(bool debug = false)
        => ReadDeviceSettingsSnapshot(debug)?.Values;

    public DeviceSettingsSnapshot? ReadDeviceSettingsSnapshot(bool debug = false)
    {
        lock (GlobalReadLock)
        {
            if (_activeBackend is not null)
            {
                return _activeBackend.ReadSettingsSnapshot(debug);
            }

            foreach (var backend in _backends)
            {
                var snapshot = backend.ReadSettingsSnapshot(debug);
                if (snapshot is not null)
                {
                    _activeBackend = backend;
                    return snapshot;
                }
            }

            return null;
        }
    }
}
