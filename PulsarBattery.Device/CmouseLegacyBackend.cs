using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;

namespace PulsarBattery.Device;

/// <summary>
/// Data-driven backend for the CID-87 mice supported by Pulsar cMouse V1.31.
/// USB PID only selects candidates; command 0x01 CID/MID identification is
/// authoritative because several receivers are shared by many models.
/// </summary>
public sealed class CmouseLegacyBackend : IHidBackend
{
    private const byte ReportId = Legacy17Protocol.OutputReportId;
    private const int PacketLength = 17;
    private const int DpiStageCount = 6;

    private const ushort AddrReportRate = 0x0000;
    private const ushort AddrMaxDpiStage = 0x0002;
    private const ushort AddrCurrentDpi = 0x0004;
    private const ushort AddrLod = 0x000A;
    private const ushort AddrDebounce = 0x00A9;
    private const ushort AddrMotionSync = 0x00AB;
    private const ushort AddrSleep = 0x00AD;
    private const ushort AddrAngleSnap = 0x00AF;
    private const ushort AddrRippleControl = 0x00B1;
    private const ushort AddrPerformanceSleep = 0x00B7;

    private static readonly HashSet<byte> InputReportIds = [0x08, 0x09];
    private static readonly IReadOnlyList<int> PollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly IReadOnlyList<int> DiscreteLodValues = [7, 10, 20];
    private static readonly IReadOnlyList<int> ContinuousLodValues =
        Enumerable.Range(7, 11).ToArray();
    private static readonly IReadOnlyList<int> SupportedSleepSeconds = [10, 30, 60, 300, 600, 1800];
    private static readonly HashSet<int> SleepSeconds = [10, 30, 60, 300, 600, 1800];

    private static readonly IReadOnlyDictionary<byte, int> PollingRateHzByCode =
        new Dictionary<byte, int>
        {
            [0x08] = 125,
            [0x04] = 250,
            [0x02] = 500,
            [0x01] = 1000,
            [0x10] = 2000,
            [0x20] = 4000,
            [0x40] = 8000,
        };

    private static readonly IReadOnlyDictionary<int, byte> PollingRateCodeByHz =
        PollingRateHzByCode.ToDictionary(pair => pair.Value, pair => pair.Key);

    private static readonly IReadOnlyDictionary<byte, int> DiscreteLodMm10ByCode =
        new Dictionary<byte, int>
        {
            [0x03] = 7,
            [0x01] = 10,
            [0x02] = 20,
        };

    private static readonly IReadOnlyDictionary<int, byte> DiscreteLodCodeByMm10 =
        DiscreteLodMm10ByCode.ToDictionary(pair => pair.Value, pair => pair.Key);

    private CmouseDeviceProfile? _lastProfile;
    private string? _lastDevicePath;
    private CmouseDeviceProfile? _lastSettingsProfile;
    private string? _lastSettingsDevicePath;
    private string? _cachedFirmware;
    private string? _cachedDongleFirmware;
    private readonly CmouseSettingsBackupStore _settingsBackupStore;
    private readonly object _settingsBackupGate = new();
    private readonly HashSet<string> _persistentlyBackedUpDevices =
        new(StringComparer.OrdinalIgnoreCase);

    public CmouseLegacyBackend()
        : this(new CmouseSettingsBackupStore())
    {
    }

    internal CmouseLegacyBackend(CmouseSettingsBackupStore settingsBackupStore)
    {
        _settingsBackupStore = settingsBackupStore
            ?? throw new ArgumentNullException(nameof(settingsBackupStore));
    }

    public string Name => _lastProfile?.Model.Trim() ?? "Pulsar cMouse";

