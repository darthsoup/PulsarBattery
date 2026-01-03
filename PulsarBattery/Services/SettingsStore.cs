using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

internal sealed class SettingsStore
{
    private const string SettingsFileName = "settings.json";
    private const string ApplicationFolderName = "PulsarBattery";
    private const string TemporaryFileExtension = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _fileAccessLock;

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? GetDefaultSettingsFilePath();
        _fileAccessLock = new SemaphoreSlim(1, 1);
    }

    public async Task<AppSettings?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _fileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureDirectoryExists();
            await WriteSettingsFileAtomicallyAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }

    private async Task WriteSettingsFileAtomicallyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var temporaryFilePath = GetTemporaryFilePath();

        await WriteToTemporaryFileAsync(settings, temporaryFilePath, cancellationToken).ConfigureAwait(false);
        ReplaceFileWithTemporary(temporaryFilePath);
    }

    private static async Task WriteToTemporaryFileAsync(AppSettings settings, string temporaryFilePath, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(temporaryFilePath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private void ReplaceFileWithTemporary(string temporaryFilePath)
    {
        File.Copy(temporaryFilePath, _filePath, overwrite: true);
        File.Delete(temporaryFilePath);
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
        return _filePath + TemporaryFileExtension;
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

