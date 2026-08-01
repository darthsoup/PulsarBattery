using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

/// <summary>
/// The Sonix-chipset 64-byte feature-report protocol used by newer Pulsar
/// mice (X2 V3 / X2 V3 eS, X3, Xlite V4 generation). Wire packet:
/// [direction, category, register, sub, 0, 0, values...] with a little-endian
/// 16-bit sum of bytes 0..61 stored at bytes 62..63, exchanged via
/// SetFeature/GetFeature with report ID 0. Reads set bit 7 of the register.
/// Responses are asynchronous: GetFeature returns the last stored response
/// (direction 0x01, or 0x02 after some writes; 0x05 = still pending), so
/// exchanges poll until the category/register/sub echo matches.
/// </summary>
internal static class Sonix64Protocol
{
    // Wire-format packet length excluding the report ID byte.
    public const int PacketLength = 64;

    private static readonly byte[] FirmwareQuery = [0x01, 0x87, 0x04];
    private static readonly byte[] BatteryQuery = [0x08, 0x81, 0x01];
    private static readonly byte[] ConnectionTypeQuery = [0x08, 0x83, 0x01];
    private static readonly byte[] PollingRateQuery = [0x08, 0x85, 0x03];
    private static readonly byte[] MotionSyncQuery = [0x07, 0x85, 0x02];
    private static readonly byte[] DebounceQuery = [0x04, 0x83, 0x03];
    private static readonly byte[] DpiQuery = [0x05, 0x82, 0x05];
    private static readonly byte[] DpiStageQuery = [0x05, 0x81, 0x02];
    private static readonly byte[] LodQuery = [0x07, 0x82, 0x03];
    private static readonly byte[] AngleSnapQuery = [0x07, 0x84, 0x02];
    private static readonly byte[] RippleControlQuery = [0x07, 0x83, 0x02];

    private static readonly byte[] MotionSyncWrite = [0x07, 0x05, 0x02];
    private static readonly byte[] DebounceWrite = [0x04, 0x03, 0x03];
    private static readonly byte[] DpiWrite = [0x05, 0x02, 0x05];
    private static readonly byte[] LodWrite = [0x07, 0x02, 0x03];
    private static readonly byte[] AngleSnapWrite = [0x07, 0x04, 0x02];
    private static readonly byte[] RippleControlWrite = [0x07, 0x03, 0x02];
    private static readonly byte[] PollingRateWrite = [0x01, 0x09, 0x02];

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

    // Write commands use a power-of-two code instead of the interval value.
    private static readonly Dictionary<int, byte> PollingRateCodeByHz = new()
    {
        [125] = 0x40,
        [250] = 0x20,
        [500] = 0x10,
        [1000] = 0x08,
        [2000] = 0x04,
        [4000] = 0x02,
        [8000] = 0x01,
    };

    public static IReadOnlyCollection<int> SupportedPollingRates => PollingRateCodeByHz.Keys;

    /// <summary>
    /// Firmware version register: b6 = minor, b7 = major, hex-formatted
    /// ("01.25"-style, matching the USB bcdDevice notation).
    /// </summary>
    public static string? ReadFirmwareVersion(HidStream stream, bool debug)
    {
        var wire = Query(stream, FirmwareQuery, debug);
        if (wire is null || (wire[6] == 0 && wire[7] == 0))
        {
            return null;
        }

        return $"{wire[7]:X2}.{wire[6]:X2}";
    }

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
        => ReadBoolAt7(stream, MotionSyncQuery, debug);

    public static bool? ReadAngleSnap(HidStream stream, bool debug)
        => ReadBoolAt7(stream, AngleSnapQuery, debug);

    public static bool? ReadRippleControl(HidStream stream, bool debug)
        => ReadBoolAt7(stream, RippleControlQuery, debug);

    public static int? ReadDebounceMs(HidStream stream, bool debug)
    {
        var wire = Query(stream, DebounceQuery, debug);
        if (wire is null)
        {
            return null;
        }

        return wire[7];
    }