    public DeviceStatus? ReadBatteryStatus(bool debug)
    {
        DeviceStatus? Read(bool activeSession) => WithSession(
            debug,
            requireOnline: activeSession,
            announceDriverOnline: activeSession,
            allowDeviceSwitch: true,
            context =>
        {
            var batteryFrame = Exchange(
                context,
                Legacy17Protocol.BuildCommandPacket(ReportId, Legacy17Protocol.CmdBattery, []),
                Legacy17Protocol.CmdBattery,
                requireStatusZero: true,
                debug);
            var battery = batteryFrame is null ? null : Legacy17Protocol.ParseBatteryPayload(batteryFrame);
            if (battery is null)
            {
                return null;
            }

            _cachedFirmware ??= ReadVersion(context, Legacy17Protocol.CmdVersion, debug);

            int? signal = null;
            string? dongleFirmware = null;
            if (context.Connection == ConnectionKind.Dongle)
            {
                if (activeSession || IsMouseOnline(context, debug))
                {
                    var signalFrame = Exchange(
                        context,
                        Legacy17Protocol.BuildCommandPacket(ReportId, Legacy17Protocol.CmdRssi, []),
                        Legacy17Protocol.CmdRssi,
                        requireStatusZero: true,
                        debug);
                    signal = signalFrame is null ? null : Legacy17Protocol.ParseSignalPayload(signalFrame);
                }

                _cachedDongleFirmware ??= ReadVersion(context, Legacy17Protocol.CmdDongleVersion, debug);
                dongleFirmware = _cachedDongleFirmware;
            }

            var (percentage, charging, voltageMv) = battery.Value;
            var connectionName = context.Connection == ConnectionKind.Dongle
                ? HidHelpers.GetProductName(context.Device)
                : null;

            return new DeviceStatus(
                percentage,
                charging,
                context.Profile.Model.Trim(),
                context.Connection,
                connectionName,
                _cachedFirmware,
                context.LinkRateHz,
                voltageMv,
                signal,
                dongleFirmware,
                ProtocolModelId: (context.Profile.Cid << 8) | context.Profile.Mid);
        });

        // Battery/status commands usually answer directly through the receiver,
        // even while the mouse sleeps. Avoid toggling driver mode every poll;
        // fall back to a full cMouse session only when the passive read fails.
        return Read(activeSession: false) ?? Read(activeSession: true);
    }

    public DeviceSettings? ReadSettings(bool debug) => ReadSettingsSnapshot(debug)?.Values;

    /// <summary>
    /// Reads the cMouse V1.31 core settings region and, for PAW3955 profiles,
    /// the separate DPI-stage region, in strict ten-byte chunks. Once a device
    /// has been detected this stays pinned to that exact HID path, so the
    /// resulting backup belongs to the same mouse that subsequent writes target.
    /// </summary>
    public CmouseSettingsBackup? ReadSettingsBackup(bool debug)
    {
        return WithSession(
            debug,
            requireOnline: true,
            announceDriverOnline: true,
            allowDeviceSwitch: _lastDevicePath is null,
            context => CreateSettingsBackup(context, debug));
    }

