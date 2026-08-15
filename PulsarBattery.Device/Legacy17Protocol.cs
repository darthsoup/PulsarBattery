using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

/// <summary>
/// Pulsar "cMouse" legacy 17-byte protocol (X2 CrazyLight, X2 V1): [reportId, cmd, data(14), checksum],
/// checksum = 0x55 - sum(bytes[0..15]). Offsets follow amassias/Bibimbap (MIT), shifted +1 for our report ID.
/// </summary>
internal static class Legacy17Protocol
{
    private const int FrameLength = 17;
    private const int MaxDataLength = 10;

    public const byte OutputReportId = 0x08;
    public const byte CmdBattery = 0x04;

    /// <summary>Tells the device that configuration software is running.</summary>
    public const byte CmdDriverStatus = 0x02;

    /// <summary>Firmware version of the responding device; unlike bcdDevice this answers over the dongle too.</summary>
    public const byte CmdVersion = 0x12;

    /// <summary>Firmware version of the receiver itself, same payload shape.</summary>
    public const byte CmdDongleVersion = 0x1D;

    /// <summary>Radio signal strength, only meaningful behind a receiver.</summary>
    public const byte CmdRssi = 0x2B;

    /// <summary>
    /// Device identification; 8-byte payload of 4 challenge bytes + 4 zeros. Answers with CID/MID mixed into
    /// the challenge at 6..7 and CID, MID, connection and dongle type in clear at 10..13 (V3.04 zeros 8..9).
    /// </summary>
    public const byte CmdInfo = 0x01;

    private const byte InfoPayloadLength = 0x08;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 1000 Hz.</summary>
    public const byte Connection1K = 0x00;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 4000 Hz.</summary>
    public const byte Connection4K = 0x01;

    /// <summary>Connection code from <see cref="CmdInfo"/>: mouse on the cable.</summary>
    public const byte ConnectionWired = 0x02;

    /// <summary>Connection code from <see cref="CmdInfo"/>: cable at 8000 Hz.</summary>
    public const byte ConnectionWired8K = 0x03;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 2000 Hz.</summary>
    public const byte Connection2K = 0x04;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 8000 Hz.</summary>
    public const byte Connection8K = 0x05;

    /// <summary>
    /// Maps a <see cref="CmdInfo"/> connection code to transport and link rate.
    /// Codes 3..5 were once missing here, which mis-read every 2K/8K link as unknown.
    /// </summary>
    public static (ConnectionKind Kind, int LinkRateHz)? DecodeConnection(byte code) => code switch
    {
        Connection1K => (ConnectionKind.Dongle, 1000),
        Connection4K => (ConnectionKind.Dongle, 4000),
        ConnectionWired => (ConnectionKind.Wired, 1000),
        ConnectionWired8K => (ConnectionKind.Wired, 8000),
        Connection2K => (ConnectionKind.Dongle, 2000),
        Connection8K => (ConnectionKind.Dongle, 8000),
        _ => null,
    };

    /// <summary>
    /// Reads a settings-EEPROM block: big-endian address at bytes 3..4, count at byte 5.
    /// Only answered while the mouse is awake; the dongle cannot serve it alone.
    /// </summary>
    public const byte CmdGetEeprom = 0x08;

    /// <summary>Writes at most ten bytes to the mouse's settings EEPROM.</summary>
    public const byte CmdSetEeprom = 0x07;

    /// <summary>Reports whether the wireless side is currently reachable.</summary>
    public const byte CmdOnline = 0x03;

    public static byte[] BuildEepromReadPacket(byte reportId, ushort address, byte length)
        => BuildPacket(reportId, CmdGetEeprom, [0x00, (byte)(address >> 8), (byte)(address & 0xFF), length]);

    /// <summary>
    /// Builds a non-flash command frame: status and address zero, length without routing flags, data at byte 6.
    /// </summary>
    public static byte[] BuildCommandPacket(byte reportId, byte cmd, ReadOnlySpan<byte> data = default)
    {
        if (data.Length > MaxDataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "A legacy 17-byte frame carries at most 10 data bytes.");
        }

