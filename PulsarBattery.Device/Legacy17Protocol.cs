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

        return (payload[6], payload[7] != 0x00);
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
