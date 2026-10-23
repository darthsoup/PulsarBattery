using PulsarBattery.Tools;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

internal sealed class SettingsStore
{
    private const string SettingsFileName = "settings.json";
    private const string ApplicationFolderName = "PulsarBattery";
    private const string TemporaryFileExtension = ".tmp";

    // Concurrent MoveFileEx replacements can fail on Windows, even with unique source files.
    private static readonly SemaphoreSlim FileAccessLock = new(1, 1);
    private readonly string _filePath;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? GetDefaultSettingsFilePath();
    }

    public async Task<AppSettings?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        await FileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync(
                stream,
                SettingsJsonContext.Default.AppSettings,
                cancellationToken).ConfigureAwait(false);
            return settings is null ? null : AppSettings.Sanitize(settings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A corrupt or unreadable file falls back to defaults, but leave a trace.
            Log.Error(nameof(SettingsStore), ex);
            return null;
        }
        finally
        {
            FileAccessLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = AppSettings.Sanitize(settings);
        await FileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureDirectoryExists();
            await WriteSettingsFileAtomicallyAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FileAccessLock.Release();
        }
    }

    private async Task WriteSettingsFileAtomicallyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var temporaryFilePath = GetTemporaryFilePath();

        try
        {
            await WriteToTemporaryFileAsync(settings, temporaryFilePath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceFileWithTemporary(temporaryFilePath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(nameof(SettingsStore), ex);
            }
        }
    }

    private static async Task WriteToTemporaryFileAsync(AppSettings settings, string temporaryFilePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            temporaryFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, options: FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, settings, SettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private void ReplaceFileWithTemporary(string temporaryFilePath)
    {
        // Move is atomic; Copy + Delete could leave a torn settings file on crash.
        File.Move(temporaryFilePath, _filePath, overwrite: true);
    }

    private void EnsureDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private string GetTemporaryFilePath()
    {
        return _filePath + "." + Guid.NewGuid().ToString("N") + TemporaryFileExtension;
    }

    private static string GetDefaultSettingsFilePath()
    {
        var appDataDirectory = GetApplicationDataDirectory();
        return Path.Combine(appDataDirectory, SettingsFileName);
    }

    private static string GetApplicationDataDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName);
    }
}

