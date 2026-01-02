using PulsarBattery.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

internal sealed class HistoryStore
{
    private const string HistoryFileName = "history.json";
    private const string ApplicationFolderName = "PulsarBattery";
    private const string TemporaryFileExtension = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _fileAccessLock;

    public HistoryStore(string? filePath = null)
    {
        _filePath = filePath ?? GetDefaultHistoryFilePath();
        _fileAccessLock = new SemaphoreSlim(1, 1);
    }

    public async Task<IReadOnlyList<BatteryReading>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<BatteryReading>();
            }

            return await DeserializeHistoryFileAsync(cancellationToken);
        }
        catch
        {
            return Array.Empty<BatteryReading>();
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyCollection<BatteryReading> readings, CancellationToken cancellationToken = default)
    {
        await _fileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            EnsureDirectoryExists();
            await WriteHistoryFileAtomicallyAsync(readings, cancellationToken);
        }
        catch
        {
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }

    private async Task<IReadOnlyList<BatteryReading>> DeserializeHistoryFileAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_filePath);
        var readings = await JsonSerializer.DeserializeAsync<List<BatteryReading>>(
            stream, 
            JsonOptions, 
            cancellationToken).ConfigureAwait(false);

        return readings ?? new List<BatteryReading>();
    }

    private async Task WriteHistoryFileAtomicallyAsync(IReadOnlyCollection<BatteryReading> readings, CancellationToken cancellationToken)
    {
        var temporaryFilePath = GetTemporaryFilePath();
        
        await WriteToTemporaryFileAsync(readings, temporaryFilePath, cancellationToken);
        ReplaceFileWithTemporary(temporaryFilePath);
    }

    private async Task WriteToTemporaryFileAsync(IReadOnlyCollection<BatteryReading> readings, string temporaryFilePath, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(temporaryFilePath);
        await JsonSerializer.SerializeAsync(stream, readings, JsonOptions, cancellationToken).ConfigureAwait(false);
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

    private static string GetDefaultHistoryFilePath()
    {
        var appDataDirectory = GetApplicationDataDirectory();
        return Path.Combine(appDataDirectory, HistoryFileName);
    }

    private static string GetApplicationDataDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName);
    }
}