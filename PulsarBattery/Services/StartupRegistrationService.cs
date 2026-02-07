using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace PulsarBattery.Services;

internal readonly record struct StartupRegistrationResult(bool Success, bool IsEnabled);

internal static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulsarBattery";
    private const string StartupArgument = "--background";

    public static bool TryGetIsEnabled(out bool isEnabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            isEnabled = key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
            return true;
        }
        catch
        {
            isEnabled = false;
            return false;
        }
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
}
