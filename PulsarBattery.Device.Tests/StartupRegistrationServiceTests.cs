using PulsarBattery.Services;

namespace PulsarBattery.Device.Tests;

public sealed class StartupRegistrationServiceTests
{
    [Theory]
    [InlineData("--background", true)]
    [InlineData("--tray", true)]
    [InlineData("--BACKGROUND", true)]
    [InlineData("", false)]
    [InlineData("--background-other", false)]
    [InlineData("--background --cleanup-source-exe victim.exe", false)]
    [InlineData("--cleanup-source-exe victim.exe", false)]
    public void OnlyRecognizesDedicatedBackgroundStartupCommands(string arguments, bool expected)
    {
        var executable = Path.Combine(Path.GetTempPath(), "Folder With Spaces", "PulsarBattery.exe");

        Assert.Equal(expected, StartupRegistrationService.MatchesStartupCommandLine($"\"{executable}\" {arguments}", executable));
    }

    [Fact]
    public void RecognizesTabSeparatedUnquotedCommand()
    {
        const string executable = @"C:\PulsarBattery\PulsarBattery.exe";

        Assert.Equal(executable, StartupRegistrationService.ExtractExecutablePath(executable + "\t--background"));
        Assert.True(StartupRegistrationService.MatchesStartupCommandLine(executable + "\t--background", executable));
    }

    [Fact]
    public void RejectsMissingSeparatorAfterQuotedExecutable()
    {
        var executable = Path.Combine(Path.GetTempPath(), "PulsarBattery.exe");

        Assert.False(StartupRegistrationService.MatchesStartupCommandLine($"\"{executable}\"--background", executable));
    }

    [Fact]
    public void RejectsRelativeAndMismatchedExecutables()
    {
        var executable = Path.Combine(Path.GetTempPath(), "PulsarBattery.exe");

        Assert.False(StartupRegistrationService.MatchesStartupCommandLine("PulsarBattery.exe --background", "PulsarBattery.exe"));
        Assert.False(StartupRegistrationService.MatchesStartupCommandLine($"\"{executable}\" --background", executable + ".other"));
        Assert.Throws<ArgumentException>(() => StartupRegistrationService.BuildStartupCommandLine("PulsarBattery.exe"));
    }

    [Fact]
    public void BuildsQuotedAbsoluteCommandForExistingExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PulsarBattery Startup Tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "PulsarBattery.exe");
            File.WriteAllBytes(executable, [1, 2, 3]);

            Assert.Equal($"\"{executable}\" --background", StartupRegistrationService.BuildStartupCommandLine(executable));
            Assert.Throws<FileNotFoundException>(() => StartupRegistrationService.BuildStartupCommandLine(Path.Combine(directory, "missing.exe")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
