using PulsarBattery.Services;

namespace PulsarBattery.Device.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "PulsarBatterySettingsTests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "settings.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioOutputPreferenceSurvivesReloadWhileSoundIsDisabled(bool allDevices)
    {
        var settings = new AppSettings
        {
            EnableBeeps = false,
            PlayAlarmOnAllAudioDevices = allDevices,
        };
        await new SettingsStore(FilePath).SaveAsync(settings);

        Assert.Equal(settings, await new SettingsStore(FilePath).TryLoadAsync());
    }

    [Fact]
    public async Task OlderSettingsKeepAlarmOnTheDefaultOutput()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath,
            """{"EnableBeeps":true,"LowBatterySoundPath":"C:\\Sounds\\battery.wav"}""");

        var settings = Assert.IsType<AppSettings>(await new SettingsStore(FilePath).TryLoadAsync());

        Assert.False(settings.PlayAlarmOnAllAudioDevices);
        Assert.True(settings.EnableBeeps);
        Assert.Equal(@"C:\Sounds\battery.wav", settings.LowBatterySoundPath);
    }

    [Fact]
    public async Task SavesSanitizedSettingsAndReplacesExistingFile()
    {
        var store = new SettingsStore(FilePath);
        await store.SaveAsync(new AppSettings { Language = "en-US" });

        var settings = new AppSettings
        {
            PollIntervalMinutes = double.NaN,
            LogIntervalMinutes = double.PositiveInfinity,
            AlertCooldownMinutes = double.NegativeInfinity,
            AlertThresholdUnlockedPercent = -10,
            Language = "de-DE",
            EnableBeeps = false,
        };
        await store.SaveAsync(settings);

        Assert.Equal(AppSettings.Sanitize(settings), await store.TryLoadAsync());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task LoadedValuesAreSanitizedBeforeTheyReachCallers()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath,
            """{"PollIntervalMinutes":-10,"LogIntervalMinutes":900,"Language":"DE-de"}""");

        var settings = Assert.IsType<AppSettings>(await new SettingsStore(FilePath).TryLoadAsync());

        Assert.Equal(1, settings.PollIntervalMinutes);
        Assert.Equal(240, settings.LogIntervalMinutes);
        Assert.Equal("de-DE", settings.Language);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"PollIntervalMinutes\":\"invalid\"}")]
    public async Task MalformedSettingsFallBackWithoutOverwritingTheOriginal(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, json);

        Assert.Null(await new SettingsStore(FilePath).TryLoadAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task CanceledSavePreservesExistingSettings()
    {
        var store = new SettingsStore(FilePath);
        var original = new AppSettings { Language = "de-DE" };
        await store.SaveAsync(original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(new AppSettings { Language = "en-US" }, cancellation.Token));

        Assert.Equal(original, await store.TryLoadAsync());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CanceledLoadPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SettingsStore(FilePath).TryLoadAsync(cancellation.Token));
    }

    [Fact]
    public async Task FailedReplacementCleansUpTemporaryFile()
    {
        Directory.CreateDirectory(FilePath);

        var exception = await Record.ExceptionAsync(
            () => new SettingsStore(FilePath).SaveAsync(new AppSettings()));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(FilePath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ReadersSeeCompleteSettingsWhileOtherStoresReplaceTheFile()
    {
        var initial = new AppSettings { PollIntervalMinutes = 1 };
        await new SettingsStore(FilePath).SaveAsync(initial);
        var candidates = Enumerable.Range(1, 12)
            .Select(value => initial with { PollIntervalMinutes = value })
            .ToArray();

        var write = Task.WhenAll(candidates.Select(settings =>
            new SettingsStore(FilePath).SaveAsync(settings)));
        var reads = Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => new SettingsStore(FilePath).TryLoadAsync()));
        await Task.WhenAll(write, reads);

        foreach (var settings in await reads)
        {
            Assert.Contains(settings, candidates);
        }
    }

    [Fact]
    public async Task IndependentWritersUseSeparateTemporaryFiles()
    {
        var candidates = Enumerable.Range(1, 12)
            .Select(value => new AppSettings { PollIntervalMinutes = value })
            .ToArray();

        await Task.WhenAll(candidates.Select(settings =>
            new SettingsStore(FilePath).SaveAsync(settings)));

        var saved = await new SettingsStore(FilePath).TryLoadAsync();
        Assert.Contains(saved, candidates);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
