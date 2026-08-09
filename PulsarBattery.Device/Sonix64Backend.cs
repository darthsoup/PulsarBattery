using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

/// <summary>
/// Generic backend for any device speaking the Sonix 64-byte protocol.
/// Which VID/PIDs it matches comes from the <see cref="DeviceDescriptor"/>,
/// so supporting another same-protocol mouse is a registry entry, not code.
/// </summary>
public sealed class Sonix64Backend : IHidBackend
{
    private static readonly IReadOnlyList<int> PollingRates =
        Sonix64Protocol.SupportedPollingRates.OrderBy(rate => rate).ToArray();
    private static readonly IReadOnlyList<int> LodValues = [7, 10, 20];
    private static readonly IReadOnlyList<DeviceValueRange> DpiRanges =
        [new DeviceValueRange(50, 26_000, 1)];

    private readonly DeviceDescriptor _descriptor;

    // Firmware never changes while the app runs, and a failed Query costs up
    // to ~900ms — read it once and stop retrying after a few misses so the
    // 5s poll loops don't pay that penalty every tick.
    private string? _firmwareVersion;
    private string? _firmwareDevicePath;
    private string? _lastDevicePath;
    private int _firmwareAttemptsLeft = 3;

    public Sonix64Backend(DeviceDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public string Name => _descriptor.Model;

    public DeviceStatus? ReadBatteryStatus(bool debug)
    {
        return WithConfigInterface(debug, allowDeviceSwitch: true, (stream, dbg) =>
        {
            var percentage = Sonix64Protocol.ReadBatteryPercent(stream, dbg);
            if (percentage is null)
            {
                return null;
            }

            var (connection, connRateHz) = Sonix64Protocol.ReadConnection(stream, dbg);
            // The live register tracks on-mouse rate switching; the connection
            // register only knows the rate the link was established with.
            var linkRateHz = Sonix64Protocol.ReadLivePollingRateHz(stream, dbg) ?? connRateHz;
            // The charge register is the source of truth when wired (a full
            // battery on the cable is not charging); fall back to the old
            // "cable = charging" heuristic if it doesn't answer.
            var charging = connection == ConnectionKind.Wired
                && (Sonix64Protocol.ReadChargingState(stream, dbg) ?? true);
            var connectionName = connection == ConnectionKind.Dongle ? HidHelpers.GetProductName(stream.Device) : null;
            var firmware = ReadFirmwareVersionCached(stream, dbg);

            if (dbg)
            {
                System.Diagnostics.Debug.WriteLine($"sonix64 battery={percentage} charging={charging} conn={connection}@{linkRateHz?.ToString() ?? "?"}Hz via={connectionName ?? "-"} fw={firmware ?? "-"}");
            }

            return new DeviceStatus(percentage.Value, charging, Name, connection, connectionName, firmware, linkRateHz);
        });
    }

    public DeviceSettings? ReadSettings(bool debug) => ReadSettingsSnapshot(debug)?.Values;

    public DeviceSettingsSnapshot? ReadSettingsSnapshot(bool debug)
    {
        return WithConfigInterface(debug, allowDeviceSwitch: false, (stream, dbg) =>
        {
            var settings = new DeviceSettings(
                PollingRateHz: Sonix64Protocol.ReadPollingRateHz(stream, dbg),
                DebounceMs: Sonix64Protocol.ReadDebounceMs(stream, dbg),
                MotionSync: Sonix64Protocol.ReadMotionSync(stream, dbg),
                Dpi: Sonix64Protocol.ReadDpi(stream, dbg),
                DpiStage: Sonix64Protocol.ReadDpiStage(stream, dbg),
                LodMm10: Sonix64Protocol.ReadLodMm10(stream, dbg),
                AngleSnap: Sonix64Protocol.ReadAngleSnap(stream, dbg),
                RippleControl: Sonix64Protocol.ReadRippleControl(stream, dbg));

            // All-null means the device never answered; treat as not found.
            if (settings == new DeviceSettings())
            {
                return null;
            }

            var fields = DeviceSettingField.None;
            if (settings.PollingRateHz is not null) fields |= DeviceSettingField.PollingRate;
            if (settings.DebounceMs is not null) fields |= DeviceSettingField.Debounce;
            if (settings.MotionSync is not null) fields |= DeviceSettingField.MotionSync;
            if (settings.Dpi is not null) fields |= DeviceSettingField.Dpi;
            if (settings.DpiStage is not null) fields |= DeviceSettingField.DpiStage;
            if (settings.LodMm10 is not null) fields |= DeviceSettingField.Lod;
            if (settings.AngleSnap is not null) fields |= DeviceSettingField.AngleSnap;
            if (settings.RippleControl is not null) fields |= DeviceSettingField.RippleControl;

            return new DeviceSettingsSnapshot(
                settings,
                new DeviceSettingsCapabilities(
                    fields,
                    fields,
                    PollingRates,
                    LodValues,
                    DpiRanges,
                    DpiStageCount: 8,
                    DebounceMinimumMs: 0,
                    DebounceMaximumMs: 30,
                    SleepValuesSeconds: [],
                    WriteTrust: DeviceSettingsWriteTrust.HardwareVerified));
        });
    }

    public bool SupportsSettingsWrite => true;

    public bool ApplySettings(DeviceSettings changes, bool debug)
    {
        if (_lastDevicePath is null || changes.SleepSeconds is not null)
        {
            return false;
        }

        var result = WithConfigInterface(debug, allowDeviceSwitch: false, (stream, dbg) =>
        {
            var allApplied = true;

            if (changes.MotionSync is bool motionSync)
            {
                allApplied &= Sonix64Protocol.WriteMotionSync(stream, motionSync, dbg);
            }

            if (changes.AngleSnap is bool angleSnap)
            {
                allApplied &= Sonix64Protocol.WriteAngleSnap(stream, angleSnap, dbg);
            }

            if (changes.RippleControl is bool ripple)
            {
                allApplied &= Sonix64Protocol.WriteRippleControl(stream, ripple, dbg);
            }

            if (changes.DebounceMs is int debounce)
            {
                allApplied &= Sonix64Protocol.WriteDebounceMs(stream, debounce, dbg);
            }

            if (changes.LodMm10 is int lod)
            {
                allApplied &= Sonix64Protocol.WriteLodMm10(stream, lod, dbg);
            }

            if (changes.DpiStage is int stage)
            {
                allApplied &= Sonix64Protocol.WriteDpiStage(stream, stage, dbg);
            }

            if (changes.Dpi is int dpi)
            {
                allApplied &= Sonix64Protocol.WriteDpi(stream, dpi, dbg);
            }

            if (changes.PollingRateHz is int pollingRate)
            {
                // Accepted-but-deferred is possible here; the caller re-reads
                // settings afterwards and surfaces any mismatch.
                allApplied &= Sonix64Protocol.WritePollingRateHz(stream, pollingRate, dbg);
            }

            // Box the bool so the generic null-on-failure contract holds.
            return (object)allApplied;
        });

        return result is true;
    }

    private string? ReadFirmwareVersionCached(HidStream stream, bool debug)
    {
        if (!string.Equals(
                _firmwareDevicePath,
                stream.Device.DevicePath,
                StringComparison.OrdinalIgnoreCase))
        {
            _firmwareDevicePath = stream.Device.DevicePath;
            _firmwareVersion = null;
            _firmwareAttemptsLeft = 3;
        }

        if (_firmwareVersion is null && _firmwareAttemptsLeft > 0)
        {
            _firmwareVersion = Sonix64Protocol.ReadFirmwareVersion(stream, debug);
            if (_firmwareVersion is null)
            {
                _firmwareAttemptsLeft--;
            }
        }

        return _firmwareVersion;
    }

    private T? WithConfigInterface<T>(
        bool debug,
        bool allowDeviceSwitch,
        Func<HidStream, bool, T?> read)
        where T : class
    {
        var candidates = HidHelpers.EnumerateDevices(_descriptor.VendorId, IsCandidate)
            .Where(device => allowDeviceSwitch
                             || _lastDevicePath is null
                             || string.Equals(
                                 device.DevicePath,
                                 _lastDevicePath,
                                 StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(device => string.Equals(
                device.DevicePath,
                _lastDevicePath,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => d.DevicePath) // mi_03 is the config interface; probe it before mi_02
            .ToList();

        foreach (var device in candidates)
        {
            HidStream? stream = null;
            try
            {
                if (!device.TryOpen(out stream))
                {
                    continue;
                }

                stream.ReadTimeout = 500;
                stream.WriteTimeout = 500;

                var result = read(stream, debug);
                if (result is not null)
                {
                    _lastDevicePath = device.DevicePath;
                    return result;
                }
            }
            catch
            {
                // mi_02 rejects SetFeature; other interfaces may be busy — try the next one.
            }
            finally
            {
                stream?.Dispose();
            }
        }

        return null;
    }

    private bool IsCandidate(HidDevice device)
    {
        if (!_descriptor.ProductIds.Contains(device.ProductID))
        {
            return false;
        }

        try
        {
            return device.GetMaxFeatureReportLength() >= Sonix64Protocol.PacketLength + 1;
        }
        catch
        {
            return false;
        }
    }
}
