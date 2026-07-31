using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

internal static class HidHelpers
{
    public static byte[]? ReadWithTimeout(HidStream stream, int maxLength, int timeoutMs)
    {
        var buffer = new byte[maxLength];
        stream.ReadTimeout = timeoutMs;
        try
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null;
            }

            return buffer.Take(read).ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static void DrainInput(HidStream stream, int attempts, int maxLength)
    {
        var originalTimeout = stream.ReadTimeout;
        try
        {
            stream.ReadTimeout = 1;
            for (var i = 0; i < attempts; i++)
            {
                var buffer = new byte[maxLength];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                stream.ReadTimeout = originalTimeout;
            }
            catch
            {
            }
        }
    }

    public static void SendReport(HidStream stream, IReadOnlyList<byte> payload, string transport)
    {
        var useFeature = transport == "feature" || (transport == "auto" && stream.Device.GetMaxFeatureReportLength() > 0);
        if (useFeature)
        {
            stream.SetFeature(payload.ToArray());
            return;
        }

        stream.WriteTimeout = 500;
        stream.Write(payload.ToArray());
    }

    public static IEnumerable<HidDevice> EnumerateDevices(int vendorId, Func<HidDevice, bool>? filter = null)
    {
        var devices = DeviceList.Local.GetHidDevices(vendorId);
        return filter is null ? devices : devices.Where(filter);
    }
}
