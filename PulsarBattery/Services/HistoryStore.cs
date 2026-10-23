using PulsarBattery.Models;
using PulsarBattery.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

internal sealed class HistoryStore
{
    internal const int MaxEntries = 500;
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;
    private const int MaxModelNameLength = 256;

    private readonly string _filePath;
    private readonly SemaphoreSlim _fileAccessLock = new(1, 1);
    private bool _canBackUpPrimary;

    public HistoryStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulsarBattery", "history.json");
    }

    public async Task<IReadOnlyList<BatteryReading>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _canBackUpPrimary = false;
            var readings = await TryReadHistoryFileAsync(_filePath, cancellationToken).ConfigureAwait(false);
            if (readings is not null)
            {
                _canBackUpPrimary = true;
                return readings;
            }

            var backup = await TryReadHistoryFileAsync(BackupFilePath, cancellationToken).ConfigureAwait(false);
            if (backup is not null)
            {
                Log.Warn(nameof(HistoryStore), "Recovered battery history from backup.");
                return backup;
            }

            return Array.Empty<BatteryReading>();
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyCollection<BatteryReading> readings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readings);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Normalize(readings);
        if (readings.Count > 0 && snapshot.Count == 0)
        {
            throw new InvalidDataException("Battery history contains no valid readings.");
        }

        await _fileAccessLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await WriteHistoryFileAtomicallyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }

    private string BackupFilePath => _filePath + ".bak";

    private async Task<List<BatteryReading>?> TryReadHistoryFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxFileSizeBytes)
            {
                throw new InvalidDataException("Battery history exceeds the maximum file size.");
            }

            var readings = await JsonSerializer.DeserializeAsync(
                stream, CompactJsonContext.Default.ListBatteryReading, cancellationToken).ConfigureAwait(false);
            if (readings is null)
            {
                throw new InvalidDataException("Battery history must contain an array of readings.");
            }

            var normalized = Normalize(readings);
            if (readings.Count > 0 && normalized.Count == 0)
            {
                throw new InvalidDataException("Battery history contains no valid readings.");
            }

            return normalized;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.Error(nameof(HistoryStore), ex);
            return null;
        }
    }

    private static List<BatteryReading> Normalize(IEnumerable<BatteryReading> readings)
    {
        var latestAllowedTimestamp = DateTimeOffset.UtcNow.AddDays(1);
        return readings
            .Where(reading => reading is not null &&
                reading.Percentage is >= 0 and <= 100 &&
                reading.Timestamp >= DateTimeOffset.UnixEpoch &&
                reading.Timestamp <= latestAllowedTimestamp &&
                !string.IsNullOrWhiteSpace(reading.Model) &&
                reading.Model.Length <= MaxModelNameLength)
            .Select(reading => reading with { Model = reading.Model.Trim() })
            .Distinct()
            .OrderByDescending(reading => reading.Timestamp)
            .Take(MaxEntries)
            .ToList();
    }

    private async Task WriteHistoryFileAtomicallyAsync(List<BatteryReading> readings, CancellationToken cancellationToken)
    {
        var temporaryFilePath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryFilePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, readings,
                    CompactJsonContext.Default.ListBatteryReading, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (readings.Count == 0)
            {
                // Clearing history must also remove recovery data so deleted readings cannot reappear.
                File.Delete(BackupFilePath);
                File.Move(temporaryFilePath, _filePath, overwrite: true);
            }
            else if (_canBackUpPrimary && File.Exists(_filePath))
            {
                File.Replace(temporaryFilePath, _filePath, BackupFilePath, ignoreMetadataErrors: true);
            }
            else
            {
                // A failed primary load must never replace a healthy backup with the corrupt file.
                File.Move(temporaryFilePath, _filePath, overwrite: true);
            }

            _canBackUpPrimary = true;
        }
        finally
        {
            try
            {
                File.Delete(temporaryFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(nameof(HistoryStore), ex);
            }
        }
    }
}
