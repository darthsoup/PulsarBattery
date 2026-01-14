using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

public sealed class X2V1Backend : IHidBackend
{
    public string Name => "X2 V1";

    private const int Vid = 0x25A7;
    private const int PidWireless = 0xFA7C;
    private const int PidWired = 0xFA7B;
    private const byte OutputReportId = 0x08;
    private const byte DefaultInputReportId = 0x09;

    private static readonly HashSet<byte> InputReportIds = new([0x08, 0x09]);

    private static readonly byte[] Cmd03Packet = [OutputReportId, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4A];
    private static readonly byte[] Cmd04Packet = [OutputReportId, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49];
    private static readonly byte[] Cmd0EPacket = Convert.FromHexString("080e00000000000000000000000000003f");

    public DeviceBatteryStatus? ReadBatteryStatus(bool debug)
    {
        // Optimize: Use List only when needed, materialize once
        var allDevices = HidHelpers.EnumerateDevices(Vid, d => d.ProductID is PidWireless or PidWired).ToList();

        if (allDevices.Count == 0)
        {
            return null;
        }

        // Optimize: Avoid OrderBy allocation by iterating directly
        foreach (var writerCandidate in allDevices)
        {
            var status = TryReadWithReaders(writerCandidate, allDevices, debug);
            if (status is not null)
            {
                return status;
            }
        }

        return null;
    }

    private DeviceBatteryStatus? TryReadWithReaders(HidDevice writerCandidate, List<HidDevice> readerPool, bool debug)
    {
        var status = TryReadPair(writerCandidate, writerCandidate, debug);
        if (status is not null)
        {
            return status;
        }

        // Optimize: Iterate directly instead of OrderBy which allocates
        foreach (var readerCandidate in readerPool)
        {
            status = TryReadPair(writerCandidate, readerCandidate, debug);
            if (status is not null)
            {
                return status;
            }
        }

        return null;
    }

    private DeviceBatteryStatus? TryReadPair(HidDevice writerDevice, HidDevice readerDevice, bool debug)
    {
        HidStream? writer = null;
        HidStream? reader = null;
        try
        {
            if (!writerDevice.TryOpen(out writer))
            {
                return null;
            }

            if (writerDevice.DevicePath == readerDevice.DevicePath)
            {
                reader = writer;
            }
            else if (!readerDevice.TryOpen(out reader))
            {
                writer.Dispose();
                writer = null;
                return null;
            }

            writer.ReadTimeout = 250;
            writer.WriteTimeout = 500;
            if (reader is not null && !ReferenceEquals(reader, writer))
            {
                reader.ReadTimeout = 250;
            }

            return ReadBatteryCmd04(writer!, reader ?? writer!, debug, transport: writer!.Device.GetMaxFeatureReportLength() > 0 ? "feature" : "output");
        }
        catch
        {
            return null;
        }
        finally
        {
            if (reader is not null && !ReferenceEquals(reader, writer))
            {
                reader.Dispose();
            }

            writer?.Dispose();
        }
    }

    private DeviceBatteryStatus? ReadBatteryCmd04(
        HidStream writer,
        HidStream reader,
        bool debug,
        string transport)
    {
        var maxLen = Math.Max(writer.Device.GetMaxInputReportLength(), reader.Device.GetMaxInputReportLength());
        HidHelpers.DrainInput(reader, 6, maxLen);

        byte[]? Attempt(double timeoutSeconds)
        {
            HidHelpers.SendReport(writer, Cmd04Packet, transport);
            System.Threading.Thread.Sleep(20);
            return ReadCmd(reader, expectedCmd: 0x04, timeoutSeconds, logOther: true, debug: debug, maxLen: maxLen);
        }

        byte[]? payload;
        try
        {
            payload = Attempt(0.8);
        }
        catch
        {
            payload = null;
        }

        if (payload is null)
        {
            foreach (var warmup in BuildWarmupSequence())
            {
                try
                {
                    HidHelpers.SendReport(writer, warmup, transport);
                }
                catch
                {
                    break;
                }

                System.Threading.Thread.Sleep(10);
            }

            try
            {
                payload = Attempt(1.2);
            }
            catch
            {
                payload = null;
            }
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

    private static IEnumerable<byte[]> BuildWarmupSequence()
    {
        yield return BuildCmd01Packet();
        yield return Cmd03Packet;
        yield return Cmd0EPacket;
    }

    private static byte[] BuildCmd01Packet()
    {
        var nonce = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);
        Span<byte> nonceBytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(nonceBytes, nonce);

        Span<byte> body = stackalloc byte[15];
        body[0] = OutputReportId;
        body[1] = 0x01;
        body[2] = 0x00;
        body[3] = 0x00;
        body[4] = 0x00;
        body[5] = 0x08;
        nonceBytes.CopyTo(body[6..10]);
        body[10] = 0x00;
        body[11] = 0x00;
        body[12] = 0x00;
        body[13] = 0x00;
        body[14] = 0x00;

        var sum = 0;
        foreach (var b in body)
        {
            sum += b;
        }

        var chk = (byte)((0x55 - (sum & 0xFF)) & 0xFF);
        var result = new byte[16];
        body.CopyTo(result);
        result[15] = chk;
        return result;
    }

    private static byte[]? ReadCmd(
        HidStream reader,
        byte expectedCmd,
        double timeoutSeconds,
        bool logOther,
        bool debug,
        int maxLen)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        
        // Optimize: Cache current time to avoid repeated DateTime.UtcNow calls in loop condition
        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= deadline)
            {
                break;
            }

            var data = HidHelpers.ReadWithTimeout(reader, maxLen, 250);
            if (data is null || data.Length == 0)
            {
                System.Threading.Thread.Sleep(10);
                continue;
            }

            var payload = NormalizeInputReport(data);
            if (payload.Length < 7 || !InputReportIds.Contains(payload[0]))
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
        // Optimize: Check if data is already byte[] to avoid unnecessary ToArray()
        if (data is byte[] existingArray)
        {
            if (existingArray.Length == 16 && existingArray[0] is 0x01 or 0x02 or 0x03 or 0x04 or 0x08 or 0x0E)
            {
                var result = new byte[17];
                result[0] = DefaultInputReportId;
                Array.Copy(existingArray, 0, result, 1, 16);
                return result;
            }
            return existingArray;
        }

        // Fallback for other collection types
        if (data.Count == 16 && data.First() is 0x01 or 0x02 or 0x03 or 0x04 or 0x08 or 0x0E)
        {
            return new[] { DefaultInputReportId }.Concat(data).ToArray();
        }

        return data.ToArray();
    }
}
