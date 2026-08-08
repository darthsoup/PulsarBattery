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
/// <remarks>
/// This is the Pulsar "cMouse" vendor protocol. Command names and field
/// offsets below follow the reverse-engineering notes published in
/// amassias/Bibimbap (MIT), docs/protocol.md — a macOS configurator that
/// documented the same wire format we had been replaying blind. Their frame
/// indices are relative to the frame; ours include the report ID at [0], so
/// their data[n] is our payload[n + 1].
/// </remarks>
internal static class Legacy17Protocol
{
    public const byte OutputReportId = 0x08;
    public const byte CmdBattery = 0x04;

    /// <summary>Tells the device that configuration software is running.</summary>
    public const byte CmdDriverStatus = 0x02;

    /// <summary>
    /// Firmware version of the responding device. Unlike the wired-only
    /// bcdDevice fallback this answers over the dongle as well.
    /// </summary>
    public const byte CmdVersion = 0x12;

    /// <summary>Firmware version of the receiver itself, same payload shape.</summary>
    public const byte CmdDongleVersion = 0x1D;

    /// <summary>Radio signal strength, only meaningful behind a receiver.</summary>
    public const byte CmdRssi = 0x2B;

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

    /// <summary>Connection code from <see cref="CmdInfo"/>: cable at 8000 Hz.</summary>
    public const byte ConnectionWired8K = 0x03;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 2000 Hz.</summary>
    public const byte Connection2K = 0x04;

    /// <summary>Connection code from <see cref="CmdInfo"/>: dongle at 8000 Hz.</summary>
    public const byte Connection8K = 0x05;

    /// <summary>
    /// Maps a <see cref="CmdInfo"/> connection code to the transport and the
    /// link rate it runs at. Codes 3..5 were missing here while only 0..2 were
    /// known, which mis-read every 2K/8K link as an unknown connection.
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
    /// How the high bit of a stage's exponent code scales the value. The low
    /// bit always doubles; the high bit doubles on most sensors but is
    /// <c>x * 5 + 10000</c> on the "pulsar x1" family, and nothing on this
    /// protocol identifies the sensor.
    /// </summary>
    public enum DpiExponentScaling
    {
        /// <summary>Sensor family unknown — reject stages that use the high bit.</summary>
        Unknown,
        Doubling,
        PulsarX1,
    }

    /// <summary>
    /// Decodes one DPI stage from a <c>DpiPair</c> block. Each stage is four
    /// bytes — x, y, attributes, check — with <c>check = 0x55 - x - y - attr</c>.
    /// </summary>
    /// <remarks>
    /// The attributes byte packs four 2-bit fields, not a plain high byte:
    /// <c>xEx</c> at bits 0-1, x's high bits at 2-3, <c>yEx</c> at 4-5 and y's
    /// high bits at 6-7. Reading it as a high byte (as this did) only agrees
    /// with the real layout while it is zero, which is why the two X2 V1
    /// samples verified live — 07 07 00 47 = 400 DPI and 0F 0F 00 37 = 800 DPI
    /// — could not tell the formulas apart. Above 12800 DPI it diverged badly:
    /// a 16000 stage decoded as 54400.
    /// <para>
    /// Cross-checked against the X2 CrazyLight flash dump published in
    /// amassias/Bibimbap (Tests/.../x2-crazylight-core.json), with
    /// <paramref name="baseStep"/> 10 and <see cref="DpiExponentScaling.PulsarX1"/>:
    /// 27 27 00 07 = 400, 3F 3F 44 93 = 3200, 37 37 22 C5 = 12800.
    /// </para>
    /// </remarks>
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
                    // Guessing the branch would show a plausible but wrong DPI;
                    // report "unknown" and let the caller surface an em dash.
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
    /// Recovers the device info from a <see cref="CmdInfo"/> response. The
    /// firmware computes <c>resp[6+i] = challenge[i]*(i+1) + challenge[(i+1)%4]
    /// + info[i]</c>, so the transform inverts directly. The result is
    /// cross-checked against the cleartext copy at bytes 10..13 and rejected
    /// unless both agree — which makes a garbled or stale frame fail closed.
    /// Verified live on an X2 V1: three different challenges all decoded to
    /// 06 04 00 00, matching bytes 10..13 exactly.
    /// </summary>
    /// <remarks>
    /// The four info bytes are cid, mid, connection type and dongle type. The
    /// dongle type gates receiver-side lighting and button features we do not
    /// implement; it is returned for logging so the value is not silently lost.
    /// </remarks>
    public static (int ModelId, byte ConnectionCode, byte DongleType)? ParseInfoPayload(
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

        return ((decoded[0] << 8) | decoded[1], decoded[2], decoded[3]);
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
    /// Decodes a <see cref="CmdBattery"/> response. The frame also carries the
    /// pack voltage in millivolts (their data[7..8]) after the level and the
    /// charging flag; it is null when the frame is too short or reads zero.
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

        return (battery, charging, voltageMv);
    }

    /// <summary>
    /// Decodes a <see cref="CmdVersion"/> response into the "01.25"-style
    /// string the rest of the app displays. The device reports a major byte
    /// and a minor byte that is rendered in hex, so 3 / 0x05 reads "03.05".
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
    /// Decodes a <see cref="CmdRssi"/> response into a raw signal strength.
    /// </summary>
    /// <remarks>
    /// The value is a small bar count, not a percentage or a dBm figure. The
    /// Pulsar cMouse notes bucket it as 4+ excellent, 3 good, 2 fair, 0-1 weak.
    /// A status of 1 is the protocol's way of saying "this model has no RSSI",
    /// not an error — see <see cref="ParseBatteryPayload"/> for the same
    /// convention. Only call this once <see cref="WaitUntilOnline"/> has
    /// succeeded: a sleeping mouse behind a live receiver still answers, and
    /// its zero must not be shown as a weak signal.
    /// </remarks>
    public static int? ParseSignalPayload(IReadOnlyList<byte> payload)
    {
        if (payload.Count < 7 || payload[2] != 0x00)
        {
            return null;
        }

        return payload[6];
    }

    /// <summary>
    /// Blocks until the wireless side reports itself reachable, or the timeout
    /// elapses. Behind a dongle the receiver answers the handshake before it
    /// has reached the mouse, and anything read in that window times out with
    /// no useful explanation — so callers wait for the mouse itself.
    /// </summary>
    /// <returns>True once the mouse is online and idle.</returns>
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
