using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulsarBattery.Device;

internal sealed record CmouseSettingsBackupRegionManifest(
    string Name,
    string FileName,
    int StartAddress,
    int Length,
    string Sha256);

internal sealed record CmouseSettingsBackupManifest(
    int FormatVersion,
    Guid AppRunId,
    DateTimeOffset CreatedUtc,
    string Model,
    int VendorId,
    int ProductId,
    byte Cid,
    byte Mid,
    string DevicePath,
    string DevicePathSha256,
    string Connection,
    int? LinkRateHz,
    string? FirmwareVersion,
    string? DongleFirmwareVersion,
    string HashAlgorithm,
    CmouseSettingsBackupRegionManifest[] Regions);

/// <summary>
/// Persists a cMouse settings snapshot atomically: regions and manifest are written to a private
/// sibling directory, flushed and verified, then made visible with Directory.Move.
/// </summary>
internal sealed class CmouseSettingsBackupStore
{
    internal const int CoreRegionLength = 0x100;
    internal const ushort ExtendedDpiAddress = 0x1B00;
    internal const int ExtendedDpiLength = 0x24;
    internal const string CoreFileName = "core-0000-00ff.bin";
    internal const string ExtendedDpiFileName = "dpi-1b00-1b23.bin";
    internal const string ManifestFileName = "manifest.json";
    internal const string ManifestHashFileName = "manifest.sha256";

    private const string ApplicationDirectoryName = "PulsarBattery";
    private const string BackupsDirectoryName = "DeviceBackups";
    private const string HashAlgorithmName = "SHA-256";

    private static readonly Guid CurrentAppRunId = Guid.NewGuid();

    private readonly string _rootDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Guid _appRunId;

    public CmouseSettingsBackupStore()
        : this(GetDefaultRootDirectory(), () => DateTimeOffset.UtcNow, CurrentAppRunId)
    {
    }

    internal CmouseSettingsBackupStore(
        string rootDirectory,
        Func<DateTimeOffset>? utcNow = null,
        Guid? appRunId = null)
    {
        _rootDirectory = rootDirectory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _appRunId = appRunId ?? CurrentAppRunId;
    }

    internal bool TrySave(
        CmouseSettingsBackup backup,
        out string? publishedDirectory,
        out string? error)
    {
        try
        {
            publishedDirectory = Save(backup);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            publishedDirectory = null;
            error = ex.Message;
            return false;
        }
    }

