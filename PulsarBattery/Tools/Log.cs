using System;
using System.Diagnostics;
using System.IO;

namespace PulsarBattery.Tools;

/// <summary>
/// Lightweight diagnostics: always writes to the debugger; additionally appends to
/// %LOCALAPPDATA%\PulsarBattery\log.txt when the app is started with --verbose.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _filePath;

    public static void Initialize(bool verboseFileLogging)
    {
        if (!verboseFileLogging)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PulsarBattery");
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, "log.txt");
            Info(nameof(Log), "Verbose file logging enabled");
        }
        catch
        {
            _filePath = null;
        }
    }

    public static void Info(string source, string message) => Write("INFO", source, message);

    public static void Warn(string source, string message) => Write("WARN", source, message);

    public static void Error(string source, Exception exception) => Write("ERROR", source, exception.ToString());

    public static void Error(string source, string message) => Write("ERROR", source, message);

    private static void Write(string level, string source, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {source}: {message}";
        Debug.WriteLine(line);

        var filePath = _filePath;
        if (filePath is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
