using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

/// <summary>
/// The Sonix-chipset 64-byte feature-report protocol used by newer Pulsar
/// mice (X2 V3 / X2 V3 eS, X3, Xlite V4 generation). Wire packet:
/// [0x00, category, register, sub, 0, 0, values...] with a little-endian
/// 16-bit sum of bytes 0..61 stored at bytes 62..63. Reads set bit 7 of the
/// register; the device echoes [0x01, category, register, sub, ...] back via
/// GetFeature on the same interface (report ID 0).
/// </summary>
internal static class Sonix64Protocol
{
    // Wire-format packet length excluding the report ID byte.
    public const int PacketLength = 64;

    private static readonly byte[] BatteryQuery = [0x08, 0x81, 0x01];
    private static readonly byte[] ConnectionTypeQuery = [0x08, 0x83, 0x01];
    private static readonly byte[] PollingRateQuery = [0x08, 0x85, 0x03];
    private static readonly byte[] MotionSyncQuery = [0x07, 0x85, 0x02];
    private static readonly byte[] DebounceQuery = [0x04, 0x83, 0x03];
    private static readonly byte[] DpiQuery = [0x05, 0x82, 0x05];
    private static readonly byte[] DpiStageQuery = [0x05, 0x81, 0x02];

    // Polling-rate register value ≈ 30000 / Hz, rounded (captured mapping).
    private static readonly Dictionary<byte, int> PollingRateByValue = new()
    {
        [240] = 125,
        [120] = 250,
        [60] = 500,
        [30] = 1000,
        [15] = 2000,
        [8] = 4000,
        [4] = 8000,
    };

    public static int? ReadBatteryPercent(HidStream stream, bool debug)
    {
        var wire = Query(stream, BatteryQuery, debug);
        if (wire is null || wire[6] > 100)
        {
            return null;
        }

        return wire[6];
    }

    /// <summary>
    /// Connection type register: 2/3 = wired 1k/8k (mouse is on the charging
    /// cable), 0/1/4/5 = wireless via dongle at 1k/4k/2k/8k.
    /// </summary>
    public static ConnectionKind ReadConnectionKind(HidStream stream, bool debug)
    {
        var wire = Query(stream, ConnectionTypeQuery, debug);
        if (wire is null)
        {
            return ConnectionKind.Unknown;
        }

        return wire[6] is 2 or 3 ? ConnectionKind.Wired : ConnectionKind.Dongle;
    }

    public static int? ReadPollingRateHz(HidStream stream, bool debug)
    {
        var wire = Query(stream, PollingRateQuery, debug);
        if (wire is null)
        {
            return null;
        }

        return PollingRateByValue.TryGetValue(wire[7], out var hz) ? hz : null;
    }

    public static bool? ReadMotionSync(HidStream stream, bool debug)
    {
        var wire = Query(stream, MotionSyncQuery, debug);
        if (wire is null)
        {
            return null;
        }

        return wire[7] == 0x01;
    }

    public static int? ReadDebounceMs(HidStream stream, bool debug)
    {
        var wire = Query(stream, DebounceQuery, debug);
        if (wire is null)
        {
            return null;
        }

        return wire[7];
    }

    public static int? ReadDpi(HidStream stream, bool debug)
    {
        var wire = Query(stream, DpiQuery, debug);
        if (wire is null)
        {
            return null;
        }

        var dpi = wire[7] | (wire[8] << 8);
        return dpi is > 0 and <= 35000 ? dpi : null;
    }

    public static int? ReadDpiStage(HidStream stream, bool debug)
    {
        var wire = Query(stream, DpiStageQuery, debug);
        if (wire is null)
        {
            return null;
        }

        var stage = wire[7];
        return stage is >= 1 and <= 10 ? stage : null;
    }

    public static byte[]? Query(HidStream stream, IReadOnlyList<byte> command, bool debug)
    {
        // The dongle occasionally answers the first exchange with a stale
        // report right after enumeration, so allow one retry.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = BuildPacket(command);
            stream.SetFeature(request);
            System.Threading.Thread.Sleep(50);

            var response = new byte[PacketLength + 1];
            stream.GetFeature(response);

            // Strip the report ID byte; validate the command echo.
            var wire = response.Skip(1).ToArray();
            if (wire[0] == 0x01 && wire[1] == command[0] && wire[2] == command[1] && wire[3] == command[2])
            {
                return wire;
            }

            if (debug)
            {
                System.Diagnostics.Debug.WriteLine($"sonix64 bad echo cmd={Convert.ToHexString(command.ToArray())} resp={Convert.ToHexString(wire[..16])}");
            }

            System.Threading.Thread.Sleep(50);
        }

        return null;
    }

    public static byte[] BuildPacket(IReadOnlyList<byte> command)
    {
        var wire = new byte[PacketLength];
        for (var i = 0; i < command.Count; i++)
        {
            wire[1 + i] = command[i];
        }

        var sum = 0;
        for (var i = 0; i < 62; i++)
        {
            sum += wire[i];
        }

        wire[62] = (byte)(sum & 0xFF);
        wire[63] = (byte)((sum >> 8) & 0xFF);

        // HidD_SetFeature buffers are prefixed with the report ID (0x00).
        var buffer = new byte[PacketLength + 1];
        wire.CopyTo(buffer, 1);
        return buffer;
    }
}