    internal string Save(CmouseSettingsBackup backup)
    {
        Validate(backup);

        if (string.IsNullOrWhiteSpace(_rootDirectory))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        var rootDirectory = Path.GetFullPath(_rootDirectory);
        Directory.CreateDirectory(rootDirectory);

        var createdUtc = _utcNow().ToUniversalTime();
        var devicePathHash = ComputeSha256(Encoding.UTF8.GetBytes(backup.DevicePath.ToUpperInvariant()));
        var backupId = Guid.NewGuid().ToString("N");
        var directoryName =
            $"{createdUtc:yyyyMMdd'T'HHmmssfff'Z'}-mid{backup.Mid:X2}-{devicePathHash[..16]}-{backupId}";
        var publishedDirectory = Path.Combine(rootDirectory, directoryName);
        var temporaryDirectory = Path.Combine(rootDirectory, $".tmp-{backupId}");
        var published = false;

        try
        {
            Directory.CreateDirectory(temporaryDirectory);

            var regions = new List<CmouseSettingsBackupRegionManifest>(2);
            regions.Add(WriteRegion(
                temporaryDirectory,
                "core-settings",
                CoreFileName,
                startAddress: 0,
                backup.CoreSettingsRegion));

            if (backup.ExtendedDpiRegion is not null)
            {
                regions.Add(WriteRegion(
                    temporaryDirectory,
                    "extended-dpi",
                    ExtendedDpiFileName,
                    backup.ExtendedDpiAddress!.Value,
                    backup.ExtendedDpiRegion));
            }

            var manifest = new CmouseSettingsBackupManifest(
                FormatVersion: 1,
                AppRunId: _appRunId,
                CreatedUtc: createdUtc,
                backup.Model,
                backup.VendorId,
                backup.ProductId,
                backup.Cid,
                backup.Mid,
                backup.DevicePath,
                DevicePathSha256: devicePathHash,
                Connection: backup.Connection.ToString(),
                backup.LinkRateHz,
                backup.FirmwareVersion,
                backup.DongleFirmwareVersion,
                HashAlgorithm: HashAlgorithmName,
                Regions: regions.ToArray());

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                CmouseSettingsBackupJsonContext.Default.CmouseSettingsBackupManifest);
            var manifestHash = ComputeSha256(manifestBytes);
            WriteDurably(Path.Combine(temporaryDirectory, ManifestFileName), manifestBytes);
            WriteDurably(
                Path.Combine(temporaryDirectory, ManifestHashFileName),
                Encoding.ASCII.GetBytes(manifestHash + Environment.NewLine));
            VerifyHash(Path.Combine(temporaryDirectory, ManifestFileName), manifestHash);

            // Both paths are direct children of the same root, so this rename
            // cannot cross volumes and exposes either the whole backup or none.
            Directory.Move(temporaryDirectory, publishedDirectory);
            published = true;
            return publishedDirectory;
        }
        finally
        {
            if (!published && Directory.Exists(temporaryDirectory))
            {
                try
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
                catch
                {
                    // The unpublished directory is intentionally ignored here;
                    // the original exception still makes the device write fail.
                }
            }
        }
    }

    private static CmouseSettingsBackupRegionManifest WriteRegion(
        string directory,
        string name,
        string fileName,
        ushort startAddress,
        byte[] bytes)
    {
        var hash = ComputeSha256(bytes);
        var path = Path.Combine(directory, fileName);
        WriteDurably(path, bytes);
        VerifyHash(path, hash);
        return new CmouseSettingsBackupRegionManifest(
            name,
            fileName,
            startAddress,
            bytes.Length,
            hash);
    }

    private static void WriteDurably(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.WriteThrough,
            });
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void VerifyHash(string path, string expectedHash)
    {
        var actualHash = ComputeSha256(File.ReadAllBytes(path));
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new IOException($"Backup verification failed for {Path.GetFileName(path)}.");
        }
    }

    private static void Validate(CmouseSettingsBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (string.IsNullOrWhiteSpace(backup.Model)
            || string.IsNullOrWhiteSpace(backup.DevicePath))
        {
            throw new InvalidDataException("Backup identity is incomplete.");
        }

        if (backup.VendorId != CmouseDeviceCatalog.VendorId
            || !CmouseDeviceCatalog.ProductIds.Contains(backup.ProductId)
            || !CmouseDeviceCatalog.TryGet(backup.Cid, backup.Mid, out var profile))
        {
            throw new InvalidDataException("Backup identity is not a known cMouse CID-87 profile.");
        }

        if (backup.CoreSettingsRegion is null
            || backup.CoreSettingsRegion.Length != CoreRegionLength)
        {
            throw new InvalidDataException(
                $"The core settings backup must contain exactly {CoreRegionLength} bytes.");
        }

        if ((backup.ExtendedDpiAddress is null) != (backup.ExtendedDpiRegion is null))
        {
            throw new InvalidDataException("The extended DPI address and region must be present together.");
        }

        var requiresExtendedDpi = profile.Sensor == Legacy17SensorKind.Paw3955;
        if (requiresExtendedDpi != (backup.ExtendedDpiRegion is not null))
        {
            throw new InvalidDataException(
                requiresExtendedDpi
                    ? "A PAW3955 backup must include its complete extended DPI region."
                    : "Only PAW3955 backups may include an extended DPI region.");
        }

        if (requiresExtendedDpi
            && (backup.ExtendedDpiAddress != ExtendedDpiAddress
                || backup.ExtendedDpiRegion!.Length != ExtendedDpiLength))
        {
            throw new InvalidDataException(
                $"The extended DPI backup must cover 0x{ExtendedDpiAddress:X4}..0x{ExtendedDpiAddress + ExtendedDpiLength - 1:X4}.");
        }
    }

    internal static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string GetDefaultRootDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationDirectoryName,
            BackupsDirectoryName);
}

[JsonSerializable(typeof(CmouseSettingsBackupManifest))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CmouseSettingsBackupJsonContext : JsonSerializerContext
{
}
