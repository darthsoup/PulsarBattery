using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

/// <summary>
/// Shared pieces of the legacy CompX/Nordic 17-byte report protocol used by
/// the X2 CrazyLight and X2 V1: [reportId, cmd, data...(14), checksum] where
/// checksum = 0x55 - sum(bytes[0..15]).
/// </summary>
internal static class Legacy17Protocol
{
    public const byte OutputReportId = 0x08;
    public const byte CmdBattery = 0x04;

    /// <summary>
    /// Device-identification command. Unlike the other legacy commands this one
    /// carries an 8-byte payload: four caller-chosen challenge bytes followed by
    /// four zero placeholders. The device answers with the challenge mixed into
    /// its own device info at bytes 6..9, and repeats that info in the clear at
    /// bytes 10..13.
    /// </summary>
    public const byte CmdInfo = 0x01;

    private const byte InfoPayloadLength = 0x08;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 1000 Hz.</summary>
    public const byte Connection1K = 0x00;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 4000 Hz.</summary>
    public const byte Connection4K = 0x01;

    /// <summary>Connection code from <see cref="CmdInfo"/>: mouse on the cable.</summary>
    public const byte ConnectionWired = 0x02;

    /// <summary>
    /// Reads a block of the mouse's settings EEPROM. The 16-bit address goes at
    /// bytes 3..4 (big-endian) and the byte count at byte 5. Only answered while
    /// the mouse itself is awake — the dongle cannot serve this on its own.
    /// </summary>
    public const byte CmdGetEeprom = 0x08;

    /// <summary>Reports whether the wireless side is currently reachable.</summary>
    public const byte CmdOnline = 0x03;

    public static byte[] BuildEepromReadPacket(byte reportId, ushort address, byte length)
        => BuildPacket(reportId, CmdGetEeprom, [0x00, (byte)(address >> 8), (byte)(address & 0xFF), length]);

    /// <summary>
    /// Settings are stored as value/check pairs where <c>value + check == 0x55</c>,
    /// which is why every setting sits on an even address. Returns one byte per
    /// pair, or null if any pair fails its check — so a torn or stale frame is
    /// rejected rather than surfaced as a bogus setting.
    /// </summary>
    public static byte[]? ParseEepromPairs(IReadOnlyList<byte> payload, int expectedPairs)
    {
        if (payload.Count < 6 || payload[2] != 0x00 || payload[5] < expectedPairs * 2)
        {
            return null;
        }

        var values = new byte[expectedPairs];
        for (var i = 0; i < expectedPairs; i++)
        {
            var value = payload[6 + (i * 2)];
            var check = payload[7 + (i * 2)];
            if ((byte)((value + check) & 0xFF) != 0x55)
            {
                return null;
            }

            values[i] = value;
        }

        return values;
    }

    /// <summary>
    /// Decodes one DPI stage from a <c>DpiPair</c> block. Each stage is four
    /// bytes — x, y, high bits, check — with <c>check = 0x55 - x - y - high</c>.
    /// Verified live on an X2 V1: 07 07 00 47 = 400 DPI, 0F 0F 00 37 = 800 DPI.
    /// </summary>
    public static int? ParseDpiStage(IReadOnlyList<byte> payload, int stageWithinBlock)
    {
        var at = 6 + (stageWithinBlock * 4);
        if (payload.Count < at + 4)
        {
            return null;
        }

        var x = payload[at];
        var y = payload[at + 1];
        var high = payload[at + 2];
        if ((byte)((0x55 - x - y - high) & 0xFF) != payload[at + 3])
        {
            return null;
        }

        var dpi = ((x | (high << 8)) + 1) * 50;
        return dpi is > 0 and <= 32000 ? dpi : null;
    }

    public static byte[] BuildInfoPacket(byte reportId, ReadOnlySpan<byte> challenge)
    {
        // [id, 0x01, 0, 0, 0, len=8, c0..c3, 0, 0, 0, 0, 0, 0, checksum]
        Span<byte> payload = stackalloc byte[8];
        payload[3] = InfoPayloadLength;
        challenge[..4].CopyTo(payload[4..]);
        return BuildPacket(reportId, CmdInfo, payload);
    }