    public static int? ReadLodMm10(HidStream stream, bool debug)
    {
        var wire = Query(stream, LodQuery, debug);
        if (wire is null)
        {
            return null;
        }

        var value = wire[8];
        return value is > 0 and <= 30 ? value : null;
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

    public static bool WriteMotionSync(HidStream stream, bool enabled, bool debug)
        => Write(stream, MotionSyncWrite, [(byte)(enabled ? 1 : 0)], debug)
           && ReadMotionSync(stream, debug) == enabled;

    public static bool WriteAngleSnap(HidStream stream, bool enabled, bool debug)
        => Write(stream, AngleSnapWrite, [(byte)(enabled ? 1 : 0)], debug)
           && ReadAngleSnap(stream, debug) == enabled;

    public static bool WriteRippleControl(HidStream stream, bool enabled, bool debug)
        => Write(stream, RippleControlWrite, [(byte)(enabled ? 1 : 0)], debug)
           && ReadRippleControl(stream, debug) == enabled;

    public static bool WriteDebounceMs(HidStream stream, int ms, bool debug)
    {
        if (ms is < 0 or > 30)
        {
            return false;
        }

        return Write(stream, DebounceWrite, [(byte)ms], debug)
               && ReadDebounceMs(stream, debug) == ms;
    }

    public static bool WriteLodMm10(HidStream stream, int mm10, bool debug)
    {
        if (mm10 is not (7 or 10 or 20))
        {
            return false;
        }

        return Write(stream, LodWrite, [0x02, (byte)mm10], debug)
               && ReadLodMm10(stream, debug) == mm10;
    }

    public static bool WriteDpi(HidStream stream, int dpi, bool debug)
    {
        if (dpi is < 50 or > 26000)
        {
            return false;
        }

        var lo = (byte)(dpi & 0xFF);
        var hi = (byte)(dpi >> 8);
        return Write(stream, DpiWrite, [lo, hi, lo, hi], debug)
               && ReadDpi(stream, debug) == dpi;
    }

    public static bool WritePollingRateHz(HidStream stream, int hz, bool debug)
    {
        if (!PollingRateCodeByHz.TryGetValue(hz, out var code))
        {
            return false;
        }

        // The device ACKs the write but the live rate may not change until it
        // reconnects (observed on the X2 V3 eS in wireless 1 kHz mode), so
        // success here means "accepted", and the caller compares the re-read
        // value to surface a mismatch.
        return Write(stream, PollingRateWrite, [code], debug);
    }

    public static byte[]? Query(HidStream stream, IReadOnlyList<byte> command, bool debug)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            stream.SetFeature(BuildPacket(command));

            for (var read = 0; read < 6; read++)
            {
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
                    System.Diagnostics.Debug.WriteLine($"sonix64 pending cmd={Convert.ToHexString(command.ToArray())} resp={Convert.ToHexString(wire[..16])}");
                }
            }
        }

        return null;
    }

    private static bool Write(HidStream stream, byte[] catRegSub, byte[] values, bool debug)
    {
        var wire = new byte[PacketLength];
        wire[1] = catRegSub[0];
        wire[2] = catRegSub[1];
        wire[3] = catRegSub[2];
        wire[6] = 0x01; // profile 1 (captured layout)
        values.CopyTo(wire, 7);
        FinalizePacket(wire, out var buffer);

        stream.SetFeature(buffer);

        for (var read = 0; read < 6; read++)
        {
            System.Threading.Thread.Sleep(60);

            var response = new byte[PacketLength + 1];
            stream.GetFeature(response);

            var echo = response.Skip(1).ToArray();
            if (echo[0] is 0x01 or 0x02 && echo[1] == catRegSub[0] && echo[2] == catRegSub[1] && echo[3] == catRegSub[2])
            {
                return true;
            }

            if (debug)
            {
                System.Diagnostics.Debug.WriteLine($"sonix64 write pending cmd={Convert.ToHexString(catRegSub)} resp={Convert.ToHexString(echo[..16])}");
            }
        }

        return false;
    }

    private static bool? ReadBoolAt7(HidStream stream, byte[] query, bool debug)
    {
        var wire = Query(stream, query, debug);
        if (wire is null)
        {
            return null;
        }

        return wire[7] == 0x01;
    }

    public static byte[] BuildPacket(IReadOnlyList<byte> command)
    {
        var wire = new byte[PacketLength];
        for (var i = 0; i < command.Count; i++)
        {
            wire[1 + i] = command[i];
        }

        FinalizePacket(wire, out var buffer);
        return buffer;
    }

    private static void FinalizePacket(byte[] wire, out byte[] buffer)
    {
        var sum = 0;
        for (var i = 0; i < 62; i++)
        {
            sum += wire[i];
        }

        wire[62] = (byte)(sum & 0xFF);
        wire[63] = (byte)((sum >> 8) & 0xFF);

        // HidD_SetFeature buffers are prefixed with the report ID (0x00).
        buffer = new byte[PacketLength + 1];
        wire.CopyTo(buffer, 1);
    }
}
