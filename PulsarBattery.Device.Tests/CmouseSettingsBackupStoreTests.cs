using System.Text;
using System.Text.Json;
using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class CmouseSettingsBackupStoreTests
{
    private static readonly DateTimeOffset FixedCreatedUtc =
        new(2026, 8, 9, 12, 34, 56, 789, TimeSpan.Zero);
    private static readonly Guid FixedAppRunId =
        Guid.Parse("d47d95c4-f0eb-4bc5-91dc-af265b7ca99a");

    [Fact]
    public void StandardBackupIsAtomicallyPublishedWithVerifiedManifest()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var backup = CreateBackup();
            var store = CreateStore(root);

            var published = store.Save(backup);

            Assert.Equal(Path.GetFullPath(root), Directory.GetParent(published)!.FullName);
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(root),
                path => Path.GetFileName(path).StartsWith(".tmp-", StringComparison.Ordinal));
            Assert.Equal(backup.CoreSettingsRegion, File.ReadAllBytes(
                Path.Combine(published, CmouseSettingsBackupStore.CoreFileName)));
            Assert.False(File.Exists(Path.Combine(
                published,
                CmouseSettingsBackupStore.ExtendedDpiFileName)));

            var manifest = ReadAndVerifyManifest(published);
            Assert.Equal(1, manifest.FormatVersion);
            Assert.Equal(FixedAppRunId, manifest.AppRunId);
            Assert.Equal(FixedCreatedUtc, manifest.CreatedUtc);
            Assert.Equal(backup.Model, manifest.Model);
            Assert.Equal(backup.VendorId, manifest.VendorId);
            Assert.Equal(backup.ProductId, manifest.ProductId);
            Assert.Equal(backup.Cid, manifest.Cid);
            Assert.Equal(backup.Mid, manifest.Mid);
            Assert.Equal(backup.DevicePath, manifest.DevicePath);
            Assert.Equal("SHA-256", manifest.HashAlgorithm);

            var region = Assert.Single(manifest.Regions);
            Assert.Equal("core-settings", region.Name);
            Assert.Equal(CmouseSettingsBackupStore.CoreFileName, region.FileName);
            Assert.Equal(0, region.StartAddress);
            Assert.Equal(0x100, region.Length);
            Assert.Equal(
                CmouseSettingsBackupStore.ComputeSha256(backup.CoreSettingsRegion),
                region.Sha256);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Paw3955BackupIncludesEntireExtendedDpiRegionAndHash()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var extended = Enumerable.Range(0, CmouseSettingsBackupStore.ExtendedDpiLength)
                .Select(value => (byte)(0x80 + value))
                .ToArray();
            var backup = CreateBackup(
                mid: 101,
                extendedDpiAddress: CmouseSettingsBackupStore.ExtendedDpiAddress,
                extendedDpiRegion: extended);

            var published = CreateStore(root).Save(backup);

            Assert.Equal(extended, File.ReadAllBytes(
                Path.Combine(published, CmouseSettingsBackupStore.ExtendedDpiFileName)));
            var manifest = ReadAndVerifyManifest(published);
            Assert.Equal(2, manifest.Regions.Length);
            var region = Assert.Single(manifest.Regions, item => item.Name == "extended-dpi");
            Assert.Equal(CmouseSettingsBackupStore.ExtendedDpiFileName, region.FileName);
            Assert.Equal(0x1B00, region.StartAddress);
            Assert.Equal(0x24, region.Length);
            Assert.Equal(CmouseSettingsBackupStore.ComputeSha256(extended), region.Sha256);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void IncompleteSnapshotFailsWithoutPublishingAnything()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulsarBatteryBackupTests-" + Guid.NewGuid().ToString("N"));
        var backup = CreateBackup(coreSettingsRegion: new byte[0xFF]);
        var store = CreateStore(root);

        var saved = store.TrySave(backup, out var published, out var error);

        Assert.False(saved);
        Assert.Null(published);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Paw3955SnapshotWithoutExtendedDpiRegionIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulsarBatteryBackupTests-" + Guid.NewGuid().ToString("N"));
        var store = CreateStore(root);

        var saved = store.TrySave(
            CreateBackup(mid: 101),
            out var published,
            out var error);

        Assert.False(saved);
        Assert.Null(published);
        Assert.Contains("PAW3955", error);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void StorageFailureIsReportedAndDoesNotAlterBlockingFile()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            var blockedRoot = Path.Combine(parent, "DeviceBackups");
            File.WriteAllText(blockedRoot, "do-not-overwrite", Encoding.UTF8);
            var store = CreateStore(blockedRoot);

            var saved = store.TrySave(CreateBackup(), out var published, out var error);

            Assert.False(saved);
            Assert.Null(published);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal("do-not-overwrite", File.ReadAllText(blockedRoot, Encoding.UTF8));
        }
        finally
        {
            DeleteTemporaryDirectory(parent);
        }
    }

    [Fact]
    public void BackupGateKeyDistinguishesRepairedMouseOnSharedDevicePath()
    {
        const string path = @"\\?\hid#vid_3710&pid_5406#shared-receiver";

        var original = CmouseLegacyBackend.CreatePersistentBackupKey(path, 0x57, 0x01);
        var sameIdentityDifferentPathCase = CmouseLegacyBackend.CreatePersistentBackupKey(
            path.ToUpperInvariant(),
            0x57,
            0x01);
        var repairedMouse = CmouseLegacyBackend.CreatePersistentBackupKey(path, 0x57, 0x65);

        Assert.Equal(original.ToUpperInvariant(), sameIdentityDifferentPathCase.ToUpperInvariant());
        Assert.NotEqual(original.ToUpperInvariant(), repairedMouse.ToUpperInvariant());
    }

    private static CmouseSettingsBackupManifest ReadAndVerifyManifest(string directory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(
            directory,
            CmouseSettingsBackupStore.ManifestFileName));
        var recordedHash = File.ReadAllText(Path.Combine(
            directory,
            CmouseSettingsBackupStore.ManifestHashFileName)).Trim();
        Assert.Equal(CmouseSettingsBackupStore.ComputeSha256(bytes), recordedHash);

        var manifest = JsonSerializer.Deserialize(
            bytes,
            CmouseSettingsBackupJsonContext.Default.CmouseSettingsBackupManifest);
        return Assert.IsType<CmouseSettingsBackupManifest>(manifest);
    }

    private static CmouseSettingsBackupStore CreateStore(string root) =>
        new(root, () => FixedCreatedUtc, FixedAppRunId);

    private static CmouseSettingsBackup CreateBackup(
        byte mid = 1,
        byte[]? coreSettingsRegion = null,
        ushort? extendedDpiAddress = null,
        byte[]? extendedDpiRegion = null) =>
        new(
            Model: "X2 CrazyLight",
            VendorId: CmouseDeviceCatalog.VendorId,
            ProductId: 0x3414,
            Cid: CmouseDeviceCatalog.Cid,
            Mid: mid,
            DevicePath: @"\\?\hid#vid_3710&pid_3414#test",
            Connection: ConnectionKind.Wired,
            LinkRateHz: 8_000,
            FirmwareVersion: "3.04",
            DongleFirmwareVersion: null,
            CoreSettingsRegion: coreSettingsRegion
                ?? Enumerable.Range(0, 0x100).Select(value => (byte)value).ToArray(),
            ExtendedDpiAddress: extendedDpiAddress,
            ExtendedDpiRegion: extendedDpiRegion);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PulsarBatteryBackupTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
