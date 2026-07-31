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
    private const byte OutputReportId = Legacy17Protocol.OutputReportId;
    private const byte DefaultInputReportId = 0x09;

    private static readonly HashSet<byte> InputReportIds = [0x08, 0x09];

    private static readonly byte[] Cmd03Packet = Legacy17Protocol.BuildPacket(OutputReportId, 0x03);
    private static readonly byte[] Cmd04Packet = Legacy17Protocol.BuildPacket(OutputReportId, Legacy17Protocol.CmdBattery);
    private static readonly byte[] Cmd0EPacket = Legacy17Protocol.BuildPacket(OutputReportId, 0x0E);

    public DeviceStatus? ReadBatteryStatus(bool debug)
    {
        var allDevices = HidHelpers.EnumerateDevices(Vid, d => d.ProductID is PidWireless or PidWired).ToList();

        if (allDevices.Count == 0)
        {
            return null;
        }

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

    private DeviceStatus? TryReadWithReaders(HidDevice writerCandidate, List<HidDevice> readerPool, bool debug)
    {
        var status = TryReadPair(writerCandidate, writerCandidate, debug);
        if (status is not null)
        {
            return status;
        }

        foreach (var readerCandidate in readerPool.OrderBy(r => r.DevicePath))
        {
            status = TryReadPair(writerCandidate, readerCandidate, debug);
            if (status is not null)
            {
                return status;
            }
        }

        return null;
    }

    private DeviceStatus? TryReadPair(HidDevice writerDevice, HidDevice readerDevice, bool debug)
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

            var connection = writerDevice.ProductID == PidWired ? ConnectionKind.Wired : ConnectionKind.Dongle;
            var connectionName = connection == ConnectionKind.Dongle ? HidHelpers.GetProductName(writerDevice) : null;
            var transport = writer!.Device.GetMaxFeatureReportLength() > 0 ? "feature" : "output";
            return ReadBatteryCmd04(writer!, reader ?? writer!, debug, transport, connection, connectionName);
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

    private DeviceStatus? ReadBatteryCmd04(
        HidStream writer,
        HidStream reader,
        bool debug,
        string transport,
        ConnectionKind connection,
        string? connectionName)
    {
        var maxLen = Math.Max(writer.Device.GetMaxInputReportLength(), reader.Device.GetMaxInputReportLength());
        HidHelpers.DrainInput(reader, 6, maxLen);

        byte[]? Attempt(double timeoutSeconds)
        {
            HidHelpers.SendReport(writer, Cmd04Packet, transport);
            System.Threading.Thread.Sleep(20);
            return ReadCmd04Response(reader, timeoutSeconds, debug, maxLen);
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

        var parsed = Legacy17Protocol.ParseBatteryPayload(payload);
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

        return new DeviceStatus(battery, charging, Name, connection, connectionName);
    }

    private static byte[]? ReadCmd04Response(HidStream reader, double timeoutSeconds, bool debug, int maxLen)
    {
        return Legacy17Protocol.ReadResponse(
            reader,
            Legacy17Protocol.CmdBattery,
            timeoutSeconds,
            InputReportIds,
            normalizeReportId: DefaultInputReportId,
            bareReportFilter: static b => b is 0x01 or 0x02 or 0x03 or 0x04 or 0x08 or 0x0E,
            debug,
            maxLen,
            idleSleepMs: 10);
    }

    private static IEnumerable<byte[]> BuildWarmupSequence()
    {
        yield return BuildCmd01Packet();
        yield return Cmd03Packet;
        yield return Cmd0EPacket;
    }

    private static byte[] BuildCmd01Packet()
    {
        // Deliberately 16 bytes (not 17 like the other packets) — this
        // preserves the byte-exact captured warmup packet.
        var nonce = (uint)(DateTime.UtcNow.Ticks & 0xFFFFFFFF);
        Span<byte> body = stackalloc byte[15];
        body[0] = OutputReportId;
        body[1] = 0x01;
        body[5] = 0x08;
        BitConverter.TryWriteBytes(body[6..10], nonce);

        var result = new byte[16];
        body.CopyTo(result);
        result[15] = Legacy17Protocol.Checksum(body);
        return result;
    }
}
