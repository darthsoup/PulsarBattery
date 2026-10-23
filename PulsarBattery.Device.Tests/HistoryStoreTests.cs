using System.Text.Json;
using PulsarBattery.Models;
using PulsarBattery.Services;
using PulsarBattery.Tools;

namespace PulsarBattery.Device.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset SampleTime = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "PulsarBatteryHistoryTests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "history.json");
    private string BackupFilePath => FilePath + ".bak";

    [Fact]
    public async Task MissingHistoryReturnsEmptyWithoutCreatingFiles()
    {
        Assert.Empty(await new HistoryStore(FilePath).LoadAsync());
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task SaveKeepsNewestFiveHundredDistinctReadingsInDescendingOrder()
    {
        var readings = Enumerable.Range(0, 520)
            .Select(index => Reading(SampleTime.AddMinutes(index), index % 101))
            .ToList();
        readings.Add(readings[519] with { Model = "  X2  " });
        readings.Reverse();
        var store = new HistoryStore(FilePath);

        await store.SaveAsync(readings);
        var saved = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(FilePath), CompactJsonContext.Default.ListBatteryReading)!;

        Assert.Equal(500, saved.Count);
        Assert.Equal(
            Enumerable.Range(20, 500).Reverse().Select(index => Reading(SampleTime.AddMinutes(index), index % 101)),
            saved);
        Assert.Equal(saved, await new HistoryStore(FilePath).LoadAsync());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task LoadNormalizesUntrustedFilesBeforeApplyingTheHistoryLimit()
    {
        var readings = Enumerable.Range(0, 520)
            .Select(index => Reading(SampleTime.AddMinutes(index), index % 101))
            .ToList();
        readings.AddRange(Enumerable.Repeat(readings[519], 30));
        readings.Add(null!);
        readings.Add(Reading(SampleTime.AddYears(1), -1));
        await WriteReadingsAsync(FilePath, readings);

        var loaded = await new HistoryStore(FilePath).LoadAsync();

        Assert.Equal(500, loaded.Count);
        Assert.Equal(Reading(SampleTime.AddMinutes(519), 14), loaded[0]);
        Assert.Equal(Reading(SampleTime.AddMinutes(20), 20), loaded[^1]);
        Assert.Equal(loaded.Count, loaded.Distinct().Count());
    }

    [Fact]
    public async Task InvalidReadingsAreDiscardedAndValidBoundaryValuesArePreserved()
    {
        var earliest = Reading(DateTimeOffset.UnixEpoch, 0) with { Model = "  X2  " };
        var full = Reading(SampleTime, 100) with { Model = new string('X', 256), IsCharging = true };
        var readings = new List<BatteryReading>
        {
            null!,
            earliest,
            full,
            Reading(SampleTime, -1),
            Reading(SampleTime, 101),
            Reading(DateTimeOffset.UnixEpoch.AddTicks(-1)),
            Reading(DateTimeOffset.UtcNow.AddDays(2)),
            Reading(SampleTime) with { Model = null! },
            Reading(SampleTime) with { Model = "" },
            Reading(SampleTime) with { Model = " \t\r\n " },
            Reading(SampleTime) with { Model = new string('X', 257) },
        };
        await WriteReadingsAsync(FilePath, readings);

        var loaded = await new HistoryStore(FilePath).LoadAsync();

        Assert.Equal(new[] { full, earliest with { Model = "X2" } }, loaded);
    }

    [Fact]
    public async Task SaveRejectsAnEntirelyInvalidSnapshotWithoutLosingExistingHistory()
    {
        var original = Reading(SampleTime);
        var store = new HistoryStore(FilePath);
        await store.SaveAsync(new[] { original });
        var originalJson = await File.ReadAllTextAsync(FilePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new[]
        {
            Reading(SampleTime, -1),
            null!,
        }));

        Assert.Equal(originalJson, await File.ReadAllTextAsync(FilePath));
        Assert.Equal(original, Assert.Single(await store.LoadAsync()));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveRejectsNullWithoutCreatingFiles()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new HistoryStore(FilePath).SaveAsync(null!));
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"Timestamp\":\"2024-01-01T12:00:00Z\",\"Percentage\":101,\"Model\":\"X2\"}]")]
    public async Task UnusablePrimaryRecoversBackupWithoutRewritingEitherFile(string json)
    {
        var backup = Reading(SampleTime, 75);
        await WriteReadingsAsync(BackupFilePath, new[] { backup });
        await File.WriteAllTextAsync(FilePath, json);
        var backupJson = await File.ReadAllTextAsync(BackupFilePath);

        var loaded = await new HistoryStore(FilePath).LoadAsync();

        Assert.Equal(backup, Assert.Single(loaded));
        Assert.Equal(json, await File.ReadAllTextAsync(FilePath));
        Assert.Equal(backupJson, await File.ReadAllTextAsync(BackupFilePath));
    }

    [Fact]
    public async Task OversizedPrimaryRecoversBackup()
    {
        var backup = Reading(SampleTime, 75);
        await WriteReadingsAsync(BackupFilePath, new[] { backup });
        await File.WriteAllTextAsync(FilePath, "[" + new string(' ', 2 * 1024 * 1024) + "]");

        Assert.Equal(backup, Assert.Single(await new HistoryStore(FilePath).LoadAsync()));
    }

    [Fact]
    public async Task MissingPrimaryRecoversBackup()
    {
        var backup = Reading(SampleTime, 75);
        await WriteReadingsAsync(BackupFilePath, new[] { backup });

        Assert.Equal(backup, Assert.Single(await new HistoryStore(FilePath).LoadAsync()));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task UnusablePrimaryAndBackupReturnEmptyWithoutDeletingEvidence()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "broken primary");
        await File.WriteAllTextAsync(BackupFilePath, "broken backup");

        Assert.Empty(await new HistoryStore(FilePath).LoadAsync());
        Assert.Equal("broken primary", await File.ReadAllTextAsync(FilePath));
        Assert.Equal("broken backup", await File.ReadAllTextAsync(BackupFilePath));
    }

    [Fact]
    public async Task AValidEmptyPrimaryDoesNotRecoverOldBackupReadings()
    {
        await WriteReadingsAsync(BackupFilePath, new[] { Reading(SampleTime) });
        await File.WriteAllTextAsync(FilePath, "[]");

        Assert.Empty(await new HistoryStore(FilePath).LoadAsync());
    }

    [Fact]
    public async Task ReplacingKnownValidHistoryCreatesARecoverablePreviousSnapshot()
    {
        var previous = Reading(SampleTime, 80);
        var current = Reading(SampleTime.AddMinutes(1), 79);
        var store = new HistoryStore(FilePath);
        await store.SaveAsync(new[] { previous });
        await store.SaveAsync(new[] { current });

        Assert.Equal(current, Assert.Single(await new HistoryStore(FilePath).LoadAsync()));
        await File.WriteAllTextAsync(FilePath, "corrupt");
        Assert.Equal(previous, Assert.Single(await new HistoryStore(FilePath).LoadAsync()));
    }

    [Fact]
    public async Task SavingAfterRecoveryPreservesTheHealthyBackup()
    {
        var backup = Reading(SampleTime, 80);
        var current = Reading(SampleTime.AddMinutes(1), 79);
        await WriteReadingsAsync(BackupFilePath, new[] { backup });
        await File.WriteAllTextAsync(FilePath, "corrupt primary");
        var store = new HistoryStore(FilePath);
        Assert.Equal(backup, Assert.Single(await store.LoadAsync()));

        await store.SaveAsync(new[] { current });

        Assert.Equal(current, Assert.Single(await new HistoryStore(FilePath).LoadAsync()));
        await File.WriteAllTextAsync(FilePath, "corrupt again");
        Assert.Equal(backup, Assert.Single(await new HistoryStore(FilePath).LoadAsync()));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ClearingHistoryRemovesBackupSoDeletedReadingsCannotReappear()
    {
        var store = new HistoryStore(FilePath);
        await store.SaveAsync(new[] { Reading(SampleTime, 80) });
        await store.SaveAsync(new[] { Reading(SampleTime.AddMinutes(1), 79) });
        Assert.True(File.Exists(BackupFilePath));

        await store.SaveAsync(Array.Empty<BatteryReading>());

        Assert.Empty(await new HistoryStore(FilePath).LoadAsync());
        Assert.False(File.Exists(BackupFilePath));
        File.Delete(FilePath);
        Assert.Empty(await new HistoryStore(FilePath).LoadAsync());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CanceledSavePreservesPrimaryAndBackupAndLeavesNoTemporaryFiles()
    {
        var store = new HistoryStore(FilePath);
        await store.SaveAsync(new[] { Reading(SampleTime, 80) });
        await store.SaveAsync(new[] { Reading(SampleTime.AddMinutes(1), 79) });
        var primaryJson = await File.ReadAllTextAsync(FilePath);
        var backupJson = await File.ReadAllTextAsync(BackupFilePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(Array.Empty<BatteryReading>(), cancellation.Token));

        Assert.Equal(primaryJson, await File.ReadAllTextAsync(FilePath));
        Assert.Equal(backupJson, await File.ReadAllTextAsync(BackupFilePath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CanceledLoadPropagatesCancellationWithoutCreatingFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HistoryStore(FilePath).LoadAsync(cancellation.Token));

        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task FailedPublicationCleansUpTemporaryFilesAndPreservesExistingBackup()
    {
        Directory.CreateDirectory(FilePath);
        var backup = Reading(SampleTime, 80);
        await WriteReadingsAsync(BackupFilePath, new[] { backup });
        var backupJson = await File.ReadAllTextAsync(BackupFilePath);

        var exception = await Record.ExceptionAsync(() => new HistoryStore(FilePath)
            .SaveAsync(new[] { Reading(SampleTime.AddMinutes(1), 79) }));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(FilePath));
        Assert.Equal(backupJson, await File.ReadAllTextAsync(BackupFilePath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    private static BatteryReading Reading(DateTimeOffset timestamp, int percentage = 50) =>
        new(timestamp, percentage, false, "X2");

    private async Task WriteReadingsAsync(string path, IEnumerable<BatteryReading> readings)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(readings.ToList(), CompactJsonContext.Default.ListBatteryReading);
        await File.WriteAllTextAsync(path, json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
