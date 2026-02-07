using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace PulsarBattery.Services;

internal readonly record struct StartupRegistrationResult(bool Success, bool IsEnabled);
internal readonly record struct StartupRegistrationState(bool IsEnabled, string? CommandLine, string? ExecutablePath);

internal static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulsarBattery";
    private const string StartupArgument = "--background";

    public static bool TryGetIsEnabled(out bool isEnabled)
    {
        if (TryGetRegistrationState(out var state))
        {
            isEnabled = state.IsEnabled;
            return true;
        }

        isEnabled = false;
        return false;
    }

    public static bool TryGetRegistrationState(out StartupRegistrationState state)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var commandLine = key?.GetValue(RunValueName) as string;
            var isEnabled = !string.IsNullOrWhiteSpace(commandLine);
            state = new StartupRegistrationState(
                IsEnabled: isEnabled,
                CommandLine: commandLine,
                ExecutablePath: ExtractExecutablePath(commandLine));
            return true;
        }
        catch
        {
            state = default;
            return false;
        }
    }

    public static bool IsEnabledForExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (!TryGetRegistrationState(out var state) || !state.IsEnabled)
        {
            return false;
        }

        return PathsEqual(state.ExecutablePath, executablePath);
    }

    public static StartupRegistrationResult SetEnabled(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                if (!TryGetIsEnabled(out var current))
                {
                    current = false;
                }

                return new StartupRegistrationResult(Success: false, IsEnabled: current);
            }

            if (enabled)
            {
                key.SetValue(RunValueName, BuildStartupCommandLine(executablePath), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            var isEnabledNow = key.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
            return new StartupRegistrationResult(Success: isEnabledNow == enabled, IsEnabled: isEnabledNow);
        }
        catch
        {
            if (!TryGetIsEnabled(out var current))
            {
                current = !enabled;
            }

            return new StartupRegistrationResult(Success: false, IsEnabled: current);
        }
    }

    private static string BuildStartupCommandLine(string? executablePathOverride)
    {
        var executablePath = executablePathOverride;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Environment.ProcessPath;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "PulsarBattery.exe";
        }

        return $"\"{executablePath}\" {StartupArgument}";
    }

    private static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuoteIndex = trimmed.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                return trimmed.Substring(1, closingQuoteIndex - 1);
            }

            return null;
        }

        var firstSpaceIndex = trimmed.IndexOf(' ');
        return firstSpaceIndex > 0 ? trimmed.Substring(0, firstSpaceIndex) : trimmed;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            var fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
