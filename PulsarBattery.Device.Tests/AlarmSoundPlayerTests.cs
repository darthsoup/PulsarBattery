using PulsarBattery.Services;

namespace PulsarBattery.Device.Tests;

public sealed class AlarmSoundPlayerTests
{
    private static readonly Uri Sound = new("file:///C:/Sounds/battery.wav");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public void DefaultOutputStartsImmediatelyWithoutEnumeratingDevices()
    {
        var playback = new FakePlayback();
        using var player = new AlarmSoundPlayer(
            () => throw new InvalidOperationException("Default playback must not enumerate devices."),
            uri =>
            {
                Assert.Equal(Sound, uri);
                return playback;
            });

        Assert.True(player.Play(Sound, allOutputs: false));

        Assert.Equal(1, playback.PlayCount);
    }

    [Fact]
    public async Task AllOutputsPlayOncePerEnabledDeviceWithoutExtraDefaultPlayback()
    {
        var headset = new FakePlayback();
        var speakers = new FakePlayback();
        var ignored = new FakePlayback();
        var defaultPlayback = new FakePlayback();
        using var player = Player(
            [Output("HEADSET", ignored, enabled: false), Output("headset", headset),
                Output("HEADSET", ignored), Output("speakers", speakers), Output("", ignored)],
            defaultPlayback);

        Assert.True(player.Play(Sound, allOutputs: true));
        await player.PendingStart.WaitAsync(TestTimeout);

        Assert.Equal(1, headset.PlayCount);
        Assert.Equal(1, speakers.PlayCount);
        Assert.Equal(0, ignored.PlayCount);
        Assert.Equal(0, defaultPlayback.PlayCount);
    }

