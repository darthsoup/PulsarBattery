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
    private static readonly byte[] VersionPacket = Legacy17Protocol.BuildPacket(OutputReportId, Legacy17Protocol.CmdVersion);
    private static readonly byte[] DongleVersionPacket = Legacy17Protocol.BuildPacket(OutputReportId, Legacy17Protocol.CmdDongleVersion);
    private static readonly byte[] RssiPacket = Legacy17Protocol.BuildPacket(OutputReportId, Legacy17Protocol.CmdRssi);

    // Firmware is stable while connected and this path also serves the 5-second
    // BatteryMonitor loop, so successful answers are kept.
    private string? _cachedFirmware;
    private string? _cachedDongleFirmware;

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
            var decoded = info is null ? null : Legacy17Protocol.DecodeConnection(info.Value.ConnectionCode);
            var connection = decoded?.Kind
                ?? (writerDevice.ProductID == PidWired ? ConnectionKind.Wired : ConnectionKind.Dongle);
            var linkRateHz = decoded?.LinkRateHz;
            var connectionName = connection == ConnectionKind.Dongle ? HidHelpers.GetProductName(writerDevice) : null;

            // CmdVersion answers over the dongle too; the wired bcdDevice is
            // only a fallback for when the device does not implement it. (On
            // this model it is known to NAK, so the fallback is the usual path.)
            _cachedFirmware ??= ReadVersion(writer!, reader ?? writer!, VersionPacket, Legacy17Protocol.CmdVersion, transportForInfo, debug);
            var firmware = _cachedFirmware
                ?? (connection == ConnectionKind.Wired ? HidHelpers.GetFirmwareFromBcd(writerDevice) : null);

            // Radio-only values; the EEPROM lives on the mouse, so gate on the
            // same online check the settings read uses.
            int? signal = null;
            string? dongleFirmware = null;
            if (connection == ConnectionKind.Dongle)
            {
                var online = Exchange(writer!, reader ?? writer!, Cmd03Packet, Legacy17Protocol.CmdOnline, transportForInfo, debug);
                if (online is not null && online.Length > 6 && online[2] == 0x00 && online[6] == 0x01)
                {
                    var rssi = Exchange(writer!, reader ?? writer!, RssiPacket, Legacy17Protocol.CmdRssi, transportForInfo, debug);
                    signal = rssi is null ? null : Legacy17Protocol.ParseSignalPayload(rssi);

                    _cachedDongleFirmware ??= ReadVersion(writer!, reader ?? writer!, DongleVersionPacket, Legacy17Protocol.CmdDongleVersion, transportForInfo, debug);
                    dongleFirmware = _cachedDongleFirmware;
                }
            }

            return ReadBatteryCmd04(writer!, reader ?? writer!, debug, transportForInfo, connection, connectionName, firmware, linkRateHz, signal, dongleFirmware);
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

    /// <summary>
    /// DPI step for this model's sensor: stage values are stored as
    /// <c>(raw + 1) x step</c>. Verified live on an X2 V1 — 07 07 00 47 is
    /// 400 DPI and 0F 0F 00 37 is 800 DPI.
    /// </summary>
    private const int DpiBaseStep = 50;
    // 0xA9 debounce, 0xAB motion sync, 0xAD sleep delay, 0xAF angle snap,
    // 0xB1 ripple control.
    //
    // Index 2 (0x00AD) was previously labelled "led" here and dropped. The
    // Pulsar cMouse notes name it SleepTime, in units of 10 seconds, and list
    // the light-related fields separately (0x00A0 Light, 0x00B3 MovingOffLight)
    // — so it is surfaced as the sleep delay. An earlier probe of this device
    // guessed "LED-off timer" for the same address; both readings agree on the
    // decasecond unit, and only hardware can settle which label is right.
    private const ushort AddrAdvParams = 0x00A9;

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

    // Only the rates this model actually reaches (see PollingRateHzByCode) are
    // offered for writing; 2000/4000/8000 decode on paper but are unreachable
    // on real X2 V1 hardware per the protocol notes.
    private static readonly IReadOnlyDictionary<int, byte> PollingRateCodeByHz = new Dictionary<int, byte>
    {
        [1000] = 0x01,
        [500] = 0x02,
        [250] = 0x04,
        [125] = 0x08,
    };

    private static readonly IReadOnlyList<int> WritablePollingRatesHz = [125, 250, 500, 1000];

    // Only the two LOD codes this backend already decodes round-trip safely
    // (see the switch in ReadSettingsSnapshot); code 0 is left unmapped.
    private static readonly IReadOnlyDictionary<int, byte> LodCodeByMm10 = new Dictionary<int, byte>
    {
        [10] = 0x01,
        [20] = 0x02,
    };

    private static readonly IReadOnlyList<int> WritableLodValuesMm10 = [10, 20];

    // Same decasecond delay register as the Pulsar cMouse V1.31 driver at the
    // identical address (0x00AD) -- reusing its hardware-verified value set.
    private static readonly IReadOnlyList<int> WritableSleepValuesSeconds = [10, 30, 60, 300, 600, 1800];

    // Conservative write range: only the plain low-byte DPI encoding (no
    // exponent bits) that ParseDpiStage already accepts on read. Actual
    // stored DPIs using the exponent bits still read back fine; this just
    // limits what new values can be written until the exponent scaling for
    // this sensor family is confirmed (see Legacy17Protocol.ParseDpiStage).
    private static readonly DeviceValueRange WritableDpiRange = new(50, 12_800, 50);

    public DeviceSettings? ReadSettings(bool debug) => ReadSettingsSnapshot(debug)?.Values;

    /// <summary>
    /// Reads the on-device settings out of the mouse's EEPROM together with the
    /// capabilities needed to render a safe editor. The EEPROM lives on the
    /// mouse rather than the dongle, so this only answers while the wireless
    /// side is awake — an idle X2 V1 sleeps within seconds and every block then
    /// times out, which is reported as "no settings" rather than partial data.
    /// </summary>
    public DeviceSettingsSnapshot? ReadSettingsSnapshot(bool debug)
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
                    if (response is not null
                        && Legacy17Protocol.TryParseEepromResponse(
                            response,
                            Legacy17Protocol.CmdGetEeprom,
                            block,
                            8,
                            out _))
                    {
                        dpi = Legacy17Protocol.ParseDpiStage(response, (stage - 1) % 2, DpiBaseStep);
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
                RippleControl: adv is null ? null : adv[4] == 0x01,
                SleepSeconds: adv?[2] is > 0 and byte sleepUnits ? sleepUnits * 10 : null);

            if (debug)
            {
                System.Diagnostics.Debug.WriteLine($"x2v1 settings: rate={settings.PollingRateHz} dpi={settings.Dpi} stage={settings.DpiStage} lod={settings.LodMm10} debounce={settings.DebounceMs} msync={settings.MotionSync} snap={settings.AngleSnap} ripple={settings.RippleControl}");
            }

            if (settings == new DeviceSettings())
            {
                return null;
            }

            var readable = DeviceSettingField.None;
            if (settings.PollingRateHz is not null) readable |= DeviceSettingField.PollingRate;
            if (settings.DebounceMs is not null) readable |= DeviceSettingField.Debounce;
            if (settings.MotionSync is not null) readable |= DeviceSettingField.MotionSync;
            if (settings.Dpi is not null) readable |= DeviceSettingField.Dpi;
            if (settings.DpiStage is not null) readable |= DeviceSettingField.DpiStage;
            if (settings.LodMm10 is not null) readable |= DeviceSettingField.Lod;
            if (settings.AngleSnap is not null) readable |= DeviceSettingField.AngleSnap;
            if (settings.RippleControl is not null) readable |= DeviceSettingField.RippleControl;
            if (settings.SleepSeconds is not null) readable |= DeviceSettingField.Sleep;

            // Writable is gated per EEPROM sub-block rather than mirroring
            // Readable outright: a field is only ever offered for writing when
            // the block that a rollback would restore it from was itself just
            // read successfully.
            var writable = DeviceSettingField.None;
            if (sys is not null)
            {
                writable |= DeviceSettingField.PollingRate | DeviceSettingField.DpiStage;
                if (settings.Dpi is not null)
                {
                    writable |= DeviceSettingField.Dpi;
                }
            }

            if (adv is not null)
            {
                writable |= DeviceSettingField.Debounce
                    | DeviceSettingField.MotionSync
                    | DeviceSettingField.AngleSnap
                    | DeviceSettingField.RippleControl
                    | DeviceSettingField.Sleep;
            }

            if (lod is not null)
            {
                writable |= DeviceSettingField.Lod;
            }

            var capabilities = new DeviceSettingsCapabilities(
                readable,
                writable,
                WritablePollingRatesHz,
                WritableLodValuesMm10,
                [WritableDpiRange],
                DpiStageCount: sys?[1] ?? 0,
                DebounceMinimumMs: 0,
                DebounceMaximumMs: 15,
                SleepValuesSeconds: WritableSleepValuesSeconds,
                WriteTrust: DeviceSettingsWriteTrust.VendorDerived,
                HasPersistentBackup: false);

            return new DeviceSettingsSnapshot(settings, capabilities);
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

    public bool SupportsSettingsWrite => true;

    /// <summary>
    /// Writes the requested EEPROM fields and verifies each by reading it back,
    /// rolling back to the previous value if any step fails. Uses the same
    /// value/check-pair addresses as <see cref="ReadSettingsSnapshot"/> --
    /// <see cref="CmouseLegacyBackend"/> write-verifies the identical
    /// 0x00A9..0x00B1 register block on the sibling cMouse protocol, but this
    /// has not been exercised on X2 V1 hardware yet, hence
    /// <see cref="DeviceSettingsWriteTrust.VendorDerived"/> above.
    /// </summary>
    public bool ApplySettings(DeviceSettings changes, bool debug)
    {
        if (!ValidateChanges(changes))
        {
            return false;
        }

        var devices = HidHelpers.EnumerateDevices(Vid, d => d.ProductID is PidWireless or PidWired).ToList();
        var writerDevice = devices.FirstOrDefault(d => SafeLength(d.GetMaxFeatureReportLength) == PacketLength);
        var readerDevice = devices.FirstOrDefault(d => SafeLength(d.GetMaxInputReportLength) == PacketLength);
        if (writerDevice is null || readerDevice is null)
        {
            return false;
        }

        HidStream? writer = null;
        HidStream? reader = null;
        try
        {
            if (!writerDevice.TryOpen(out writer) || !readerDevice.TryOpen(out reader))
            {
                return false;
            }

            writer.WriteTimeout = 500;
            reader.ReadTimeout = 250;
            var transport = writerDevice.GetMaxFeatureReportLength() > 0 ? "feature" : "output";

            var online = Exchange(writer, reader, Legacy17Protocol.BuildPacket(OutputReportId, Legacy17Protocol.CmdOnline), Legacy17Protocol.CmdOnline, transport, debug);
            if (online is null || online[2] != 0x00 || online[6] != 0x01)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine("x2v1 settings: mouse offline, refusing write");
                }

                return false;
            }

            var sys = ReadEepromPairs(writer, reader, transport, AddrSysConfig, 3, debug);
            var adv = ReadEepromPairs(writer, reader, transport, AddrAdvParams, 5, debug);
            var lod = ReadEepromPairs(writer, reader, transport, AddrLod, 1, debug);

            var operations = BuildOperations(writer, reader, transport, sys, adv, lod, changes, debug);
            if (operations is null)
            {
                return false;
            }

            var applied = new List<WriteOperation>();
            WriteOperation? attempted = null;
            foreach (var operation in operations)
            {
                if (operation.Desired.SequenceEqual(operation.Original))
                {
                    continue;
                }

                attempted = operation;
                if (!WriteRawBlock(writer, reader, transport, operation.Address, operation.Desired, debug))
                {
                    RollBack(writer, reader, transport, attempted, applied, debug);
                    return false;
                }

                applied.Add(operation);
                attempted = null;
            }

            return true;
        }
        catch
        {
            return false;
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

    private static bool ValidateChanges(DeviceSettings changes)
    {
        if (changes.PollingRateHz is int rate && !PollingRateCodeByHz.ContainsKey(rate))
        {
            return false;
        }

        if (changes.DebounceMs is < 0 or > 15)
        {
            return false;
        }

        if (changes.LodMm10 is int lod && !LodCodeByMm10.ContainsKey(lod))
        {
            return false;
        }

        if (changes.SleepSeconds is int sleep && !WritableSleepValuesSeconds.Contains(sleep))
        {
            return false;
        }

        if (changes.Dpi is int dpi && !WritableDpiRange.Contains(dpi))
        {
            return false;
        }

        if (changes.DpiStage is < 1)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the current value of every requested field's pair/block so each
    /// write can be verified and, on failure partway through, rolled back to
    /// exactly what was on the mouse before this call.
    /// </summary>
    private static List<WriteOperation>? BuildOperations(
        HidStream writer,
        HidStream reader,
        string transport,
        byte[]? sys,
        byte[]? adv,
        byte[]? lod,
        DeviceSettings changes,
        bool debug)
    {
        var operations = new List<WriteOperation>();

        if (changes.PollingRateHz is int rate)
        {
            if (sys is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(
                AddrSysConfig,
                Legacy17Protocol.EncodeCheckedValue(PollingRateCodeByHz[rate]),
                Legacy17Protocol.EncodeCheckedValue(sys[0])));
        }

        if (changes.DebounceMs is int debounce)
        {
            if (adv is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(
                AddrAdvParams,
                Legacy17Protocol.EncodeCheckedValue((byte)debounce),
                Legacy17Protocol.EncodeCheckedValue(adv[0])));
        }

        if (changes.MotionSync is bool motionSync)
        {
            if (adv is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(
                (ushort)(AddrAdvParams + 2),
                Legacy17Protocol.EncodeCheckedValue((byte)(motionSync ? 1 : 0)),
                Legacy17Protocol.EncodeCheckedValue(adv[1])));
        }

        if (changes.SleepSeconds is int sleep)
        {
            if (adv is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(
                (ushort)(AddrAdvParams + 4),
                Legacy17Protocol.EncodeCheckedValue((byte)(sleep / 10)),
                Legacy17Protocol.EncodeCheckedValue(adv[2])));
        }

        if (changes.AngleSnap is bool angleSnap)
        {
            if (adv is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(
                (ushort)(AddrAdvParams + 6),
                Legacy17Protocol.EncodeCheckedValue((byte)(angleSnap ? 1 : 0)),
                Legacy17Protocol.EncodeCheckedValue(adv[3])));
        }

        if (changes.RippleControl is bool ripple)
        {
            if (adv is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(
                (ushort)(AddrAdvParams + 8),
                Legacy17Protocol.EncodeCheckedValue((byte)(ripple ? 1 : 0)),
                Legacy17Protocol.EncodeCheckedValue(adv[4])));
        }

        if (changes.LodMm10 is int lodMm10)
        {
            if (lod is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(
                AddrLod,
                Legacy17Protocol.EncodeCheckedValue(LodCodeByMm10[lodMm10]),
                Legacy17Protocol.EncodeCheckedValue(lod[0])));
        }

        int? targetStage = changes.DpiStage;
        if (targetStage is null && changes.Dpi is not null && sys is not null)
        {
            targetStage = sys[2];
        }

        if (changes.DpiStage is int stage)
        {
            if (sys is null || stage > sys[1])
            {
                return null;
            }

            operations.Add(new WriteOperation(
                (ushort)(AddrSysConfig + 4),
                Legacy17Protocol.EncodeCheckedValue((byte)stage),
                Legacy17Protocol.EncodeCheckedValue(sys[2])));
        }

        if (changes.Dpi is int dpi)
        {
            if (sys is null || targetStage is not int validStage || validStage < 1 || validStage > sys[1])
            {
                return null;
            }

            var stageIndex = validStage - 1;
            var blockAddress = (ushort)(AddrDpiPair1 + ((stageIndex / 2) * 8) + ((stageIndex % 2) * 4));
            var original = ReadRawBlock(writer, reader, transport, blockAddress, 4, debug);
            if (original is null)
            {
                return null;
            }

            // Plain low-byte encoding only -- matches WritableDpiRange, which
            // is capped to the values ParseDpiStage decodes without needing
            // the exponent bits (see DpiExponentScaling.Unknown there).
            var raw = (byte)((dpi / DpiBaseStep) - 1);
            var desired = new byte[4];
            desired[0] = raw;
            desired[1] = raw;
            desired[2] = 0x00;
            desired[3] = Legacy17Protocol.Checksum(desired.AsSpan(0, 3));
            operations.Add(new WriteOperation(blockAddress, desired, original));
        }

        return operations;
    }

    private static byte[]? ReadRawBlock(HidStream writer, HidStream reader, string transport, ushort address, int length, bool debug)
    {
        var response = Exchange(
            writer,
            reader,
            Legacy17Protocol.BuildEepromReadPacket(OutputReportId, address, (byte)length),
            Legacy17Protocol.CmdGetEeprom,
            transport,
            debug);
        return response is not null
            && Legacy17Protocol.TryParseEepromResponse(response, Legacy17Protocol.CmdGetEeprom, address, length, out var data)
            ? data
            : null;
    }

    private static bool WriteRawBlock(HidStream writer, HidStream reader, string transport, ushort address, byte[] desired, bool debug)
    {
        var packet = Legacy17Protocol.BuildEepromWritePacket(OutputReportId, address, desired);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = Exchange(writer, reader, packet, Legacy17Protocol.CmdSetEeprom, transport, debug);
            if (response is null || !Legacy17Protocol.MatchesWriteAck(response, address, desired))
            {
                continue;
            }

            var readBack = ReadRawBlock(writer, reader, transport, address, desired.Length, debug);
            if (readBack is not null && readBack.SequenceEqual(desired))
            {
                return true;
            }
        }

        return false;
    }

    private static void RollBack(
        HidStream writer,
        HidStream reader,
        string transport,
        WriteOperation? attempted,
        IReadOnlyList<WriteOperation> applied,
        bool debug)
    {
        if (attempted is not null)
        {
            TryRestore(writer, reader, transport, attempted, debug);
        }

        for (var i = applied.Count - 1; i >= 0; i--)
        {
            TryRestore(writer, reader, transport, applied[i], debug);
        }
    }

    private static void TryRestore(HidStream writer, HidStream reader, string transport, WriteOperation operation, bool debug)
    {
        if (!WriteRawBlock(writer, reader, transport, operation.Address, operation.Original, debug) && debug)
        {
            System.Diagnostics.Debug.WriteLine($"x2v1 rollback verification failed address=0x{operation.Address:X4}");
        }
    }

    private sealed record WriteOperation(ushort Address, byte[] Desired, byte[] Original);

    private static byte[]? ReadEepromPairs(HidStream writer, HidStream reader, string transport, ushort address, int pairs, bool debug)
    {
        var length = pairs * 2;
        var response = Exchange(
            writer,
            reader,
            Legacy17Protocol.BuildEepromReadPacket(OutputReportId, address, (byte)length),
            Legacy17Protocol.CmdGetEeprom,
            transport,
            debug);
        if (response is null
            || !Legacy17Protocol.TryParseEepromResponse(
                response,
                Legacy17Protocol.CmdGetEeprom,
                address,
                length,
                out var data))
        {
            return null;
        }

        var values = new byte[pairs];
        for (var i = 0; i < pairs; i++)
        {
            var value = data[i * 2];
            var check = data[(i * 2) + 1];
            if (((value + check) & 0xFF) != 0x55)
            {
                return null;
            }

            values[i] = value;
        }

        return values;
    }

    private static byte[]? Exchange(HidStream writer, HidStream reader, byte[] packet, byte expectedCmd, string transport, bool debug)
    {
        try
        {
            var maxLength = reader.Device.GetMaxInputReportLength();
            HidHelpers.DrainInput(reader, 4, maxLength);
            HidHelpers.SendReport(writer, packet, transport);
            System.Threading.Thread.Sleep(15);

            var response = Legacy17Protocol.ReadResponse(
                reader,
                expectedCmd,
                timeoutSeconds: 0.6,
                InputReportIds,
                normalizeReportId: DefaultInputReportId,
                bareReportFilter: static b => b is 0x01 or 0x02 or 0x03 or 0x04 or 0x08 or 0x0E,
                debug,
                maxLength,
                idleSleepMs: 10);
            return response is not null && Legacy17Protocol.HasValidChecksum(response)
                ? response
                : null;
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
    private static (int ModelId, byte ConnectionCode, byte DongleType)? ReadDeviceInfo(
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

            if (payload is null || !Legacy17Protocol.HasValidChecksum(payload))
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
        int? linkRateHz,
        int? signal,
        string? dongleFirmware)
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

        var (battery, charging, voltageMv) = parsed.Value;
        if (debug)
        {
            System.Diagnostics.Debug.WriteLine($"cmd04 raw={battery} charging={charging} mV={voltageMv} signal={signal} data={Convert.ToHexString(payload)}");
        }

        return new DeviceStatus(battery, charging, Name, connection, connectionName, firmware, linkRateHz, voltageMv, signal, dongleFirmware);
    }

    /// <summary>
    /// Reads a firmware version over the wire. Unlike the bcdDevice fallback
    /// this also answers behind the dongle, where the descriptor would only
    /// expose the receiver's own version.
    /// </summary>
    private static string? ReadVersion(HidStream writer, HidStream reader, byte[] packet, byte expectedCmd, string transport, bool debug)
    {
        try
        {
            var response = Exchange(writer, reader, packet, expectedCmd, transport, debug);
            return response is null ? null : Legacy17Protocol.ParseVersionPayload(response);
        }
        catch
        {
            return null;
        }
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
