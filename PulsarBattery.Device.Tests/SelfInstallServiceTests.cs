using PulsarBattery.Services;

namespace PulsarBattery.Device.Tests;

public sealed class SelfInstallServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PulsarBatteryInstallTests-" + Guid.NewGuid().ToString("N"));

    public SelfInstallServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "installed"));
        Directory.CreateDirectory(Path.Combine(_directory, "download"));
    }

    private string InstallDirectory => Path.Combine(_directory, "installed");
    private string InstalledExecutable => Path.Combine(InstallDirectory, "PulsarBattery.exe");
    private string SourceExecutable => Path.Combine(_directory, "download", "PulsarBattery.exe");

    [Fact]
    public void CopyReplacesInstallationOnlyAfterCompleteFileIsReady()
    {
        byte[] source = [1, 2, 3, 4, 5];
        File.WriteAllBytes(SourceExecutable, source);
        File.WriteAllBytes(InstalledExecutable, [9, 8, 7]);

        SelfInstallService.CopyExecutableOnly(SourceExecutable, InstalledExecutable);

        Assert.Equal(source, File.ReadAllBytes(InstalledExecutable));
        Assert.Equal(source, File.ReadAllBytes(SourceExecutable));
        Assert.Empty(Directory.EnumerateFiles(InstallDirectory, "*.tmp"));
    }

    [Fact]
    public void MissingSourcePreservesPreviousInstallationAndCleansTemporaryFile()
    {
        byte[] previous = [9, 8, 7];
        File.WriteAllBytes(InstalledExecutable, previous);

        Assert.Throws<FileNotFoundException>(() => SelfInstallService.CopyExecutableOnly(SourceExecutable, InstalledExecutable));

        Assert.Equal(previous, File.ReadAllBytes(InstalledExecutable));
        Assert.Empty(Directory.EnumerateFiles(InstallDirectory, "*.tmp"));
    }

    [Fact]
    public void LockedDestinationPreservesPreviousInstallationAndCleansTemporaryFile()
    {
        byte[] previous = [9, 8, 7];
        File.WriteAllBytes(SourceExecutable, [1, 2, 3]);
        File.WriteAllBytes(InstalledExecutable, previous);

        using (File.Open(InstalledExecutable, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = Record.Exception(() => SelfInstallService.CopyExecutableOnly(SourceExecutable, InstalledExecutable));
            Assert.True(exception is IOException or UnauthorizedAccessException, $"Expected a sharing/access failure, received: {exception}");
        }

        Assert.Equal(previous, File.ReadAllBytes(InstalledExecutable));
        Assert.Empty(Directory.EnumerateFiles(InstallDirectory, "*.tmp"));
    }

    [Fact]
    public void CleanupAcceptsOnlyIdenticalExternalExecutableFromInstalledProcess()
    {
        byte[] contents = [1, 2, 3, 4];
        File.WriteAllBytes(SourceExecutable, contents);
        File.WriteAllBytes(InstalledExecutable, contents);

        Assert.Equal(SourceExecutable, SelfInstallService.ValidateCleanupSourceExePath(SourceExecutable, InstalledExecutable, InstallDirectory));
        Assert.Null(SelfInstallService.ValidateCleanupSourceExePath(InstalledExecutable, InstalledExecutable, InstallDirectory));
        Assert.Null(SelfInstallService.ValidateCleanupSourceExePath(SourceExecutable, SourceExecutable, InstallDirectory));
        Assert.Null(SelfInstallService.ValidateCleanupSourceExePath("PulsarBattery.exe", InstalledExecutable, InstallDirectory));
    }

    [Fact]
    public void CleanupRejectsSameNameAndLengthWithDifferentContents()
    {
        File.WriteAllBytes(SourceExecutable, [1, 2, 3]);
        File.WriteAllBytes(InstalledExecutable, [3, 2, 1]);

        Assert.Null(SelfInstallService.ValidateCleanupSourceExePath(SourceExecutable, InstalledExecutable, InstallDirectory));
        Assert.True(File.Exists(SourceExecutable));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