        var packet = new byte[FrameLength];
        packet[0] = reportId;
        packet[1] = cmd;
        packet[5] = (byte)data.Length;
        data.CopyTo(packet.AsSpan(6, data.Length));
        packet[16] = Checksum(packet.AsSpan(0, 16));
        return packet;
    }

    /// <summary>Builds <see cref="CmdDriverStatus"/>; true announces an active driver, false releases it.</summary>
    public static byte[] BuildDriverStatusPacket(byte reportId, bool online)
        => BuildCommandPacket(reportId, CmdDriverStatus, [online ? (byte)0x01 : (byte)0x00]);

    /// <summary>
    /// Builds a <see cref="CmdOnline"/> query when <paramref name="hold"/> is null, else the acquire/release form.
    /// </summary>
    public static byte[] BuildOnlinePacket(byte reportId, bool? hold = null)
        => hold.HasValue
            ? BuildCommandPacket(reportId, CmdOnline, [hold.Value ? (byte)0x01 : (byte)0x00])
            : BuildCommandPacket(reportId, CmdOnline);

    /// <summary>
    /// Builds one EEPROM write frame (big-endian address, max 10 data bytes).
    /// The keyboard-mode routing bit is deliberately never set for mouse traffic.
    /// </summary>
    public static byte[] BuildEepromWritePacket(byte reportId, ushort address, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxDataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "An EEPROM write carries at most 10 data bytes.");
        }

        var packet = new byte[FrameLength];
        packet[0] = reportId;
        packet[1] = CmdSetEeprom;
        packet[3] = (byte)(address >> 8);
        packet[4] = (byte)(address & 0xFF);
        packet[5] = (byte)data.Length;
        data.CopyTo(packet.AsSpan(6, data.Length));
        packet[16] = Checksum(packet.AsSpan(0, 16));
        return packet;
    }

    /// <summary>Encodes a value with its complement so the pair sums to 0x55 mod 256.</summary>
    public static byte[] EncodeCheckedValue(byte value)
        => [value, unchecked((byte)(0x55 - value))];

    /// <summary>
    /// Validates the first complete 17-byte report; extra bytes from a larger HID collection are ignored.
    /// </summary>
    public static bool HasValidChecksum(IReadOnlyList<byte> frame)
    {
        if (frame is null || frame.Count < FrameLength)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < FrameLength; i++)
        {
            sum += frame[i];
        }

        return (sum & 0xFF) == 0x55;
    }

    /// <summary>
    /// Parses an EEPROM response only when checksum, status, command, address and length match the request.
    /// A status-only acknowledgement is rejected because it carries no data.
    /// </summary>
    public static bool TryParseEepromResponse(
        IReadOnlyList<byte> frame,
        byte expectedCmd,
        ushort address,
        int length,
        out byte[] data)
    {
        data = Array.Empty<byte>();
        if (length is < 0 or > MaxDataLength
            || frame is null
            || frame.Count < FrameLength
            || !HasValidChecksum(frame)
            || frame[1] != expectedCmd
            || frame[2] != 0x00
            || frame[3] != (byte)(address >> 8)
            || frame[4] != (byte)(address & 0xFF)
            || frame[5] != (byte)length)
        {
            return false;
        }

        data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = frame[6 + i];
        }

        return true;
    }

    /// <summary>
    /// Returns true only for the exact 17-byte echo that the official cMouse
    /// driver requires as acknowledgement of <see cref="CmdSetEeprom"/>.
    /// </summary>
    public static bool MatchesWriteAck(IReadOnlyList<byte> response, IReadOnlyList<byte> request)
    {
        if (response is null
            || request is null
            || response.Count < FrameLength
            || request.Count < FrameLength
            || request[1] != CmdSetEeprom
            || request[2] != 0x00
            || request[5] > MaxDataLength
            || !HasValidChecksum(request)
            || !HasValidChecksum(response))
        {
            return false;
        }

        for (var i = 0; i < FrameLength; i++)
        {
            if (response[i] != request[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates a write ack by rebuilding the exact frame the device must echo.</summary>
    public static bool MatchesWriteAck(
        IReadOnlyList<byte> response,
        ushort address,
        ReadOnlySpan<byte> data)
    {
        if (response is null || response.Count < FrameLength || data.Length > MaxDataLength)
        {
            return false;
        }

        var request = BuildEepromWritePacket(response[0], address, data);
        return MatchesWriteAck(response, request);
    }

    /// <summary>
    /// Settings are value/check pairs summing to 0x55, so every setting sits on an even address.
    /// Returns null if any pair fails its check, rejecting torn frames instead of surfacing bogus settings.
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
    /// How the exponent high bit scales DPI: doubling on most sensors, x * 5 + 10000 on the "pulsar x1"
    /// family. Nothing in this protocol identifies the sensor, so the caller must supply it.
    /// </summary>
    public enum DpiExponentScaling
    {
        /// <summary>Sensor family unknown: reject stages that use the high bit.</summary>
        Unknown,
        Doubling,
        PulsarX1,
    }

    /// <summary>
    /// Decodes one DPI stage: four bytes (x, y, attributes, check) with check = 0x55 - x - y - attr.
    /// Attributes packs 2-bit fields, not a high byte: xEx 0-1, x high 2-3, yEx 4-5, y high 6-7.
    /// </summary>
    public static int? ParseDpiStage(
        IReadOnlyList<byte> payload,
        int stageWithinBlock,
        int baseStep,
        DpiExponentScaling scaling = DpiExponentScaling.Unknown)
    {
        var at = 6 + (stageWithinBlock * 4);
        if (payload.Count < at + 4)
        {
            return null;
        }

        var x = payload[at];
        var y = payload[at + 1];
        var attributes = payload[at + 2];
        if ((byte)((0x55 - x - y - attributes) & 0xFF) != payload[at + 3])
        {
            return null;
        }

        var dpi = ((x | (((attributes >> 2) & 0b11) << 8)) + 1) * baseStep;

        if ((attributes & 0b10) != 0)
        {
            switch (scaling)
            {
                case DpiExponentScaling.Doubling:
                    dpi *= 2;
                    break;
                case DpiExponentScaling.PulsarX1:
                    dpi = (dpi * 5) + 10000;
                    break;
                default:
                    // Guessing the branch would show a plausible but wrong DPI.
                    return null;
            }
        }

        if ((attributes & 0b01) != 0)
        {
            dpi *= 2;
        }

        return dpi is > 0 and <= 42000 ? dpi : null;
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
    /// Inverts the firmware mix <c>resp[6+i] = challenge[i]*(i+1) + challenge[(i+1)%4] + info[i]</c>.
    /// CID/MID must agree with the cleartext copy at 10..11, so a garbled or stale frame fails closed.
    /// </summary>
    public static (int ModelId, byte ConnectionCode, byte DongleType)? ParseInfoPayload(
        IReadOnlyList<byte> payload,
        ReadOnlySpan<byte> challenge)
    {
        if (payload.Count < 14 || challenge.Length < 4 || payload[2] != 0x00)
        {
            return null;
        }

        Span<byte> decoded = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            decoded[i] = (byte)((payload[6 + i] - (challenge[i] * (i + 1)) - challenge[(i + 1) % 4]) & 0xFF);
        }

        // V3.04 zeros connection/dongle in the mixed copy, so only CID/MID cross-check portably.
        for (var i = 0; i < 2; i++)
        {
            if (decoded[i] != payload[10 + i])
            {
                return null;
            }
        }

        return ((decoded[0] << 8) | decoded[1], payload[12], payload[13]);
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

    /// <summary>
    /// Decodes a <see cref="CmdBattery"/> response; pack voltage in mV follows the level and charging flag.
    /// </summary>
    public static (int battery, bool charging, int? voltageMv)? ParseBatteryPayload(IReadOnlyList<byte> payload)
    {
        if (payload.Count < 8)
        {
            return null;
        }

        var battery = payload[6];
        var charging = payload[7] != 0x00;

        int? voltageMv = null;
        if (payload.Count >= 10)
        {
            var millivolts = (payload[8] << 8) | payload[9];
            // Guard against an unpopulated field on models that don't report it.
            if (millivolts is > 1000 and < 6000)
            {
                voltageMv = millivolts;
            }
        }

        // The dongle answers with 0% when the mouse is unreachable (asleep, out of range), which would fire a
        // false low-battery alert. Report no reading so the caller keeps its cache; 0% while charging is real.
        if (battery == 0 && !charging)
        {
            return null;
        }

        if (battery > 100)
        {
            return null;
        }

        return (battery, charging, voltageMv);
    }

    /// <summary>
    /// Decodes <see cref="CmdVersion"/> to an "01.25"-style string; the minor byte renders as hex.
    /// </summary>
    public static string? ParseVersionPayload(IReadOnlyList<byte> payload)
    {
        if (payload.Count < 8 || payload[2] != 0x00)
        {
            return null;
        }

        var major = payload[6];
        var minor = payload[7];
        if (major == 0 && minor == 0)
        {
            return null;
        }

        return $"{major:D2}.{minor:X2}";
    }

    /// <summary>
    /// Decodes <see cref="CmdRssi"/> to a bar count (4+ excellent, 3 good, 2 fair, 0-1 weak), not a percentage.
    /// Call only after <see cref="WaitUntilOnline"/>: a sleeping mouse still answers, and its zero is not weak signal.
    /// </summary>
    public static int? ParseSignalPayload(IReadOnlyList<byte> payload)
    {
        if (payload.Count < 7 || payload[2] != 0x00)
        {
            return null;
        }

        return payload[6];
    }

    /// <summary>
    /// Blocks until the mouse itself is reachable. The receiver answers the handshake before it has
    /// reached the mouse, and anything read in that window times out with no useful explanation.
    /// </summary>
    public static bool WaitUntilOnline(
        HidStream writer,
        HidStream reader,
        byte reportId,
        string transport,
        ISet<byte> validReportIds,
        double timeoutSeconds,
        bool debug,
        int maxLength)
    {
        var packet = BuildPacket(reportId, CmdOnline);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            HidHelpers.SendReport(writer, packet, transport);
            var response = ReadResponse(reader, CmdOnline, 0.4, validReportIds, reportId, null, debug, maxLength);

            // data[5] is the mouse's own reachability and data[9] falls back to
            // zero once the receiver has finished talking to it.
            if (response is not null && response.Length > 10 && response[6] == 0x01 && response[10] == 0x00)
            {
                return true;
            }

            System.Threading.Thread.Sleep(20);
        }

        if (debug)
        {
            System.Diagnostics.Debug.WriteLine("legacy17 waitUntilOnline timed out");
        }

        return false;
    }

    /// <summary>
    /// Reads input reports until one matches the expected command. Bare 16-byte reports (report ID
    /// stripped by the OS) are re-prefixed with <paramref name="normalizeReportId"/>.
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
