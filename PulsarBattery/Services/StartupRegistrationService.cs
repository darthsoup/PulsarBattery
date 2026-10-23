using Microsoft.Win32;
using PulsarBattery.Tools;
using System;
using System.IO;

namespace PulsarBattery.Services;

internal readonly record struct StartupRegistrationResult(bool Success, bool IsEnabled);
internal readonly record struct StartupRegistrationState(
    bool IsEnabled,
    string? CommandLine,
    string? ExecutablePath,
    RegistryValueKind ValueKind = RegistryValueKind.String);

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
            var commandLine = key?.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var isEnabled = !string.IsNullOrWhiteSpace(commandLine);
            var valueKind = commandLine is not null ? key!.GetValueKind(RunValueName) : RegistryValueKind.String;
            var expandedCommandLine = valueKind == RegistryValueKind.ExpandString && commandLine is not null
                ? Environment.ExpandEnvironmentVariables(commandLine)
                : commandLine;
            state = new StartupRegistrationState(
                IsEnabled: isEnabled,
                CommandLine: commandLine,
                ExecutablePath: ExtractExecutablePath(expandedCommandLine),
                ValueKind: valueKind);
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

        var commandLine = state.ValueKind == RegistryValueKind.ExpandString && state.CommandLine is not null
            ? Environment.ExpandEnvironmentVariables(state.CommandLine)
            : state.CommandLine;
        return MatchesStartupCommandLine(commandLine, executablePath);
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

            var expectedCommandLine = enabled ? BuildStartupCommandLine(executablePath) : null;
            if (expectedCommandLine is not null)
            {
                key.SetValue(RunValueName, expectedCommandLine, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            var actualCommandLine = key.GetValue(RunValueName) as string;
            var isEnabledNow = !string.IsNullOrWhiteSpace(actualCommandLine);
            var success = enabled
                ? string.Equals(actualCommandLine, expectedCommandLine, StringComparison.Ordinal)
                : actualCommandLine is null;
            return new StartupRegistrationResult(Success: success, IsEnabled: isEnabledNow);
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

    internal static bool TryRestoreRegistrationState(StartupRegistrationState previousState, string installedExePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            var currentCommandLine = key.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.Equals(currentCommandLine, previousState.CommandLine, StringComparison.Ordinal) &&
                (currentCommandLine is null || key.GetValueKind(RunValueName) == previousState.ValueKind))
            {
                return true;
            }

            // Do not undo an independent registration change made after this installation started.
            var installedCommandLine = $"\"{Path.GetFullPath(installedExePath)}\" {StartupArgument}";
            if (!string.Equals(currentCommandLine, installedCommandLine, StringComparison.Ordinal))
            {
                return false;
            }

            if (previousState.CommandLine is null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(RunValueName, previousState.CommandLine, previousState.ValueKind);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(nameof(StartupRegistrationService), ex);
            return false;
        }
    }

    internal static string BuildStartupCommandLine(string? executablePathOverride)
    {
        var executablePath = executablePathOverride;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = SelfInstallService.GetCurrentExecutablePath();
        }

        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath) ||
            executablePath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("A fully qualified executable path is required.", nameof(executablePathOverride));
        }

        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The startup executable was not found.", executablePath);
        }

        return $"\"{executablePath}\" {StartupArgument}";
    }

    internal static bool MatchesStartupCommandLine(string? commandLine, string? executablePath)
    {
        if (!PathsEqual(ExtractExecutablePath(commandLine), executablePath) || commandLine is null)
        {
            return false;
        }

        var trimmed = commandLine.Trim();
        var argumentStart = trimmed.StartsWith('"')
            ? trimmed.IndexOf('"', 1) + 1
            : FindFirstWhitespace(trimmed);
        if (argumentStart <= 0 || argumentStart >= trimmed.Length || !char.IsWhiteSpace(trimmed[argumentStart]))
        {
            return false;
        }

        var argument = trimmed[argumentStart..].Trim();
        return string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ExtractExecutablePath(string? commandLine)
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

        var firstSpaceIndex = FindFirstWhitespace(trimmed);
        return firstSpaceIndex > 0 ? trimmed.Substring(0, firstSpaceIndex) : trimmed;
    }

    private static int FindFirstWhitespace(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right))
            {
                return false;
            }

            var fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
