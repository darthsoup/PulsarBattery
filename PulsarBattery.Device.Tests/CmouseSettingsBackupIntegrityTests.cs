using System.Globalization;
using System.Text.Json;
using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class CmouseSettingsBackupIntegrityTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 8, 9, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public void SaveOwnsRegionBytesBeforeInvokingClock()
    {
        var root = CreateTemporaryPath();
        try
        {
            var originalCore = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            var originalExtended = Enumerable.Repeat((byte)0x75, 36).ToArray();
            var backup = CreateBackup() with
            {
                Mid = 101,
                CoreSettingsRegion = (byte[])originalCore.Clone(),
                ExtendedDpiAddress = CmouseSettingsBackupStore.ExtendedDpiAddress,
                ExtendedDpiRegion = (byte[])originalExtended.Clone(),
            };
            var store = new CmouseSettingsBackupStore(root, () =>
            {
                Array.Fill(backup.CoreSettingsRegion, (byte)0xCC);
                Array.Fill(backup.ExtendedDpiRegion!, (byte)0xDD);
                return CreatedUtc;
            });

            var published = store.Save(backup);

            Assert.Equal(originalCore, File.ReadAllBytes(Path.Combine(
                published, CmouseSettingsBackupStore.CoreFileName)));
            Assert.Equal(originalExtended, File.ReadAllBytes(Path.Combine(
                published, CmouseSettingsBackupStore.ExtendedDpiFileName)));
            var manifest = ReadManifest(published);
            Assert.Equal(CmouseSettingsBackupStore.ComputeSha256(originalCore), manifest.Regions[0].Sha256);
            Assert.Equal(CmouseSettingsBackupStore.ComputeSha256(originalExtended), manifest.Regions[1].Sha256);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void BackupDirectoryTimestampUsesGregorianCalendarAcrossCultures()
    {
        var root = CreateTemporaryPath();
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var store = new CmouseSettingsBackupStore(root, () => CreatedUtc);

            var published = store.Save(CreateBackup());

            Assert.StartsWith("20260809T123456789Z-mid01-", Path.GetFileName(published));
            Assert.Equal(CreatedUtc, ReadManifest(published).CreatedUtc);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void RepeatedSavesAtSameTimestampKeepBothCompleteBackups()
    {
        var root = CreateTemporaryPath();
        try
        {
            var store = new CmouseSettingsBackupStore(root, () => CreatedUtc);
            var backup = CreateBackup();

            var first = store.Save(backup);
            backup.CoreSettingsRegion[0] = 0xA5;
            var second = store.Save(backup);

            Assert.NotEqual(first, second);
            Assert.Equal(2, Directory.EnumerateDirectories(root).Count());
            Assert.Equal((byte)0, File.ReadAllBytes(Path.Combine(first, CmouseSettingsBackupStore.CoreFileName))[0]);
            Assert.Equal((byte)0xA5, File.ReadAllBytes(Path.Combine(second, CmouseSettingsBackupStore.CoreFileName))[0]);
            ReadManifest(first);
            ReadManifest(second);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void InconsistentIdentityOrRegionsNeverCreateBackupDirectory(int scenario)
    {
        var root = CreateTemporaryPath();
        var valid = CreateBackup();
        var backup = scenario switch
        {
            0 => valid with { VendorId = 0 },
            1 => valid with { ProductId = 0 },
            2 => valid with { Cid = 0 },
            3 => valid with { Mid = 0 },
            4 => valid with { ExtendedDpiAddress = CmouseSettingsBackupStore.ExtendedDpiAddress },
            5 => valid with { ExtendedDpiRegion = new byte[36] },
            6 => valid with
            {
                ExtendedDpiAddress = CmouseSettingsBackupStore.ExtendedDpiAddress,
                ExtendedDpiRegion = new byte[36],
            },
            7 => valid with { Mid = 101, ExtendedDpiAddress = 0, ExtendedDpiRegion = new byte[36] },
            _ => valid with
            {
                Mid = 101,
                ExtendedDpiAddress = CmouseSettingsBackupStore.ExtendedDpiAddress,
                ExtendedDpiRegion = new byte[35],
            },
        };
        try
        {
            var saved = new CmouseSettingsBackupStore(root).TrySave(backup, out var published, out var error);

            Assert.False(saved);
            Assert.Null(published);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void UnavailableStorageFailsWithoutPublishing(string root)
    {
        var saved = new CmouseSettingsBackupStore(root).TrySave(
            CreateBackup(), out var published, out var error);

        Assert.False(saved);
        Assert.Null(published);
        Assert.Contains("unavailable", error);
    }

    private static CmouseSettingsBackup CreateBackup() => new(
        Model: "X2 CrazyLight",
        VendorId: CmouseDeviceCatalog.VendorId,
        ProductId: 0x3414,
        Cid: CmouseDeviceCatalog.Cid,
        Mid: 1,
        DevicePath: @"\\?\hid#vid_3710&pid_3414#integrity-test",
        Connection: ConnectionKind.Wired,
        LinkRateHz: 8_000,
        FirmwareVersion: "3.04",
        DongleFirmwareVersion: null,
        CoreSettingsRegion: new byte[CmouseSettingsBackupStore.CoreRegionLength],
        ExtendedDpiAddress: null,
        ExtendedDpiRegion: null);

    private static CmouseSettingsBackupManifest ReadManifest(string directory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, CmouseSettingsBackupStore.ManifestFileName));
        var recordedHash = File.ReadAllText(Path.Combine(
            directory, CmouseSettingsBackupStore.ManifestHashFileName)).Trim();
        Assert.Equal(CmouseSettingsBackupStore.ComputeSha256(bytes), recordedHash);
        return Assert.IsType<CmouseSettingsBackupManifest>(JsonSerializer.Deserialize(
            bytes, CmouseSettingsBackupJsonContext.Default.CmouseSettingsBackupManifest));
    }

    private static string CreateTemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "PulsarBatteryBackupIntegrity-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
