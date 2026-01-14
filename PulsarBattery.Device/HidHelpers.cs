using HidSharp;
using System.Buffers;

namespace PulsarBattery.Device;

internal static class HidHelpers
{
    public static (int battery, bool charging)? ParseCmd04Payload(IReadOnlyList<byte> payload)
    {
        if (payload.Count < 8)
        {
            return null;
        }

        var battery = payload[6];
        var charging = payload[7] != 0x00;
        return (battery, charging);
    }

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

            // Optimize: Use Span-based approach or Array.Resize instead of LINQ Take().ToArray()
            // to avoid extra allocations
            if (read == maxLength)
            {
                return buffer;
            }

            var result = new byte[read];
            Array.Copy(buffer, 0, result, 0, read);
            return result;
        }
        catch
        {
            return null;
        }
    }

    public static void DrainInput(HidStream stream, int attempts, int maxLength)
    {
        var originalTimeout = stream.ReadTimeout;
        // Optimize: Use ArrayPool to avoid allocating buffers repeatedly
        byte[]? buffer = null;
        try
        {
            stream.ReadTimeout = 1;
            buffer = ArrayPool<byte>.Shared.Rent(maxLength);

            for (var i = 0; i < attempts; i++)
            {
                var read = stream.Read(buffer, 0, maxLength);
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
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

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

        // Optimize: Avoid allocating a new array if payload is already a byte[]
        byte[] payloadArray;
        if (payload is byte[] existingArray)
        {
            payloadArray = existingArray;
        }
        else
        {
            payloadArray = payload.ToArray();
        }

        if (useFeature)
        {
            stream.SetFeature(payloadArray);
            return;
        }

        stream.WriteTimeout = 500;
        stream.Write(payloadArray);
    }

    public static IEnumerable<HidDevice> EnumerateDevices(int vendorId, Func<HidDevice, bool>? filter = null)
    {
        var devices = DeviceList.Local.GetHidDevices(vendorId);
        return filter is null ? devices : devices.Where(filter);
    }
}
