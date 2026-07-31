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

            if (dbg)
            {
                System.Diagnostics.Debug.WriteLine($"sonix64 battery={percentage} charging={charging} conn={connection}");
            }

            return new DeviceStatus(percentage.Value, charging, Name, connection);
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
                DpiStage: Sonix64Protocol.ReadDpiStage(stream, dbg));

            // All-null means the device never answered; treat as not found.
            return settings == new DeviceSettings() ? null : settings;
        });
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