    public DeviceSettingsSnapshot? ReadSettingsSnapshot(bool debug)
    {
        var snapshot = WithSession(
            debug,
            requireOnline: true,
            announceDriverOnline: true,
            allowDeviceSwitch: false,
            context =>
        {
            var core = ReadBlock(context, AddrReportRate, 6, debug);
            var lod = ReadBlock(context, AddrLod, 2, debug);
            var advanced = ReadBlock(context, AddrDebounce, 10, debug);
            if (core is null && lod is null && advanced is null)
            {
                return null;
            }

            var pollingCode = DecodeCheckedValue(core, 0);
            var stageCount = DecodeCheckedValue(core, 2);
            var activeStage = DecodeCheckedValue(core, 4);
            var lodCode = DecodeCheckedValue(lod, 0);
            var debounce = DecodeCheckedValue(advanced, 0);
            var motionSync = DecodeCheckedValue(advanced, 2);
            var sleepUnits = DecodeCheckedValue(advanced, 4);
            var angleSnap = DecodeCheckedValue(advanced, 6);
            var ripple = DecodeCheckedValue(advanced, 8);

            var configuredStageCount = stageCount is >= 1 and <= DpiStageCount
                ? stageCount.Value
                : (byte?)null;
            var validActiveStage = activeStage is >= 1
                && configuredStageCount is byte count
                && activeStage <= count
                    ? activeStage
                    : null;
            var validDebounce = debounce is <= 15 ? debounce : null;
            var decodedSleepSeconds = sleepUnits is > 0
                ? sleepUnits.Value * 10
                : (int?)null;
            var validSleepSeconds = decodedSleepSeconds is int seconds && SleepSeconds.Contains(seconds)
                ? seconds
                : (int?)null;

            int? dpi = null;
            if (validActiveStage is byte stage)
            {
                var dpiAddress = Legacy17DpiCodec.StageAddress(context.Profile.Sensor, stage - 1);
                var dpiBlock = ReadBlock(
                    context,
                    dpiAddress,
                    Legacy17DpiCodec.RecordLength(context.Profile.Sensor),
                    debug);
                if (dpiBlock is not null)
                {
                    dpi = Legacy17DpiCodec.DecodeStage(context.Profile.Sensor, dpiBlock);
                }
            }

            var values = new DeviceSettings(
                PollingRateHz: pollingCode is byte rate && PollingRateHzByCode.TryGetValue(rate, out var hz) ? hz : null,
                DebounceMs: validDebounce,
                MotionSync: DecodeBoolean(motionSync),
                Dpi: dpi,
                DpiStage: validActiveStage,
                LodMm10: lodCode is byte rawLod
                    ? DecodeLodMm10(context.Profile, rawLod)
                    : null,
                AngleSnap: DecodeBoolean(angleSnap),
                RippleControl: DecodeBoolean(ripple),
                SleepSeconds: validSleepSeconds);

            if (values == new DeviceSettings())
            {
                return null;
            }

            var readable = DeviceSettingField.None;
            AddReadable(ref readable, DeviceSettingField.PollingRate, values.PollingRateHz);
            AddReadable(ref readable, DeviceSettingField.Debounce, values.DebounceMs);
            AddReadable(ref readable, DeviceSettingField.MotionSync, values.MotionSync);
            AddReadable(ref readable, DeviceSettingField.Dpi, values.Dpi);
            AddReadable(ref readable, DeviceSettingField.DpiStage, values.DpiStage);
            AddReadable(ref readable, DeviceSettingField.Lod, values.LodMm10);
            AddReadable(ref readable, DeviceSettingField.AngleSnap, values.AngleSnap);
            AddReadable(ref readable, DeviceSettingField.RippleControl, values.RippleControl);
            AddReadable(ref readable, DeviceSettingField.Sleep, values.SleepSeconds);

            var writable = context.Profile.WriteEnabled ? readable : DeviceSettingField.None;
            var capabilities = new DeviceSettingsCapabilities(
                readable,
                writable,
                PollingRates,
                GetLodValues(context.Profile),
                Legacy17DpiCodec.GetRanges(context.Profile.Sensor),
                configuredStageCount ?? DpiStageCount,
                DebounceMinimumMs: 0,
                DebounceMaximumMs: 15,
                SleepValuesSeconds: SupportedSleepSeconds,
                WriteTrust: context.Profile.WriteTrust);

            if (debug)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"cmouse settings model={context.Profile.Model} mid={context.Profile.Mid} rate={values.PollingRateHz} dpi={values.Dpi} stage={values.DpiStage} lod={values.LodMm10} debounce={values.DebounceMs}");
            }

            _lastSettingsDevicePath = context.Device.DevicePath;
            _lastSettingsProfile = context.Profile;
            return new DeviceSettingsSnapshot(values, capabilities);
        });

        if (snapshot is null)
        {
            _lastSettingsDevicePath = null;
            _lastSettingsProfile = null;
        }

        return snapshot;
    }

    public bool SupportsSettingsWrite => _lastSettingsProfile?.WriteEnabled == true;

    public bool ApplySettings(DeviceSettings changes, bool debug)
    {
        var targetPath = _lastSettingsDevicePath;
        var targetProfile = _lastSettingsProfile;
        if (targetPath is null || targetProfile is null)
        {
            return false;
        }

        var result = WithSession(
            debug,
            requireOnline: true,
            announceDriverOnline: true,
            allowDeviceSwitch: false,
            context =>
        {
            if (!context.Profile.WriteEnabled || !ValidateChanges(context.Profile, changes))
            {
                return BoolResult.False;
            }

            var applied = new List<WriteOperation>();
            WriteOperation? attempted = null;
            var holdAttempted = false;
            try
            {
                holdAttempted = true;
                if (!SetHold(context, acquire: true, debug))
                {
                    return BoolResult.False;
                }

                if (!EnsurePersistentSettingsBackup(context, debug))
                {
                    return BoolResult.False;
                }

                var operations = BuildOperations(context, changes, debug);
                if (operations is null)
                {
                    return BoolResult.False;
                }

                foreach (var operation in operations)
                {
                    attempted = operation;
                    if (operation.Original.SequenceEqual(operation.Desired))
                    {
                        attempted = null;
                        continue;
                    }

                    if (!WriteAndVerify(context, operation.Address, operation.Desired, debug))
                    {
                        RollBack(context, attempted, applied, debug);
                        return BoolResult.False;
                    }

                    applied.Add(operation);
                    attempted = null;
                }

                return BoolResult.True;
            }
            catch (Exception ex)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"cmouse write failed; attempting rollback error={ex.Message}");
                }

                RollBack(context, attempted, applied, debug);
                return BoolResult.False;
            }
            finally
            {
                if (holdAttempted)
                {
                    try
                    {
                        if (!SetHold(context, acquire: false, debug) && debug)
                        {
                            System.Diagnostics.Debug.WriteLine("cmouse hold release was not acknowledged");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (debug)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"cmouse hold release failed error={ex.Message}");
                        }
                    }
                }
            }
        },
            requiredDevicePath: targetPath,
            requiredProfile: targetProfile);

        return result?.Value == true;
    }

    private bool EnsurePersistentSettingsBackup(SessionContext context, bool debug)
    {
        lock (_settingsBackupGate)
        {
            var backupKey = CreatePersistentBackupKey(
                context.Device.DevicePath,
                context.Profile.Cid,
                context.Profile.Mid);
            if (_persistentlyBackedUpDevices.Contains(backupKey))
            {
                return true;
            }

            var backup = CreateSettingsBackup(context, debug);
            if (backup is null)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"cmouse write refused: full settings backup read failed path={context.Device.DevicePath}");
                }

                return false;
            }

            if (!_settingsBackupStore.TrySave(backup, out var directory, out var error))
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"cmouse write refused: persistent settings backup failed path={context.Device.DevicePath} error={error}");
                }

                return false;
            }

            _persistentlyBackedUpDevices.Add(backupKey);
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"cmouse pre-write settings backup saved path={context.Device.DevicePath} directory={directory}");
            }

            return true;
        }
    }

    internal static string CreatePersistentBackupKey(string devicePath, byte cid, byte mid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        return $"{cid:X2}:{mid:X2}:{devicePath}";
    }

    private CmouseSettingsBackup? CreateSettingsBackup(SessionContext context, bool debug)
    {
        var core = ReadRegion(context, 0, CmouseSettingsBackupStore.CoreRegionLength, debug);
        if (core is null)
        {
            return null;
        }

        var hasExtendedDpi = context.Profile.Sensor == Legacy17SensorKind.Paw3955;
        var extendedDpi = hasExtendedDpi
            ? ReadRegion(
                context,
                CmouseSettingsBackupStore.ExtendedDpiAddress,
                CmouseSettingsBackupStore.ExtendedDpiLength,
                debug)
            : null;
        if (hasExtendedDpi && extendedDpi is null)
        {
            return null;
        }

        _cachedFirmware ??= ReadVersion(context, Legacy17Protocol.CmdVersion, debug);
        if (context.Connection == ConnectionKind.Dongle)
        {
            _cachedDongleFirmware ??= ReadVersion(
                context,
                Legacy17Protocol.CmdDongleVersion,
                debug);
        }

        return new CmouseSettingsBackup(
            context.Profile.Model.Trim(),
            CmouseDeviceCatalog.VendorId,
            context.Device.ProductID,
            context.Profile.Cid,
            context.Profile.Mid,
            context.Device.DevicePath,
            context.Connection,
            context.LinkRateHz,
            _cachedFirmware,
            _cachedDongleFirmware,
            core,
            hasExtendedDpi ? CmouseSettingsBackupStore.ExtendedDpiAddress : null,
            extendedDpi);
    }

    private T? WithSession<T>(
        bool debug,
        bool requireOnline,
        bool announceDriverOnline,
        bool allowDeviceSwitch,
        Func<SessionContext, T?> action,
        string? requiredDevicePath = null,
        CmouseDeviceProfile? requiredProfile = null)
        where T : class
    {
        var expectedProfile = requiredProfile ?? (allowDeviceSwitch ? null : _lastProfile);
        foreach (var device in EnumerateCandidates(allowDeviceSwitch, requiredDevicePath))
        {
            HidStream? stream = null;
            SessionContext? context = null;
            try
            {
                if (!device.TryOpen(out stream))
                {
                    continue;
                }

                stream.ReadTimeout = 250;
                stream.WriteTimeout = 500;
                context = OpenSession(
                    device,
                    stream,
                    requireOnline,
                    announceDriverOnline,
                    debug);
                if (context is null)
                {
                    continue;
                }

                if (expectedProfile is not null
                    && (context.Profile.Cid != expectedProfile.Cid
                        || context.Profile.Mid != expectedProfile.Mid))
                {
                    if (debug)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"cmouse identity changed on pinned path: expected {expectedProfile.Cid:X2}/{expectedProfile.Mid:X2}, got {context.Profile.Cid:X2}/{context.Profile.Mid:X2}");
                    }

                    continue;
                }

                if (!string.Equals(_lastDevicePath, device.DevicePath, StringComparison.OrdinalIgnoreCase)
                    || _lastProfile?.Mid != context.Profile.Mid)
                {
                    _cachedFirmware = null;
                    _cachedDongleFirmware = null;
                }

                var result = action(context);
                if (result is not null)
                {
                    _lastDevicePath = device.DevicePath;
                    _lastProfile = context.Profile;
                    return result;
                }
            }
            catch (Exception ex)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine($"cmouse candidate failed path={device.DevicePath} error={ex.Message}");
                }
            }
            finally
            {
                if (context?.DriverOnline == true)
                {
                    TrySetDriverOffline(context, debug);
                }

                try
                {
                    stream?.Dispose();
                }
                catch (Exception ex)
                {
                    if (debug)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"cmouse stream cleanup failed error={ex.Message}");
                    }
                }
            }
        }

        return null;
    }

    private IEnumerable<HidDevice> EnumerateCandidates(
        bool allowDeviceSwitch,
        string? requiredDevicePath = null)
    {
        var preferredPath = requiredDevicePath ?? _lastDevicePath;
        var candidates = HidHelpers.EnumerateDevices(
                CmouseDeviceCatalog.VendorId,
                device => CmouseDeviceCatalog.ProductIds.Contains(device.ProductID)
                          && SafeLength(device.GetMaxOutputReportLength) >= PacketLength
                          && SafeLength(device.GetMaxInputReportLength) >= PacketLength)
            .OrderByDescending(device => string.Equals(
                device.DevicePath,
                preferredPath,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(device => PreferredPath(device.DevicePath))
            .ThenBy(device => device.DevicePath);

        // Reads used for discovery may move to another device after an unplug.
        // Settings reads and especially writes must stay on the exact HID path
        // whose status was shown to the user; silently falling through to a
        // second CID-87 mouse would configure the wrong physical device.
        var pinnedPath = requiredDevicePath ?? (!allowDeviceSwitch ? _lastDevicePath : null);
        return pinnedPath is not null
            ? candidates.Where(device => string.Equals(
                device.DevicePath,
                pinnedPath,
                StringComparison.OrdinalIgnoreCase))
            : candidates;
    }

    private static SessionContext? OpenSession(
        HidDevice device,
        HidStream stream,
        bool requireOnline,
        bool announceDriverOnline,
        bool debug)
    {
        var maxLength = Math.Max(PacketLength, SafeLength(device.GetMaxInputReportLength));
        HidHelpers.DrainInput(stream, 6, maxLength);

        Span<byte> challenge = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(challenge);
        var infoFrame = Exchange(
            new SessionContext(device, stream, null!, ConnectionKind.Unknown, null, maxLength, DriverOnline: false),
            Legacy17Protocol.BuildInfoPacket(ReportId, challenge),
            Legacy17Protocol.CmdInfo,
            requireStatusZero: true,
            debug);
        var info = infoFrame is null ? null : Legacy17Protocol.ParseInfoPayload(infoFrame, challenge);
        if (info is null)
        {
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"cmouse identify: no valid response frame={(infoFrame is null ? "-" : Convert.ToHexString(infoFrame.Take(17).ToArray()))}");
            }

            return null;
        }

        var cid = (byte)(info.Value.ModelId >> 8);
        var mid = (byte)info.Value.ModelId;
        if (!CmouseDeviceCatalog.TryGet(cid, mid, out var profile))
        {
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine($"cmouse unknown identity cid={cid} mid={mid}");
            }

            return null;
        }

        var decodedConnection = Legacy17Protocol.DecodeConnection(info.Value.ConnectionCode);
        var context = new SessionContext(
            device,
            stream,
            profile,
            decodedConnection?.Kind ?? ConnectionKind.Unknown,
            decodedConnection?.LinkRateHz,
            maxLength,
            DriverOnline: false);

        if (requireOnline && !WaitUntilOnline(context, debug))
        {
            if (debug) System.Diagnostics.Debug.WriteLine($"cmouse {profile.Model}: online wait failed");
            return null;
        }

        if (announceDriverOnline)
        {
            try
            {
                if (!TrySetDriverOnline(context, online: true, debug))
                {
                    if (debug) System.Diagnostics.Debug.WriteLine($"cmouse {profile.Model}: driver-online ACK failed");
                    TrySetDriverOffline(context, debug);
                    return null;
                }
            }
            catch (Exception ex)
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"cmouse {profile.Model}: driver-online transition failed error={ex.Message}");
                }

                TrySetDriverOffline(context, debug);
                return null;
            }
        }

        return announceDriverOnline ? context with { DriverOnline = true } : context;
    }

    private static bool WaitUntilOnline(SessionContext context, bool debug)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var response = Exchange(
                context,
                Legacy17Protocol.BuildOnlinePacket(ReportId, hold: null),
                Legacy17Protocol.CmdOnline,
                requireStatusZero: false,
                debug,
                attempts: 1);
            if (response is not null && response.Length > 10 && response[6] == 1 && response[10] == 0)
            {
                return true;
            }

            System.Threading.Thread.Sleep(20);
        }

        return false;
    }

    private static bool IsMouseOnline(SessionContext context, bool debug)
    {
        var response = Exchange(
            context,
            Legacy17Protocol.BuildOnlinePacket(ReportId, hold: null),
            Legacy17Protocol.CmdOnline,
            requireStatusZero: false,
            debug,
            attempts: 1);
        return response is not null
            && response.Length > 10
            && response[6] == 1
            && response[10] == 0;
    }

    private static bool TrySetDriverOnline(SessionContext context, bool online, bool debug)
    {
        var response = Exchange(
            context,
            Legacy17Protocol.BuildDriverStatusPacket(ReportId, online),
            Legacy17Protocol.CmdDriverStatus,
            requireStatusZero: false,
            debug,
            attempts: online ? 5 : 1);
        return response is not null || !online;
    }

    private static bool TrySetDriverOffline(SessionContext context, bool debug)
    {
        try
        {
            return TrySetDriverOnline(context, online: false, debug);
        }
        catch (Exception ex)
        {
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"cmouse driver-offline cleanup failed error={ex.Message}");
            }

            return false;
        }
    }

    private static bool SetHold(SessionContext context, bool acquire, bool debug)
    {
        for (var spin = 0; spin < 40; spin++)
        {
            var response = Exchange(
                context,
                Legacy17Protocol.BuildOnlinePacket(ReportId, acquire),
                Legacy17Protocol.CmdOnline,
                requireStatusZero: false,
                debug,
                attempts: 1);
            if (response is not null && response.Length > 10 && response[10] == 0)
            {
                return !acquire || response[6] == 1;
            }

            System.Threading.Thread.Sleep(10);
        }

        return false;
    }

    private static byte[]? ReadBlock(
        SessionContext context,
        ushort address,
        int length,
        bool debug)
    {
        if (length is < 1 or > 10)
        {
            return null;
        }

        var packet = Legacy17Protocol.BuildEepromReadPacket(ReportId, address, (byte)length);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = Exchange(
                context,
                packet,
                Legacy17Protocol.CmdGetEeprom,
                requireStatusZero: true,
                debug,
                attempts: 1);
            if (response is not null
                && Legacy17Protocol.TryParseEepromResponse(
                    response,
                    Legacy17Protocol.CmdGetEeprom,
                    address,
                    (byte)length,
                    out var data))
            {
                return data;
            }
        }

        return null;
    }

    private static byte[]? ReadRegion(
        SessionContext context,
        ushort startAddress,
        int length,
        bool debug)
    {
        var bytes = new byte[length];
        for (var offset = 0; offset < length; offset += 10)
        {
            var blockLength = Math.Min(10, length - offset);
            var block = ReadBlock(
                context,
                checked((ushort)(startAddress + offset)),
                blockLength,
                debug);
            if (block is null)
            {
                return null;
            }

            block.CopyTo(bytes, offset);
        }

        return bytes;
    }

    private static bool WriteAndVerify(
        SessionContext context,
        ushort address,
        byte[] desired,
        bool debug)
    {
        var packet = Legacy17Protocol.BuildEepromWritePacket(ReportId, address, desired);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = Exchange(
                context,
                packet,
                Legacy17Protocol.CmdSetEeprom,
                requireStatusZero: false,
                debug,
                attempts: 1);
            if (response is null || !Legacy17Protocol.MatchesWriteAck(response, address, desired))
            {
                continue;
            }

            var readBack = ReadBlock(context, address, desired.Length, debug);
            if (readBack is not null && readBack.SequenceEqual(desired))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[]? Exchange(
        SessionContext context,
        byte[] packet,
        byte expectedCommand,
        bool requireStatusZero,
        bool debug,
        int attempts = 5)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            HidHelpers.DrainInput(context.Stream, 4, context.MaxInputLength);
            HidHelpers.SendReport(context.Stream, packet, "output");
            System.Threading.Thread.Sleep(8);

            var response = Legacy17Protocol.ReadResponse(
                context.Stream,
                expectedCommand,
                timeoutSeconds: 0.2,
                InputReportIds,
                normalizeReportId: ReportId,
                bareReportFilter: null,
                debug,
                context.MaxInputLength,
                idleSleepMs: 2);
            if (response is null || !Legacy17Protocol.HasValidChecksum(response))
            {
                if (debug)
                {
                    System.Diagnostics.Debug.WriteLine(response is null
                        ? $"cmouse cmd=0x{expectedCommand:X2}: timeout"
                        : $"cmouse cmd=0x{expectedCommand:X2}: bad checksum {Convert.ToHexString(response.Take(17).ToArray())}");
                }
                continue;
            }

            if (requireStatusZero && (response.Length < 3 || response[2] != 0))
            {
                return null;
            }

            return response;
        }

        return null;
    }

    private static string? ReadVersion(SessionContext context, byte command, bool debug)
    {
        var frame = Exchange(
            context,
            Legacy17Protocol.BuildCommandPacket(ReportId, command, []),
            command,
            requireStatusZero: true,
            debug);
        return frame is null ? null : Legacy17Protocol.ParseVersionPayload(frame);
    }

    private static List<WriteOperation>? BuildOperations(
        SessionContext context,
        DeviceSettings changes,
        bool debug)
    {
        byte? configuredStageCount = null;
        if (changes.Dpi is not null || changes.DpiStage is not null)
        {
            configuredStageCount = DecodeCheckedValue(
                ReadBlock(context, AddrMaxDpiStage, 2, debug),
                0);
            if (configuredStageCount is not >= 1 or > DpiStageCount)
            {
                return null;
            }
        }

        var desired = new List<(ushort Address, byte[] Data)>();
        if (changes.PollingRateHz is int pollingRate)
        {
            desired.Add((AddrReportRate, Legacy17Protocol.EncodeCheckedValue(PollingRateCodeByHz[pollingRate])));
        }

        if (changes.DebounceMs is int debounce)
        {
            desired.Add((AddrDebounce, Legacy17Protocol.EncodeCheckedValue((byte)debounce)));
        }

        if (changes.MotionSync is bool motionSync)
        {
            desired.Add((AddrMotionSync, Legacy17Protocol.EncodeCheckedValue((byte)(motionSync ? 1 : 0))));
        }

        if (changes.LodMm10 is int lod)
        {
            desired.Add((
                AddrLod,
                Legacy17Protocol.EncodeCheckedValue(EncodeLodCode(context.Profile, lod))));
        }

        if (changes.AngleSnap is bool angleSnap)
        {
            desired.Add((AddrAngleSnap, Legacy17Protocol.EncodeCheckedValue((byte)(angleSnap ? 1 : 0))));
        }

        if (changes.RippleControl is bool ripple)
        {
            desired.Add((AddrRippleControl, Legacy17Protocol.EncodeCheckedValue((byte)(ripple ? 1 : 0))));
        }

        if (changes.SleepSeconds is int sleep)
        {
            var encoded = Legacy17Protocol.EncodeCheckedValue((byte)(sleep / 10));
            desired.Add((AddrSleep, encoded));
            desired.Add((AddrPerformanceSleep, encoded));
        }

        var targetStage = changes.DpiStage;
        if (targetStage is null && changes.Dpi is not null)
        {
            var currentStageBlock = ReadBlock(context, AddrCurrentDpi, 2, debug);
            targetStage = DecodeCheckedValue(currentStageBlock, 0);
        }

        if (changes.Dpi is int dpi)
        {
            if (targetStage is not >= 1
                || configuredStageCount is not byte stageCount
                || targetStage > stageCount)
            {
                return null;
            }

            var dpiBlock = Legacy17DpiCodec.EncodeStage(context.Profile.Sensor, dpi);
            if (dpiBlock is null)
            {
                return null;
            }

            desired.Add((
                Legacy17DpiCodec.StageAddress(context.Profile.Sensor, targetStage.Value - 1),
                dpiBlock));
        }

        if (changes.DpiStage is int stage)
        {
            if (configuredStageCount is not byte stageCount || stage > stageCount)
            {
                return null;
            }

            desired.Add((AddrCurrentDpi, Legacy17Protocol.EncodeCheckedValue((byte)stage)));
        }

        var operations = new List<WriteOperation>();
        foreach (var (address, data) in desired)
        {
            var original = ReadBlock(context, address, data.Length, debug);
            if (original is null)
            {
                return null;
            }

            operations.Add(new WriteOperation(address, data, original));
        }

        return operations;
    }

    private static bool ValidateChanges(CmouseDeviceProfile profile, DeviceSettings changes)
    {
        if (changes.PollingRateHz is int rate && !PollingRateCodeByHz.ContainsKey(rate))
        {
            return false;
        }

        if (changes.DebounceMs is < 0 or > 15)
        {
            return false;
        }

        if (changes.DpiStage is < 1 or > DpiStageCount)
        {
            return false;
        }

        if (changes.LodMm10 is int lod && !GetLodValues(profile).Contains(lod))
        {
            return false;
        }

        if (changes.SleepSeconds is int sleep && !SleepSeconds.Contains(sleep))
        {
            return false;
        }

        if (changes.Dpi is int dpi && !Legacy17DpiCodec.GetRanges(profile.Sensor).Any(range => range.Contains(dpi)))
        {
            return false;
        }

        return true;
    }

    internal static IReadOnlyList<int> GetLodValues(CmouseDeviceProfile profile) =>
        profile.Sensor == Legacy17SensorKind.Paw3955
            ? ContinuousLodValues
            : DiscreteLodValues;

    internal static int? DecodeLodMm10(CmouseDeviceProfile profile, byte code)
    {
        if (profile.Sensor == Legacy17SensorKind.Paw3955)
        {
            return code is >= 1 and <= 11 ? code + 6 : null;
        }

        return DiscreteLodMm10ByCode.TryGetValue(code, out var mm10) ? mm10 : null;
    }

    internal static byte EncodeLodCode(CmouseDeviceProfile profile, int mm10) =>
        profile.Sensor == Legacy17SensorKind.Paw3955
            ? checked((byte)(mm10 - 6))
            : DiscreteLodCodeByMm10[mm10];

    private static bool RollBack(
        SessionContext context,
        WriteOperation? attempted,
        IReadOnlyList<WriteOperation> applied,
        bool debug)
    {
        var restored = true;
        if (attempted is not null)
        {
            restored &= TryRestore(context, attempted, debug);
        }

        for (var i = applied.Count - 1; i >= 0; i--)
        {
            restored &= TryRestore(context, applied[i], debug);
        }

        return restored;
    }

    private static bool TryRestore(
        SessionContext context,
        WriteOperation operation,
        bool debug)
    {
        try
        {
            var restored = WriteAndVerify(context, operation.Address, operation.Original, debug);
            if (!restored && debug)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"cmouse rollback verification failed address=0x{operation.Address:X4}");
            }

            return restored;
        }
        catch (Exception ex)
        {
            if (debug)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"cmouse rollback failed address=0x{operation.Address:X4} error={ex.Message}");
            }

            return false;
        }
    }

    private static byte? DecodeCheckedValue(IReadOnlyList<byte>? data, int offset)
    {
        if (data is null || offset < 0 || data.Count < offset + 2)
        {
            return null;
        }

        return (byte)((data[offset] + data[offset + 1]) & 0xFF) == 0x55
            ? data[offset]
            : null;
    }

    private static bool? DecodeBoolean(byte? value) => value switch
    {
        0 => false,
        1 => true,
        _ => null,
    };

    private static void AddReadable<T>(
        ref DeviceSettingField fields,
        DeviceSettingField field,
        T? value)
        where T : struct
    {
        if (value is not null)
        {
            fields |= field;
        }
    }

    private static int PreferredPath(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains("mi_01") && lower.Contains("col05"))
        {
            return 2;
        }

        return lower.Contains("mi_01") ? 1 : 0;
    }

    private static int SafeLength(Func<int> get)
    {
        try { return get(); }
        catch { return 0; }
    }

    private sealed record SessionContext(
        HidDevice Device,
        HidStream Stream,
        CmouseDeviceProfile Profile,
        ConnectionKind Connection,
        int? LinkRateHz,
        int MaxInputLength,
        bool DriverOnline);

    private sealed record WriteOperation(ushort Address, byte[] Desired, byte[] Original);

    private sealed record BoolResult(bool Value)
    {
        public static readonly BoolResult True = new(true);
        public static readonly BoolResult False = new(false);
    }
}