    [Fact]
    public async Task OneUnavailableOutputDoesNotPreventOtherOutputsFromPlaying()
    {
        var broken = new FakePlayback { ThrowOnPlay = true };
        var speakers = new FakePlayback();
        var defaultPlayback = new FakePlayback();
        using var player = Player(
            [new("unplugged", true, _ => throw new InvalidOperationException("Output removed.")),
                Output("broken", broken), Output("speakers", speakers)],
            defaultPlayback);

        player.Play(Sound, allOutputs: true);
        await player.PendingStart.WaitAsync(TestTimeout);

        Assert.Equal(1, speakers.PlayCount);
        Assert.Equal(1, broken.DisposeCount);
        Assert.Equal(0, defaultPlayback.PlayCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrFailedDeviceEnumerationFallsBackToDefault(bool enumerationFails)
    {
        var defaultPlayback = new FakePlayback();
        using var player = new AlarmSoundPlayer(
            () => enumerationFails
                ? Task.FromException<IReadOnlyList<AlarmAudioOutput>>(new InvalidOperationException("Enumeration failed."))
                : Task.FromResult<IReadOnlyList<AlarmAudioOutput>>([]),
            _ => defaultPlayback);

        player.Play(Sound, allOutputs: true);
        await player.PendingStart.WaitAsync(TestTimeout);

        Assert.Equal(1, defaultPlayback.PlayCount);
    }

    [Fact]
    public async Task OutputsThatAllFailToStartFallBackToDefaultOnce()
    {
        var broken = new FakePlayback { ThrowOnPlay = true };
        var defaultPlayback = new FakePlayback();
        using var player = Player([Output("broken", broken)], defaultPlayback);

        player.Play(Sound, allOutputs: true);
        await player.PendingStart.WaitAsync(TestTimeout);

        Assert.Equal(1, broken.DisposeCount);
        Assert.Equal(1, defaultPlayback.PlayCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposalPreventsLatePlaybackAfterEnumeration(bool dispose)
    {
        var enumeration = new PendingEnumeration();
        var playback = new FakePlayback();
        var defaultPlayback = new FakePlayback();
        using var player = new AlarmSoundPlayer(enumeration.Start, _ => defaultPlayback);
        player.Play(Sound, allOutputs: true);
        var pendingStart = player.PendingStart;
        await enumeration.Started.Task.WaitAsync(TestTimeout);

        if (dispose)
            player.Dispose();
        else
            player.Stop();
        enumeration.Result.SetResult([Output("speakers", playback)]);
        await pendingStart.WaitAsync(TestTimeout);

        Assert.Equal(0, playback.PlayCount);
        Assert.Equal(0, defaultPlayback.PlayCount);
        if (dispose)
            Assert.False(player.Play(Sound, allOutputs: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementIgnoresTheOldEnumerationIncludingItsFallback(bool enumerationFails)
    {
        var enumeration = new PendingEnumeration();
        var oldPlayback = new FakePlayback();
        var replacement = new FakePlayback();
        using var player = new AlarmSoundPlayer(enumeration.Start, _ => replacement);
        player.Play(Sound, allOutputs: true);
        var oldStart = player.PendingStart;
        await enumeration.Started.Task.WaitAsync(TestTimeout);

        Assert.True(player.Play(Sound, allOutputs: false));
        if (enumerationFails)
            enumeration.Result.SetException(new InvalidOperationException("Late enumeration failure."));
        else
            enumeration.Result.SetResult([Output("speakers", oldPlayback)]);
        await oldStart.WaitAsync(TestTimeout);

        Assert.Equal(0, oldPlayback.PlayCount);
        Assert.Equal(1, replacement.PlayCount);
        Assert.Equal(0, replacement.DisposeCount);
    }

    [Fact]
    public async Task CompletionDisposesOnlyTheFinishedOutputAndStopCleansUpTheRest()
    {
        var headset = new FakePlayback();
        var speakers = new FakePlayback();
        using var player = Player([Output("headset", headset), Output("speakers", speakers)]);
        player.Play(Sound, allOutputs: true);
        await player.PendingStart.WaitAsync(TestTimeout);

        headset.Complete();

        Assert.Equal(1, headset.DisposeCount);
        Assert.Equal(0, speakers.DisposeCount);

        player.Stop();
        headset.Complete();

        Assert.Equal(1, headset.DisposeCount);
        Assert.Equal(1, speakers.DisposeCount);
    }

    [Fact]
    public async Task ADisposalFailureDoesNotLeakOtherOutputsOrPreventTheNextAlarm()
    {
        var broken = new FakePlayback { ThrowOnDispose = true };
        var speakers = new FakePlayback();
        var next = new FakePlayback();
        using var player = Player([Output("broken", broken), Output("speakers", speakers)], next);
        player.Play(Sound, allOutputs: true);
        await player.PendingStart.WaitAsync(TestTimeout);

        Assert.True(player.Play(Sound, allOutputs: false));

        Assert.Equal(1, broken.DisposeCount);
        Assert.Equal(1, speakers.DisposeCount);
        Assert.Equal(1, next.PlayCount);
        player.Dispose();
        Assert.Equal(1, next.DisposeCount);
    }

    private static AlarmSoundPlayer Player(
        IReadOnlyList<AlarmAudioOutput> outputs, FakePlayback? defaultPlayback = null)
        => new(() => Task.FromResult(outputs), _ => defaultPlayback ?? new FakePlayback());

    private static AlarmAudioOutput Output(string id, FakePlayback playback, bool enabled = true)
        => new(id, enabled, uri =>
        {
            Assert.Equal(Sound, uri);
            return playback;
        });

    private sealed class PendingEnumeration
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<AlarmAudioOutput>> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AlarmAudioOutput>> Start()
        {
            Started.TrySetResult();
            return Result.Task;
        }
    }

    private sealed class FakePlayback : IAlarmPlayback
    {
        public event EventHandler? Completed;
        public bool ThrowOnPlay { get; init; }
        public bool ThrowOnDispose { get; init; }
        public int PlayCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Play()
        {
            PlayCount++;
            if (ThrowOnPlay)
                throw new InvalidOperationException("Output failed to start.");
        }

        public void Complete() => Completed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            DisposeCount++;
            if (ThrowOnDispose)
                throw new InvalidOperationException("Output failed to dispose.");
        }
    }
}
