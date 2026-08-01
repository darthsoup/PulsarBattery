using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

public sealed class X2ClBackend : IHidBackend
{
    public string Name => "X2 CrazyLight";

    private const int Vid = 0x3710;
    private const int PidWireless = 0x5406;
    private const int PidWired = 0x3414;
    private const byte ReportId = Legacy17Protocol.OutputReportId;

    private static readonly HashSet<byte> InputReportIds = [ReportId];

    private static readonly byte[] Cmd03Packet = Legacy17Protocol.BuildPacket(ReportId, 0x03);
    private static readonly byte[] Cmd04Packet = Legacy17Protocol.BuildPacket(ReportId, Legacy17Protocol.CmdBattery);
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

    public DeviceStatus? ReadBatteryStatus(bool debug)
    {
        var candidates = HidHelpers.EnumerateDevices(Vid, d => d.ProductID is PidWireless or PidWired)
            .OrderBy(d => d.DevicePath)
            .ToList();

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

    private DeviceStatus? TryReadDevice(HidDevice device, bool debug)
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

            var connection = device.ProductID == PidWired ? ConnectionKind.Wired : ConnectionKind.Dongle;
            var connectionName = connection == ConnectionKind.Dongle ? HidHelpers.GetProductName(device) : null;
            // No known firmware command in the legacy protocol; wired bcdDevice
            // is the mouse's own version, the dongle's would be the dongle's.
            var firmware = connection == ConnectionKind.Wired ? HidHelpers.GetFirmwareFromBcd(device) : null;
            return ReadBatteryCmd04(writer, writer, debug, "output", connection, connectionName, firmware);
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

    private DeviceStatus? ReadBatteryCmd04(HidStream writer, HidStream reader, bool debug, string transport, ConnectionKind connection, string? connectionName, string? firmware)
    {
        var maxLength = writer.Device.GetMaxInputReportLength();
        HidHelpers.DrainInput(reader, 6, maxLength);

        byte[]? Attempt(double timeoutSeconds)
        {
            HidHelpers.SendReport(writer, Cmd04Packet, transport);
            return ReadCmd04Response(reader, timeoutSeconds, debug, maxLength);
        }

        var payload = Attempt(0.8);
        if (payload is null)
        {
            foreach (var init in Cmd04InitSequence)
            {
                HidHelpers.SendReport(writer, init, transport);
                System.Threading.Thread.Sleep(10);
            }

            payload = ReadCmd04Response(reader, 2.0, debug, maxLength);
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

        return new DeviceStatus(battery, charging, Name, connection, connectionName, firmware);
    }

    private static byte[]? ReadCmd04Response(HidStream reader, double timeoutSeconds, bool debug, int maxLength)
    {
        return Legacy17Protocol.ReadResponse(
            reader,
            Legacy17Protocol.CmdBattery,
            timeoutSeconds,
            InputReportIds,
            normalizeReportId: ReportId,
            bareReportFilter: null,
            debug,
            maxLength);
    }
}
