using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

/// <summary>
/// Backend for any device speaking the Sonix 64-byte protocol. VID/PIDs come from the
/// <see cref="DeviceDescriptor"/>, so another same-protocol mouse is a registry entry, not code.
/// </summary>
public sealed class Sonix64Backend : IHidBackend
{
    private static readonly IReadOnlyList<int> PollingRates =
        Sonix64Protocol.SupportedPollingRates.OrderBy(rate => rate).ToArray();
    private static readonly IReadOnlyList<int> LodValues = [7, 10, 20];
    private static readonly IReadOnlyList<DeviceValueRange> DpiRanges =
        [new DeviceValueRange(50, 26_000, 1)];

    private readonly DeviceDescriptor _descriptor;

    // Firmware never changes while the app runs and a failed Query costs ~900ms, so read it once and
    // stop retrying after a few misses rather than paying that on every 5s poll tick.
    private string? _firmwareVersion;
    private string? _firmwareDevicePath;
    private string? _lastDevicePath;
    private string? _settingsDevicePath;
    private DeviceSettingField _writableFields;
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

            // Which device answered is ground truth for wired-vs-dongle. The connection register reported
            // "Dongle @ 4000Hz" throughout on a genuinely wired mouse with no dongle on the bus.
            var connection = _descriptor.DongleProductIds.Contains(stream.Device.ProductID)
                ? ConnectionKind.Dongle
                : ConnectionKind.Wired;
            var connRateHz = Sonix64Protocol.ReadConnection(stream, dbg);
            // The live register tracks on-mouse rate switching; the connection
            // register only knows the rate the link was established with.
            var linkRateHz = Sonix64Protocol.ReadLivePollingRateHz(stream, dbg) ?? connRateHz;
            // Register 08 82 01 is not "actively charging": b6 stayed 0 while the percentage climbed.
            // Cable-attached is the reliable signal.
            var charging = connection == ConnectionKind.Wired;
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
        _settingsDevicePath = null;
        _writableFields = DeviceSettingField.None;
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

            var fields = GetPresentFields(settings);
            var writableFields = GetWritableFields(settings);
            _settingsDevicePath = stream.Device.DevicePath;
            _writableFields = writableFields;

            return new DeviceSettingsSnapshot(
                settings,
                new DeviceSettingsCapabilities(
                    Readable: fields,
                    Writable: writableFields,
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

    public bool SupportsSettingsWrite => _writableFields != DeviceSettingField.None
        && string.Equals(_settingsDevicePath, _lastDevicePath, StringComparison.OrdinalIgnoreCase);

    public bool ApplySettings(DeviceSettings changes, bool debug)
    {
        if (_lastDevicePath is null || !SupportsSettingsWrite || !ValidateChanges(changes, _writableFields))
        {
            return false;
        }

        var result = WithConfigInterface(debug, allowDeviceSwitch: false, (stream, dbg) =>
        {
            if (changes.MotionSync is bool motionSync && !Sonix64Protocol.WriteMotionSync(stream, motionSync, dbg))
            {
                return (object)false;
            }

            if (changes.AngleSnap is bool angleSnap && !Sonix64Protocol.WriteAngleSnap(stream, angleSnap, dbg))
            {
                return (object)false;
            }

            if (changes.RippleControl is bool ripple && !Sonix64Protocol.WriteRippleControl(stream, ripple, dbg))
            {
                return (object)false;
            }

            if (changes.DebounceMs is int debounce && !Sonix64Protocol.WriteDebounceMs(stream, debounce, dbg))
            {
                return (object)false;
            }

            if (changes.LodMm10 is int lod && !Sonix64Protocol.WriteLodMm10(stream, lod, dbg))
            {
                return (object)false;
            }

            // A failed stage switch must stop the batch before DPI can target the previous stage.
            if (changes.DpiStage is int stage && !Sonix64Protocol.WriteDpiStage(stream, stage, dbg))
            {
                return (object)false;
            }

            if (changes.Dpi is int dpi && !Sonix64Protocol.WriteDpi(stream, dpi, dbg))
            {
                return (object)false;
            }

            if (changes.PollingRateHz is int pollingRate && !Sonix64Protocol.WritePollingRateHz(stream, pollingRate, dbg))
            {
                return (object)false;
            }

            return (object)true;
        });

        return result is true;
    }

    internal static bool ValidateChanges(DeviceSettings changes, DeviceSettingField writableFields)
    {
        var requestedFields = GetPresentFields(changes);
        return (requestedFields & writableFields) == requestedFields
            && changes.SleepSeconds is null
            && (changes.PollingRateHz is not int polling || PollingRates.Contains(polling))
            && (changes.DebounceMs is not int debounce || debounce is >= 0 and <= 30)
            && (changes.Dpi is not int dpi || DpiRanges.Any(range => range.Contains(dpi)))
            && (changes.DpiStage is not int stage || stage is >= 1 and <= 8)
            && (changes.LodMm10 is not int lod || LodValues.Contains(lod));
    }

    internal static DeviceSettingField GetWritableFields(DeviceSettings settings)
    {
        var fields = GetPresentFields(settings) & ~DeviceSettingField.Sleep;
        if (settings.PollingRateHz is int polling && !PollingRates.Contains(polling)) fields &= ~DeviceSettingField.PollingRate;
        if (settings.DebounceMs is int debounce && debounce is < 0 or > 30) fields &= ~DeviceSettingField.Debounce;
        if (settings.Dpi is int dpi && !DpiRanges.Any(range => range.Contains(dpi))) fields &= ~DeviceSettingField.Dpi;
        if (settings.DpiStage is int stage && stage is < 1 or > 8) fields &= ~DeviceSettingField.DpiStage;
        if (settings.LodMm10 is int lod && !LodValues.Contains(lod)) fields &= ~DeviceSettingField.Lod;
        return fields;
    }

    private static DeviceSettingField GetPresentFields(DeviceSettings settings)
    {
        var fields = DeviceSettingField.None;
        if (settings.PollingRateHz is not null) fields |= DeviceSettingField.PollingRate;
        if (settings.DebounceMs is not null) fields |= DeviceSettingField.Debounce;
        if (settings.MotionSync is not null) fields |= DeviceSettingField.MotionSync;
        if (settings.Dpi is not null) fields |= DeviceSettingField.Dpi;
        if (settings.DpiStage is not null) fields |= DeviceSettingField.DpiStage;
        if (settings.LodMm10 is not null) fields |= DeviceSettingField.Lod;
        if (settings.AngleSnap is not null) fields |= DeviceSettingField.AngleSnap;
        if (settings.RippleControl is not null) fields |= DeviceSettingField.RippleControl;
        if (settings.SleepSeconds is not null) fields |= DeviceSettingField.Sleep;
        return fields;
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
                // mi_02 rejects SetFeature; other interfaces may be busy, so try the next one.
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
