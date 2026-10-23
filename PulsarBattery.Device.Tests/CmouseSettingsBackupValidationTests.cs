using System.Text;
using System.Text.Json;
using PulsarBattery.Device;

namespace PulsarBattery.Device.Tests;

public sealed class CmouseSettingsBackupValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedBackupPassesForItsOriginalIdentity(bool extended)
    {
        using var fixture = new BackupFixture(extended);

        Assert.True(fixture.Validate());
        Assert.True(fixture.Store.TryValidate(
            fixture.Directory,
            fixture.Backup.DevicePath.ToUpperInvariant(),
            fixture.Backup.ProductId,
            fixture.Backup.Cid,
            fixture.Backup.Mid));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("product")]
    [InlineData("cid")]
    [InlineData("mid")]
    public void BackupForAnotherIdentityIsRejected(string mismatch)
    {
        using var fixture = new BackupFixture();
        var backup = fixture.Backup;

        Assert.False(fixture.Store.TryValidate(
            fixture.Directory,
            mismatch == "path" ? backup.DevicePath + "-other" : backup.DevicePath,
            mismatch == "product" ? 0x3415 : backup.ProductId,
            mismatch == "cid" ? (byte)0x58 : backup.Cid,
            mismatch == "mid" ? (byte)2 : backup.Mid));
    }

    [Fact]
    public void BackupFromAnotherAppRunIsRejected()
    {
        using var fixture = new BackupFixture();
        var otherStore = new CmouseSettingsBackupStore(
            fixture.Root, appRunId: Guid.Parse("77777777-7777-7777-7777-777777777777"));

        Assert.False(otherStore.TryValidate(
            fixture.Directory,
            fixture.Backup.DevicePath,
            fixture.Backup.ProductId,
            fixture.Backup.Cid,
            fixture.Backup.Mid));
    }

    [Theory]
    [InlineData(CmouseSettingsBackupStore.ManifestFileName)]
    [InlineData(CmouseSettingsBackupStore.ManifestHashFileName)]
    [InlineData(CmouseSettingsBackupStore.CoreFileName)]
    [InlineData(CmouseSettingsBackupStore.ExtendedDpiFileName)]
    public void MissingRequiredFileInvalidatesBackup(string fileName)
    {
        using var fixture = new BackupFixture(extended: true);
        File.Delete(Path.Combine(fixture.Directory, fileName));

        Assert.False(fixture.Validate());
    }

    [Theory]
    [InlineData(CmouseSettingsBackupStore.ManifestFileName)]
    [InlineData(CmouseSettingsBackupStore.ManifestHashFileName)]
    [InlineData(CmouseSettingsBackupStore.CoreFileName)]
    [InlineData(CmouseSettingsBackupStore.ExtendedDpiFileName)]
    public void TamperedRequiredFileInvalidatesBackup(string fileName)
    {
        using var fixture = new BackupFixture(extended: true);
        var path = Path.Combine(fixture.Directory, fileName);
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        Assert.False(fixture.Validate());
    }

    [Fact]
    public void DeletedBackupDirectoryIsRejected()
    {
        using var fixture = new BackupFixture();
        Directory.Delete(fixture.Directory, recursive: true);

        Assert.False(fixture.Validate());
    }

    [Theory]
    [InlineData("format")]
    [InlineData("algorithm")]
    [InlineData("vendor")]
    [InlineData("path-hash")]
    [InlineData("traversal")]
    [InlineData("wrong-length")]
    [InlineData("wrong-address")]
    [InlineData("null-regions")]
    [InlineData("null-region")]
    [InlineData("missing-extended")]
    [InlineData("duplicate-region")]
    public void HashCorrectButInvalidManifestIsRejected(string mutation)
    {
        using var fixture = new BackupFixture(extended: true);
        var manifest = fixture.ReadManifest();
        var core = manifest.Regions[0];
        var extended = manifest.Regions[1];
        manifest = mutation switch
        {
            "format" => manifest with { FormatVersion = 2 },
            "algorithm" => manifest with { HashAlgorithm = "SHA-1" },
            "vendor" => manifest with { VendorId = 0x1234 },
            "path-hash" => manifest with { DevicePathSha256 = new string('0', 64) },
            "traversal" => manifest with
            {
                Regions = [core with { FileName = "../" + core.FileName }, extended],
            },
            "wrong-length" => manifest with
            {
                Regions = [core with { Length = core.Length - 1 }, extended],
            },
            "wrong-address" => manifest with
            {
                Regions = [core, extended with { StartAddress = extended.StartAddress + 1 }],
            },
            "null-regions" => manifest with { Regions = null! },
            "null-region" => manifest with { Regions = [null!, extended] },
            "missing-extended" => manifest with { Regions = [core] },
            "duplicate-region" => manifest with { Regions = [core, core] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        fixture.WriteManifest(manifest);

        Assert.False(fixture.Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegionWithWrongPhysicalLengthIsRejectedEvenWithMatchingHash(bool extended)
    {
        using var fixture = new BackupFixture(extended: true);
        var manifest = fixture.ReadManifest();
        var regionIndex = extended ? 1 : 0;
        var region = manifest.Regions[regionIndex];
        var path = Path.Combine(fixture.Directory, region.FileName);
        var bytes = File.ReadAllBytes(path).Append((byte)0x42).ToArray();
        File.WriteAllBytes(path, bytes);
        manifest.Regions[regionIndex] = region with
        {
            Sha256 = CmouseSettingsBackupStore.ComputeSha256(bytes),
        };
        fixture.WriteManifest(manifest);

        Assert.False(fixture.Validate());
    }

    [Fact]
    public void OversizedManifestIsRejectedEvenWhenJsonAndHashAreValid()
    {
        using var fixture = new BackupFixture();
        var path = Path.Combine(fixture.Directory, CmouseSettingsBackupStore.ManifestFileName);
        var json = File.ReadAllText(path);
        var paddedBytes = Encoding.UTF8.GetBytes(json + new string(' ', 256 * 1024));
        File.WriteAllBytes(path, paddedBytes);
        fixture.WriteManifestHash(paddedBytes);

        Assert.False(fixture.Validate());
    }

    [Fact]
    public void OversizedHashSidecarIsRejectedEvenWhenTrimmedHashMatches()
    {
        using var fixture = new BackupFixture();
        var path = Path.Combine(fixture.Directory, CmouseSettingsBackupStore.ManifestHashFileName);
        File.AppendAllText(path, new string(' ', 128));

        Assert.False(fixture.Validate());
    }

    private sealed class BackupFixture : IDisposable
    {
        internal string Root { get; }
        internal string Directory { get; }
        internal CmouseSettingsBackupStore Store { get; }
        internal CmouseSettingsBackup Backup { get; }

        internal BackupFixture(bool extended = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "PulsarBatteryValidationTests-" + Guid.NewGuid().ToString("N"));
            Store = new CmouseSettingsBackupStore(
                Root,
                () => new DateTimeOffset(2026, 8, 9, 12, 34, 56, TimeSpan.Zero),
                Guid.Parse("d47d95c4-f0eb-4bc5-91dc-af265b7ca99a"));
            Backup = new CmouseSettingsBackup(
                Model: extended ? "Pulsar Feinmann F01 Noctua Edition" : "X2 CrazyLight",
                VendorId: CmouseDeviceCatalog.VendorId,
                ProductId: 0x3414,
                Cid: CmouseDeviceCatalog.Cid,
                Mid: extended ? (byte)101 : (byte)1,
                DevicePath: @"\\?\hid#vid_3710&pid_3414#validation-test",
                Connection: ConnectionKind.Wired,
                LinkRateHz: 8_000,
                FirmwareVersion: "3.04",
                DongleFirmwareVersion: null,
                CoreSettingsRegion: Enumerable.Range(0, CmouseSettingsBackupStore.CoreRegionLength)
                    .Select(value => (byte)value).ToArray(),
                ExtendedDpiAddress: extended ? CmouseSettingsBackupStore.ExtendedDpiAddress : null,
                ExtendedDpiRegion: extended ? new byte[CmouseSettingsBackupStore.ExtendedDpiLength] : null);
            Directory = Store.Save(Backup);
        }

        internal bool Validate() => Store.TryValidate(
            Directory, Backup.DevicePath, Backup.ProductId, Backup.Cid, Backup.Mid);

        internal CmouseSettingsBackupManifest ReadManifest() =>
            JsonSerializer.Deserialize(
                File.ReadAllBytes(Path.Combine(Directory, CmouseSettingsBackupStore.ManifestFileName)),
                CmouseSettingsBackupJsonContext.Default.CmouseSettingsBackupManifest)!;

        internal void WriteManifest(CmouseSettingsBackupManifest manifest)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest, CmouseSettingsBackupJsonContext.Default.CmouseSettingsBackupManifest);
            File.WriteAllBytes(Path.Combine(Directory, CmouseSettingsBackupStore.ManifestFileName), bytes);
            WriteManifestHash(bytes);
        }

        internal void WriteManifestHash(byte[] bytes) => File.WriteAllText(
            Path.Combine(Directory, CmouseSettingsBackupStore.ManifestHashFileName),
            CmouseSettingsBackupStore.ComputeSha256(bytes) + Environment.NewLine,
            Encoding.ASCII);

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Root))
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
        }
    }
}
