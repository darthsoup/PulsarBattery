using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

public sealed class X2ClBackend : IHidBackend
{
    public string Name => "X2 Crazylight";

    private const int Vid = 0x3710;
    private const int PidWireless = 0x5406;
    private const int PidWired = 0x3414;
    private const byte OutputReportId = 0x08;
    private const byte InputReportId = 0x08;

    private static readonly byte[] Cmd03Packet = [OutputReportId, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4A];
    private static readonly byte[] Cmd04Packet = [OutputReportId, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49];
    private static readonly byte[] Cmd01PacketA = Convert.FromHexString("0801000000088e0c4d4c00000000000011");
    private static readonly byte[] Cmd01PacketB = Convert.FromHexString("0801000000089505dd4b00000000000082");
    private static readonly byte[] Cmd02Packet = Convert.FromHexString("0802000000010100000000000000000049");

    private static readonly IReadOnlyList<byte[]> Cmd04InitSequence = new List<byte[]>
    {
        Cmd01PacketA,
        Cmd03Packet,
        Cmd03Packet,
        Cmd03Packet,
        Cmd01PacketA,
        Cmd03Packet,
        Cmd03Packet,
        Cmd01PacketA,
        Cmd03Packet,
        Cmd01PacketB,
        Cmd03Packet,
        Cmd03Packet,
        Cmd03Packet,
        Cmd02Packet,
        Cmd03Packet,
        Cmd03Packet,
        Cmd04Packet,
        Cmd04Packet,
    };

    public DeviceBatteryStatus? ReadBatteryStatus(bool debug)
    {
        // Optimize: Avoid LINQ allocations - enumerate directly and sort only if needed
        var candidates = HidHelpers.EnumerateDevices(Vid, d => d.ProductID is PidWireless or PidWired);

        foreach (var device in candidates)
        {
            var status = TryReadDevice(device, debug);
            if (status is not null)
            {
                return status;
            }
        }

        return null;
    }

    private DeviceBatteryStatus? TryReadDevice(HidDevice device, bool debug)
    {
        HidStream? writer = null;
        try
        {
            if (!device.TryOpen(out writer))
            {
                return null;
            }

            writer.ReadTimeout = 250;
            writer.WriteTimeout = 500;

            var status = ReadBatteryCmd04(writer, writer, debug, "output");
            return status;
        }
        catch
        {
            return null;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private DeviceBatteryStatus? ReadBatteryCmd04(HidStream writer, HidStream reader, bool debug, string transport)
    {
        HidHelpers.DrainInput(reader, 6, writer.Device.GetMaxInputReportLength());

        byte[]? Attempt(double timeoutSeconds)
        {
            HidHelpers.SendReport(writer, Cmd04Packet, transport);
            return ReadCmd(reader, expectedCmd: 0x04, timeoutSeconds, logOther: true, debug: debug);
        }

        var payload = Attempt(0.8);
        if (payload is null)
        {
            foreach (var init in Cmd04InitSequence)
            {
                HidHelpers.SendReport(writer, init, transport);
                System.Threading.Thread.Sleep(10);
            }

            payload = ReadCmd(reader, 0x04, 2.0, logOther: true, debug: debug);
        }

        if (payload is null)
        {
            return null;
        }

        var parsed = HidHelpers.ParseCmd04Payload(payload);
        if (parsed is null)
        {
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine($"cmd04 parse failed data={Convert.ToHexString(payload)}");
            }

            return null;
        }

        var (battery, charging) = parsed.Value;
        if (debug)
        {
            System.Diagnostics.Debug.WriteLine($"cmd04 raw={battery} charging={charging} data={Convert.ToHexString(payload)}");
        }

        return new DeviceBatteryStatus(battery, charging, Name);
    }

    private static byte[]? ReadCmd(
        HidStream reader,
        byte expectedCmd,
        double timeoutSeconds,
        bool logOther,
        bool debug)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var maxLength = reader.Device.GetMaxInputReportLength();

        // Optimize: Cache current time to avoid repeated DateTime.UtcNow calls
        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= deadline)
            {
                break;
            }

            var data = HidHelpers.ReadWithTimeout(reader, maxLength, 250);
            if (data is null || data.Length == 0)
            {
                continue;
            }

            var payload = NormalizeInputReport(data);
            if (payload.Length < 7 || payload[0] != InputReportId)
            {
                continue;
            }

            if (payload[1] != expectedCmd)
            {
                if (debug && logOther)
                {
                    System.Diagnostics.Debug.WriteLine($"cmd04 skip cmd=0x{payload[1]:X2} data={Convert.ToHexString(payload)}");
                }

                continue;
            }

            return payload;
        }

        return null;
    }

    private static byte[] NormalizeInputReport(IReadOnlyCollection<byte> data)
    {
        // Optimize: Avoid calling ToArray() if data is already a byte array
        if (data is byte[] existingArray)
        {
            if (existingArray.Length == 16)
            {
                var result = new byte[17];
                result[0] = InputReportId;
                Array.Copy(existingArray, 0, result, 1, 16);
                return result;
            }
            return existingArray;
        }

        // Fallback for other collection types
        if (data.Count == 16)
        {
            return new[] { InputReportId }.Concat(data).ToArray();
        }

        return data.ToArray();
    }
}
