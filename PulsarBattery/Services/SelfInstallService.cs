using PulsarBattery.Tools;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace PulsarBattery.Services;

internal readonly record struct SelfInstallResult(
    bool Success,
    bool RequiresRestart,
    string? ErrorMessage = null);

internal static class SelfInstallService
{
    internal const string CleanupSourceExeArgument = "--cleanup-source-exe";
    private const string InstallFolderName = "PulsarBattery";
    private static int _installInProgress;

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
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return Environment.ProcessPath;
            }

            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsCurrentExecutableBundled()
    {
        try
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly is not null && string.IsNullOrWhiteSpace(entryAssembly.Location))
            {
                return true;
            }
        }
        catch
        {
        }

        var currentExe = GetCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(currentExe);
        var baseName = Path.GetFileNameWithoutExtension(currentExe);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        var sidecarDll = Path.Combine(directory, $"{baseName}.dll");
        var sidecarRuntimeConfig = Path.Combine(directory, $"{baseName}.runtimeconfig.json");
        var sidecarDeps = Path.Combine(directory, $"{baseName}.deps.json");

        return !File.Exists(sidecarDll) &&
               !File.Exists(sidecarRuntimeConfig) &&
               !File.Exists(sidecarDeps);
    }

    public static SelfInstallResult InstallCurrentBuildAndEnableAutostart()
    {
        if (Interlocked.CompareExchange(ref _installInProgress, 1, 0) != 0)
        {
            return new SelfInstallResult(false, false, Loc.T("Installation is already in progress."));
        }

        StartupRegistrationState previousStartupState = default;
        string? installedExePath = null;
        var restoreStartupOnFailure = false;
        try
        {
            var sourceExePath = GetCurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(sourceExePath) || !File.Exists(sourceExePath))
            {
                return new SelfInstallResult(false, false, Loc.T("Current executable path is invalid."));
            }

            if (!IsCurrentExecutableBundled())
            {
                return new SelfInstallResult(false, false, Loc.T("Only bundled executable builds can be installed."));
            }

            installedExePath = Path.Combine(GetInstallDirectory(), Path.GetFileName(sourceExePath));
            var isAlreadyInstalled = PathsEqual(sourceExePath, installedExePath);
            if (!StartupRegistrationService.TryGetRegistrationState(out previousStartupState))
            {
                return new SelfInstallResult(false, false, Loc.T("Could not register autostart in Windows."));
            }

            if (!isAlreadyInstalled)
            {
                CopyExecutableOnly(sourceExePath, installedExePath);
            }

            if (!File.Exists(installedExePath))
            {
                return new SelfInstallResult(false, false, Loc.T("Installed executable was not found after copy."));
            }

            restoreStartupOnFailure = true;
            var autostart = StartupRegistrationService.SetEnabled(enabled: true, executablePath: installedExePath);
            if (!autostart.Success || !autostart.IsEnabled)
            {
                return new SelfInstallResult(false, false, Loc.T("Could not register autostart in Windows."));
            }

            if (!isAlreadyInstalled)
            {
                var launchSuccess = LaunchInstalledExecutable(installedExePath, sourceExePath);
                if (!launchSuccess)
                {
                    return new SelfInstallResult(false, false, Loc.T("Installed app could not be launched."));
                }

                restoreStartupOnFailure = false;
                return new SelfInstallResult(true, true);
            }

            restoreStartupOnFailure = false;
            return new SelfInstallResult(true, false);
        }
        catch (Exception ex)
        {
            return new SelfInstallResult(false, false, ex.Message);
        }
        finally
        {
            if (restoreStartupOnFailure && installedExePath is not null)
            {
                if (!StartupRegistrationService.TryRestoreRegistrationState(previousStartupState, installedExePath))
                {
                    Log.Error(nameof(SelfInstallService), "Previous startup registration could not be restored.");
                }
            }

            Interlocked.Exchange(ref _installInProgress, 0);
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
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return ValidateCleanupSourceExePath(path, GetCurrentExecutablePath(), GetInstallDirectory());
        }

        return null;
    }

    internal static string? ValidateCleanupSourceExePath(string path, string? currentExe, string installDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentExe) ||
                !Path.IsPathFullyQualified(path) ||
                !PathsEqual(currentExe, Path.Combine(installDirectory, Path.GetFileName(currentExe))))
            {
                return null;
            }

            path = Path.GetFullPath(path);
            if (PathsEqual(path, currentExe) ||
                !string.Equals(Path.GetFileName(path), Path.GetFileName(currentExe), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            // A matching name alone must never authorize deleting an unrelated executable.
            using var source = File.OpenRead(path);
            using var installed = File.OpenRead(currentExe);
            if (source.Length != installed.Length ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(installed)))
            {
                return null;
            }

            return path;
        }
        catch
        {
            return null;
        }
    }

    private static bool LaunchInstalledExecutable(string installedExePath, string sourceExePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installedExePath,
            WorkingDirectory = Path.GetDirectoryName(installedExePath) ?? string.Empty,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(CleanupSourceExeArgument);
        startInfo.ArgumentList.Add(sourceExePath);

        using var process = Process.Start(startInfo);
        return process is not null;
    }

    internal static void CopyExecutableOnly(string sourceExePath, string installedExePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installedExePath) ?? GetInstallDirectory());
        var temporaryPath = installedExePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = File.OpenRead(sourceExePath))
            using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            // Keep the previous installation intact until a complete copy is ready to replace it.
            File.Move(temporaryPath, installedExePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase);
    }
}
