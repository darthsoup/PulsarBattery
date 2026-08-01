using System;
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
    private readonly DeviceDescriptor _descriptor;

    // Firmware never changes while the app runs, and a failed Query costs up
    // to ~900ms — read it once and stop retrying after a few misses so the
    // 5s poll loops don't pay that penalty every tick.
    private string? _firmwareVersion;
    private int _firmwareAttemptsLeft = 3;

    public Sonix64Backend(DeviceDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public string Name => _descriptor.Model;

    public DeviceStatus? ReadBatteryStatus(bool debug)
    {
        return WithConfigInterface(debug, (stream, dbg) =>
        {
            var percentage = Sonix64Protocol.ReadBatteryPercent(stream, dbg);
            if (percentage is null)
            {
                return null;
            }

            var connection = Sonix64Protocol.ReadConnectionKind(stream, dbg);
            var charging = connection == ConnectionKind.Wired;
            var connectionName = connection == ConnectionKind.Dongle ? HidHelpers.GetProductName(stream.Device) : null;
            var firmware = ReadFirmwareVersionCached(stream, dbg);

            if (dbg)
            {
                System.Diagnostics.Debug.WriteLine($"sonix64 battery={percentage} charging={charging} conn={connection} via={connectionName ?? "-"} fw={firmware ?? "-"}");
            }

            return new DeviceStatus(percentage.Value, charging, Name, connection, connectionName, firmware);
        });
    }

    public DeviceSettings? ReadSettings(bool debug)
    {
        return WithConfigInterface(debug, (stream, dbg) =>
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
            return settings == new DeviceSettings() ? null : settings;
        });
    }

    public bool SupportsSettingsWrite => true;

    public bool ApplySettings(DeviceSettings changes, bool debug)
    {
        var result = WithConfigInterface(debug, (stream, dbg) =>
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

    private T? WithConfigInterface<T>(bool debug, Func<HidStream, bool, T?> read)
        where T : class
    {
        var candidates = HidHelpers.EnumerateDevices(_descriptor.VendorId, IsCandidate)
            .OrderByDescending(d => d.DevicePath) // mi_03 is the config interface; probe it before mi_02
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
