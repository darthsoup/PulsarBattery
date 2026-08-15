using PulsarBattery.Device;
using PulsarBattery.Tools;
using System;
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
                try
                {
                    var status = backend.ReadBatteryStatus(debug);
                    if (status is not null)
                    {
                        _activeBackend = backend;
                        return new BatteryStatus(status.Percentage, status.IsCharging, status.Model, status.Connection, status.ConnectionName, status.FirmwareVersion, status.LinkRateHz, status.VoltageMv, status.SignalStrength, status.DongleFirmwareVersion, status.ProtocolModelId);
                    }
                }
                catch (Exception ex)
                {
                    LogBackendFailure(backend, "battery read", ex);
                }
            }

            _activeBackend = null;
            return null;
        }
    }

    /// <summary>
    /// Applies non-null fields to the first write-capable backend. Null when no writable device is
    /// present, else whether every change was applied and confirmed.
    /// </summary>
    public bool? ApplyDeviceSettings(DeviceSettings changes, bool debug = false)
    {
        lock (GlobalReadLock)
        {
            // Status, settings and writes must stay on one backend: shared dongle PIDs make an
            // independent rescan unsafe when two Pulsar devices are connected.
            if (_activeBackend is not null)
            {
                try
                {
                    return _activeBackend.SupportsSettingsWrite
                        ? _activeBackend.ApplySettings(changes, debug)
                        : null;
                }
                catch (Exception ex)
                {
                    LogBackendFailure(_activeBackend, "settings write", ex);
                    return false;
                }
            }

            foreach (var backend in _backends)
            {
                DeviceStatus? status;
                try
                {
                    status = backend.ReadBatteryStatus(debug);
                }
                catch (Exception ex)
                {
                    LogBackendFailure(backend, "settings discovery", ex);
                    continue;
                }

                if (status is null)
                {
                    continue;
                }

                _activeBackend = backend;
                try
                {
                    return backend.SupportsSettingsWrite
                        ? backend.ApplySettings(changes, debug)
                        : null;
                }
                catch (Exception ex)
                {
                    LogBackendFailure(backend, "settings write", ex);
                    return false;
                }
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
                try
                {
                    return _activeBackend.ReadSettingsSnapshot(debug);
                }
                catch (Exception ex)
                {
                    LogBackendFailure(_activeBackend, "settings read", ex);
                    return null;
                }
            }

            foreach (var backend in _backends)
            {
                try
                {
                    var snapshot = backend.ReadSettingsSnapshot(debug);
                    if (snapshot is not null)
                    {
                        _activeBackend = backend;
                        return snapshot;
                    }
                }
                catch (Exception ex)
                {
                    LogBackendFailure(backend, "settings discovery/read", ex);
                }
            }

            return null;
        }
    }

    private static void LogBackendFailure(IHidBackend backend, string operation, Exception exception)
    {
        Log.Error(
            nameof(PulsarBatteryReader),
            $"{operation} failed in {backend.GetType().Name}: {exception}");
    }
}
