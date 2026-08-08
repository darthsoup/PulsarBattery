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
    private static readonly byte[] DriverStatusPacket = Legacy17Protocol.BuildPacket(ReportId, Legacy17Protocol.CmdDriverStatus, [0x00, 0x00, 0x00, 0x00, 0x01]);
    private static readonly byte[] VersionPacket = Legacy17Protocol.BuildPacket(ReportId, Legacy17Protocol.CmdVersion);
    private static readonly byte[] DongleVersionPacket = Legacy17Protocol.BuildPacket(ReportId, Legacy17Protocol.CmdDongleVersion);
    private static readonly byte[] RssiPacket = Legacy17Protocol.BuildPacket(ReportId, Legacy17Protocol.CmdRssi);

    // Firmware does not change while the device stays connected, and this read
    // sits on the same path the 5-second BatteryMonitor loop uses — so answers
    // are kept rather than re-fetched on every poll.
    private string? _cachedFirmware;
    private string? _cachedDongleFirmware;
    private static readonly byte[] Cmd01PacketA = Convert.FromHexString("0801000000088e0c4d4c00000000000011");
    private static readonly byte[] Cmd01PacketB = Convert.FromHexString("0801000000089505dd4b00000000000082");
    private static readonly byte[] Cmd02Packet = Convert.FromHexString("0802000000010100000000000000000049");

    /// <summary>
    /// The captured opening exchange, replayed verbatim when the documented
    /// handshake does not yield a battery frame. Decoded against the Pulsar
    /// cMouse notes it is: identify (cmd 1) / online polls (cmd 3) /
    /// driver-status (cmd 2) / battery (cmd 4). Kept as a fallback because
    /// this is the path that is known to work on real hardware.
    /// </summary>
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

            var maxLength = writer.Device.GetMaxInputReportLength();

            // The device's own handshake beats guessing the transport from the
            // PID, and it is the only source of the link rate on this protocol.
            var handshake = OpenSession(writer, debug, maxLength);
            var connection = handshake?.Kind
                ?? (device.ProductID == PidWired ? ConnectionKind.Wired : ConnectionKind.Dongle);
            var linkRateHz = handshake?.LinkRateHz;

            var connectionName = connection == ConnectionKind.Dongle ? HidHelpers.GetProductName(device) : null;

            // CmdVersion answers over the dongle too; the wired bcdDevice is
            // only a fallback for when the handshake path found nothing.
            _cachedFirmware ??= ReadVersion(writer, VersionPacket, Legacy17Protocol.CmdVersion, debug, maxLength);
            var firmware = _cachedFirmware
                ?? (connection == ConnectionKind.Wired ? HidHelpers.GetFirmwareFromBcd(device) : null);

            // Radio-only values. Reading them on a cable would just cost a
            // timeout for a value that cannot exist.
            int? signal = null;
            string? dongleFirmware = null;
            if (connection == ConnectionKind.Dongle && handshake?.IsOnline == true)
            {
                signal = ReadSignal(writer, debug, maxLength);
                _cachedDongleFirmware ??= ReadVersion(writer, DongleVersionPacket, Legacy17Protocol.CmdDongleVersion, debug, maxLength);
                dongleFirmware = _cachedDongleFirmware;
            }

            return ReadBatteryCmd04(writer, writer, debug, "output", connection, connectionName, firmware, linkRateHz, signal, dongleFirmware);
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

    /// <summary>
    /// Runs the documented opening exchange — identify, announce the driver,
    /// then wait for the mouse itself to be reachable — and returns what the
    /// handshake said about the link. Null when the device did not answer, in
    /// which case the caller falls back to <see cref="Cmd04InitSequence"/>.
    /// </summary>
    private static (ConnectionKind Kind, int LinkRateHz, bool IsOnline)? OpenSession(HidStream writer, bool debug, int maxLength)
    {
        try
        {
            HidHelpers.DrainInput(writer, 4, maxLength);

            Span<byte> challenge = stackalloc byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(challenge);

            HidHelpers.SendReport(writer, Legacy17Protocol.BuildInfoPacket(ReportId, challenge), "output");
            var info = Legacy17Protocol.ReadResponse(
                writer, Legacy17Protocol.CmdInfo, 0.8, InputReportIds, ReportId, null, debug, maxLength);
            if (info is null)
            {
                return null;
            }

            var parsed = Legacy17Protocol.ParseInfoPayload(info, challenge);
            if (parsed is null)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine($"x2cl handshake parse failed data={Convert.ToHexString(info)}");
                }

                return null;
            }

            HidHelpers.SendReport(writer, DriverStatusPacket, "output");
            Legacy17Protocol.ReadResponse(
                writer, Legacy17Protocol.CmdDriverStatus, 0.4, InputReportIds, ReportId, null, debug, maxLength);

            var online = Legacy17Protocol.WaitUntilOnline(writer, writer, ReportId, "output", InputReportIds, 1.0, debug, maxLength);

            var decoded = Legacy17Protocol.DecodeConnection(parsed.Value.ConnectionCode);
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine($"x2cl handshake model=0x{parsed.Value.ModelId:X4} conn={parsed.Value.ConnectionCode} dongleType={parsed.Value.DongleType} online={online} -> {decoded?.Kind} {decoded?.LinkRateHz}");
            }

            return decoded is null ? null : (decoded.Value.Kind, decoded.Value.LinkRateHz, online);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadVersion(HidStream writer, byte[] packet, byte expectedCmd, bool debug, int maxLength)
    {
        try
        {
            HidHelpers.SendReport(writer, packet, "output");
            var response = Legacy17Protocol.ReadResponse(
                writer, expectedCmd, 0.4, InputReportIds, ReportId, null, debug, maxLength);
            return response is null ? null : Legacy17Protocol.ParseVersionPayload(response);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadSignal(HidStream writer, bool debug, int maxLength)
    {
        try
        {
            HidHelpers.SendReport(writer, RssiPacket, "output");
            var response = Legacy17Protocol.ReadResponse(
                writer, Legacy17Protocol.CmdRssi, 0.4, InputReportIds, ReportId, null, debug, maxLength);
            return response is null ? null : Legacy17Protocol.ParseSignalPayload(response);
        }
        catch
        {
            return null;
        }
    }

    private DeviceStatus? ReadBatteryCmd04(HidStream writer, HidStream reader, bool debug, string transport, ConnectionKind connection, string? connectionName, string? firmware, int? linkRateHz, int? signal, string? dongleFirmware)
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

        var (battery, charging, voltageMv) = parsed.Value;
        if (debug)
        {
            System.Diagnostics.Debug.WriteLine($"cmd04 raw={battery} charging={charging} mV={voltageMv} signal={signal} data={Convert.ToHexString(payload)}");
        }

        return new DeviceStatus(battery, charging, Name, connection, connectionName, firmware, linkRateHz, voltageMv, signal, dongleFirmware);
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
