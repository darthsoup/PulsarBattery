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

            var transportForInfo = writer!.Device.GetMaxFeatureReportLength() > 0 ? "feature" : "output";
            var maxInfoLen = Math.Max(writer.Device.GetMaxInputReportLength(), (reader ?? writer).Device.GetMaxInputReportLength());
            var info = ReadDeviceInfo(writer, reader ?? writer, transportForInfo, maxInfoLen, debug);

            // The device's own connection code beats guessing from the PID, and
            // it is the only source of the link rate on this protocol.
            var connection = info?.ConnectionCode switch
            {
                Legacy17Protocol.ConnectionWired => ConnectionKind.Wired,
                Legacy17Protocol.Connection1K or Legacy17Protocol.Connection4K => ConnectionKind.Dongle,
                _ => writerDevice.ProductID == PidWired ? ConnectionKind.Wired : ConnectionKind.Dongle,
            };
            var linkRateHz = info?.ConnectionCode switch
            {
                Legacy17Protocol.Connection1K => 1000,
                Legacy17Protocol.Connection4K => 4000,
                _ => (int?)null,
            };
            var connectionName = connection == ConnectionKind.Dongle ? HidHelpers.GetProductName(writerDevice) : null;
            // No known firmware command in the legacy protocol; wired bcdDevice
            // is the mouse's own version, the dongle's would be the dongle's.
            var firmware = connection == ConnectionKind.Wired ? HidHelpers.GetFirmwareFromBcd(writerDevice) : null;
            return ReadBatteryCmd04(writer!, reader ?? writer!, debug, transportForInfo, connection, connectionName, firmware, linkRateHz);
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

    // EEPROM addresses of the settings this app surfaces. Every entry is a
    // value/check pair except the DPI blocks, which are 4 bytes per stage.
    private const ushort AddrSysConfig = 0x0000;   // rate, stage count, active stage
    private const ushort AddrLod = 0x000A;
    private const ushort AddrDpiPair1 = 0x000C;    // stages 1+2; +8 per further pair
    private const ushort AddrAdvParams = 0x00A9;   // debounce, msync, led, snap, ripple

    // Stored polling code -> Hz. The low nibble is inverted relative to the
    // high one; the X2 V1 tops out at 1000 Hz, so only 0x01..0x08 occur here.
    private static readonly Dictionary<byte, int> PollingRateHzByCode = new()
    {
        [0x01] = 1000,
        [0x02] = 500,
        [0x04] = 250,
        [0x08] = 125,
        [0x10] = 2000,
        [0x20] = 4000,
        [0x40] = 8000,
    };

    /// <summary>
    /// Reads the on-device settings out of the mouse's EEPROM. The EEPROM lives
    /// on the mouse rather than the dongle, so this only answers while the
    /// wireless side is awake — an idle X2 V1 sleeps within seconds and every
    /// block then times out, which is reported as "no settings" rather than
    /// partial data.
    /// </summary>
    public DeviceSettings? ReadSettings(bool debug)
    {
        var devices = HidHelpers.EnumerateDevices(Vid, d => d.ProductID is PidWireless or PidWired).ToList();
        var writerDevice = devices.FirstOrDefault(d => SafeLength(d.GetMaxFeatureReportLength) == PacketLength);
        var readerDevice = devices.FirstOrDefault(d => SafeLength(d.GetMaxInputReportLength) == PacketLength);
        if (writerDevice is null || readerDevice is null)
        {
            return null;
        }

        HidStream? writer = null;
        HidStream? reader = null;
        try
        {
            if (!writerDevice.TryOpen(out writer) || !readerDevice.TryOpen(out reader))
            {
                return null;
            }

            writer.WriteTimeout = 500;
            reader.ReadTimeout = 250;
            var transport = writerDevice.GetMaxFeatureReportLength() > 0 ? "feature" : "output";

            // Fail fast while the mouse is asleep: the EEPROM would time out
            // block by block and cost several seconds under the global lock.
            var online = Exchange(writer, reader, Legacy17Protocol.BuildPacket(OutputReportId, Legacy17Protocol.CmdOnline), Legacy17Protocol.CmdOnline, transport, debug);
            if (online is null || online[2] != 0x00 || online[6] != 0x01)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine("x2v1 settings: mouse offline, skipping EEPROM read");
                }

                return null;
            }

            var sys = ReadEepromPairs(writer, reader, transport, AddrSysConfig, 3, debug);
            var adv = ReadEepromPairs(writer, reader, transport, AddrAdvParams, 5, debug);
            var lod = ReadEepromPairs(writer, reader, transport, AddrLod, 1, debug);

            int? dpi = null;
            if (sys is not null)
            {
                var stage = sys[2];
                if (stage >= 1)
                {
                    // Stages are stored two per block, four bytes each.
                    var block = (ushort)(AddrDpiPair1 + (((stage - 1) / 2) * 8));
                    var response = Exchange(writer, reader, Legacy17Protocol.BuildEepromReadPacket(OutputReportId, block, 8), Legacy17Protocol.CmdGetEeprom, transport, debug);
                    if (response is not null)
                    {
                        dpi = Legacy17Protocol.ParseDpiStage(response, (stage - 1) % 2);
                    }
                }
            }

            var settings = new DeviceSettings(
                PollingRateHz: sys is not null && PollingRateHzByCode.TryGetValue(sys[0], out var hz) ? hz : null,
                DebounceMs: adv?[0],
                MotionSync: adv is null ? null : adv[1] == 0x01,
                Dpi: dpi,
                DpiStage: sys?[2],
                LodMm10: lod?[0] switch { 1 => 10, 2 => 20, _ => null },
                AngleSnap: adv is null ? null : adv[3] == 0x01,
                RippleControl: adv is null ? null : adv[4] == 0x01);

            if (debug)
            {
                System.Diagnostics.Debug.WriteLine($"x2v1 settings: rate={settings.PollingRateHz} dpi={settings.Dpi} stage={settings.DpiStage} lod={settings.LodMm10} debounce={settings.DebounceMs} msync={settings.MotionSync} snap={settings.AngleSnap} ripple={settings.RippleControl}");
            }

            return settings == new DeviceSettings() ? null : settings;
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

    private static byte[]? ReadEepromPairs(HidStream writer, HidStream reader, string transport, ushort address, int pairs, bool debug)
    {
        var response = Exchange(writer, reader, Legacy17Protocol.BuildEepromReadPacket(OutputReportId, address, (byte)(pairs * 2)), Legacy17Protocol.CmdGetEeprom, transport, debug);
        return response is null ? null : Legacy17Protocol.ParseEepromPairs(response, pairs);
    }

    private static byte[]? Exchange(HidStream writer, HidStream reader, byte[] packet, byte expectedCmd, string transport, bool debug)
    {
        try
        {
            var maxLength = reader.Device.GetMaxInputReportLength();
            HidHelpers.DrainInput(reader, 4, maxLength);
            HidHelpers.SendReport(writer, packet, transport);
            System.Threading.Thread.Sleep(15);

            return Legacy17Protocol.ReadResponse(
                reader,
                expectedCmd,
                timeoutSeconds: 0.6,
                InputReportIds,
                normalizeReportId: DefaultInputReportId,
                bareReportFilter: static b => b is 0x01 or 0x02 or 0x03 or 0x04 or 0x08 or 0x0E,
                debug,
                maxLength,
                idleSleepMs: 10);
        }
        catch
        {
            return null;
        }
    }

    private static int SafeLength(Func<int> get)
    {
        try { return get(); }
        catch { return 0; }
    }

    private const int PacketLength = 17;

    /// <summary>
    /// Queries the device-identification command with a fresh random challenge.
    /// Returns null when the device does not answer or the response fails its
    /// own cross-check, so callers fall back to PID-based guessing.
    /// </summary>
    private static (int ModelId, byte ConnectionCode)? ReadDeviceInfo(
        HidStream writer,
        HidStream reader,
        string transport,
        int maxLength,
        bool debug)
    {
        Span<byte> challenge = stackalloc byte[4];
        System.Random.Shared.NextBytes(challenge);

        try
        {
            HidHelpers.DrainInput(reader, 4, maxLength);
            HidHelpers.SendReport(writer, Legacy17Protocol.BuildInfoPacket(OutputReportId, challenge), transport);
            System.Threading.Thread.Sleep(20);

            var payload = Legacy17Protocol.ReadResponse(
                reader,
                Legacy17Protocol.CmdInfo,
                timeoutSeconds: 0.6,
                InputReportIds,
                normalizeReportId: DefaultInputReportId,
                bareReportFilter: static b => b is 0x01 or 0x02 or 0x03 or 0x04 or 0x08 or 0x0E,
                debug,
                maxLength,
                idleSleepMs: 10);

            if (payload is null)
            {
                return null;
            }

            var info = Legacy17Protocol.ParseInfoPayload(payload, challenge);
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"cmd01 info model=0x{info?.ModelId:X4} conn=0x{info?.ConnectionCode:X2} data={Convert.ToHexString(payload)}");
            }

            return info;
        }
        catch
        {
            return null;
        }
    }

    private DeviceStatus? ReadBatteryCmd04(
        HidStream writer,
        HidStream reader,
        bool debug,
        string transport,
        ConnectionKind connection,
        string? connectionName,
        string? firmware,
        int? linkRateHz)
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

        return new DeviceStatus(battery, charging, Name, connection, connectionName, firmware, linkRateHz);
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
