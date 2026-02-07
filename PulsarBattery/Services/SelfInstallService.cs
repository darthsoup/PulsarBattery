using System;
using System.Diagnostics;
using System.IO;

namespace PulsarBattery.Services;

internal readonly record struct SelfInstallResult(
    bool Success,
    bool RequiresRestart,
    string? ErrorMessage = null);

internal static class SelfInstallService
{
    internal const string CleanupSourceExeArgument = "--cleanup-source-exe";
    private const string InstallFolderName = "PulsarBattery";

    public static string GetInstallDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", InstallFolderName);
    }

    public static bool IsRunningFromInstallDirectory()
    {
        var currentExe = GetCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            return false;
        }

        var installedExe = Path.Combine(GetInstallDirectory(), Path.GetFileName(currentExe));
        return PathsEqual(currentExe, installedExe);
    }

    public static string? GetCurrentExecutablePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
    }

    public static SelfInstallResult InstallCurrentBuildAndEnableAutostart()
    {
        var sourceExePath = GetCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(sourceExePath) || !File.Exists(sourceExePath))
        {
            return new SelfInstallResult(false, false, "Current executable path is invalid.");
        }

        var installDirectory = GetInstallDirectory();
        var executableName = Path.GetFileName(sourceExePath);
        var installedExePath = Path.Combine(installDirectory, executableName);

        try
        {
            if (!IsRunningFromInstallDirectory())
            {
                CopyExecutableOnly(sourceExePath, installedExePath);
            }

            if (!File.Exists(installedExePath))
            {
                return new SelfInstallResult(false, false, "Installed executable was not found after copy.");
            }

            var autostart = StartupRegistrationService.SetEnabled(enabled: true, executablePath: installedExePath);
            if (!autostart.Success || !autostart.IsEnabled)
            {
                return new SelfInstallResult(false, false, "Could not register autostart in Windows.");
            }

            if (!IsRunningFromInstallDirectory())
            {
                var launchSuccess = LaunchInstalledExecutable(installedExePath, sourceExePath);
                if (!launchSuccess)
                {
                    return new SelfInstallResult(false, false, "Installed app could not be launched.");
                }

                return new SelfInstallResult(true, true);
            }

            return new SelfInstallResult(true, false);
        }
        catch (Exception ex)
        {
            return new SelfInstallResult(false, false, ex.Message);
        }
    }

    public static string? TryGetCleanupSourceExePath(string[] args)
    {
        if (args.Length < 3)
        {
            return null;
        }

        for (var i = 1; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], CleanupSourceExeArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = args[i + 1];
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        return null;
    }

    private static bool LaunchInstalledExecutable(string installedExePath, string sourceExePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installedExePath,
            WorkingDirectory = Path.GetDirectoryName(installedExePath) ?? string.Empty,
            UseShellExecute = false,
            Arguments = $"{CleanupSourceExeArgument} \"{sourceExePath}\""
        };

        var process = Process.Start(startInfo);
        return process is not null;
    }

    private static void CopyExecutableOnly(string sourceExePath, string installedExePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installedExePath) ?? GetInstallDirectory());
        File.Copy(sourceExePath, installedExePath, overwrite: true);
    }

    private static bool PathsEqual(string left, string right)
    {
        var fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase);
    }
}