    /// <summary>
    /// Recovers the device info from a <see cref="CmdInfo"/> response. The
    /// firmware computes <c>resp[6+i] = challenge[i]*(i+1) + challenge[(i+1)%4]
    /// + info[i]</c>, so the transform inverts directly. The result is
    /// cross-checked against the cleartext copy at bytes 10..13 and rejected
    /// unless both agree — which makes a garbled or stale frame fail closed.
    /// Verified live on an X2 V1: three different challenges all decoded to
    /// 06 04 00 00, matching bytes 10..13 exactly.
    /// </summary>
    public static (int ModelId, byte ConnectionCode)? ParseInfoPayload(
        IReadOnlyList<byte> payload,
        ReadOnlySpan<byte> challenge)
    {
        if (payload.Count < 14 || payload[2] != 0x00)
        {
            return null;
        }

        Span<byte> decoded = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            decoded[i] = (byte)((payload[6 + i] - (challenge[i] * (i + 1)) - challenge[(i + 1) % 4]) & 0xFF);
        }

        for (var i = 0; i < 4; i++)
        {
            if (decoded[i] != payload[10 + i])
            {
                return null;
            }
        }

        return ((decoded[0] << 8) | decoded[1], decoded[2]);
    }

    public static byte[] BuildPacket(byte reportId, byte cmd, ReadOnlySpan<byte> payload = default)
    {
        var packet = new byte[17];
        packet[0] = reportId;
        packet[1] = cmd;
        payload.CopyTo(packet.AsSpan(2));
        packet[16] = Checksum(packet.AsSpan(0, 16));
        return packet;
    }

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
        {
            sum += b;
        }

        return (byte)((0x55 - (sum & 0xFF)) & 0xFF);
    }

    public static (int battery, bool charging)? ParseBatteryPayload(IReadOnlyList<byte> payload)
    {
        if (payload.Count < 8)
        {
            return null;
        }

        var battery = payload[6];
        var charging = payload[7] != 0x00;

        // The dongle answers the battery command even when the mouse itself is
        // not reachable over RF (asleep, out of range, switched off) — and then
        // reports 0%. Verified live on an X2 V1: the live read returned 0 while
        // the dongle's own cached state report still held 100%, and it snapped
        // back to 100% as soon as the mouse became active again. Treating that
        // as a real reading makes the app show 0% and fire a false low-battery
        // alert, so report "no reading" instead and let the caller keep its
        // cached value. 0% while charging is left alone: a flat battery on the
        // cable is a legitimate state.
        if (battery == 0 && !charging)
        {
            return null;
        }

        if (battery > 100)
        {
            return null;
        }

        return (battery, charging);
    }

    /// <summary>
    /// Reads input reports until one matches the expected command or the
    /// timeout elapses. Bare 16-byte reports (report ID stripped by the OS)
    /// are re-prefixed with <paramref name="normalizeReportId"/>;
    /// <paramref name="bareReportFilter"/> can restrict which bare reports
    /// qualify.
    /// </summary>
    public static byte[]? ReadResponse(
        HidStream reader,
        byte expectedCmd,
        double timeoutSeconds,
        ISet<byte> validReportIds,
        byte normalizeReportId,
        Func<byte, bool>? bareReportFilter,
        bool debug,
        int maxLength,
        int idleSleepMs = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var data = HidHelpers.ReadWithTimeout(reader, maxLength, 250);
            if (data is null || data.Length == 0)
            {
                if (idleSleepMs > 0)
                {
                    System.Threading.Thread.Sleep(idleSleepMs);
                }

                continue;
            }

            var payload = Normalize(data, normalizeReportId, bareReportFilter);
            if (payload.Length < 7 || !validReportIds.Contains(payload[0]))
            {
                continue;
            }

            if (payload[1] != expectedCmd)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine($"legacy17 skip cmd=0x{payload[1]:X2} data={Convert.ToHexString(payload)}");
                }

                continue;
            }

            return payload;
        }

        return null;
    }

    private static byte[] Normalize(byte[] data, byte normalizeReportId, Func<byte, bool>? bareReportFilter)
    {
        if (data.Length == 16 && (bareReportFilter is null || bareReportFilter(data[0])))
        {
            return new[] { normalizeReportId }.Concat(data).ToArray();
        }

        return data;
    }
}
