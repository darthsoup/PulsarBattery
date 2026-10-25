using System.Collections.Concurrent;
using System.Threading.Channels;
using PulsarBattery.Services;

namespace PulsarBattery.Device.Tests;

public sealed class BatteryMonitorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task LockChecksImmediatelyDespiteLongRegularPollInterval()
    {
        await using var fixture = new MonitorFixture();
        fixture.Monitor.Start();
        var initialRead = await fixture.NextReadAsync();
        initialRead.Complete(Status(25));
        await fixture.WaitForEvaluationAsync(initialRead.Number);

        fixture.Monitor.OnWorkstationLockChanged(true);

        var lockRead = await fixture.NextReadAsync();
        lockRead.Complete(Status(25));
        var alert = await fixture.NextAlertAsync();
        Assert.Equal(30, alert.Threshold);
        Assert.Equal(25, alert.Status.Percentage);
        Assert.Equal(2, fixture.ReadCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task LockDuringReadUsesLockedThresholdWithoutWaitingForAnotherRead(int elapsedSeconds)
    {
        await using var fixture = new MonitorFixture();
        fixture.Monitor.Start();
        var pendingRead = await fixture.NextReadAsync();

        fixture.Monitor.OnWorkstationLockChanged(true);
        fixture.Clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        pendingRead.Complete(Status(25));

        var alert = await fixture.NextAlertAsync();
        Assert.Equal(30, alert.Threshold);
        Assert.Equal(1, fixture.ReadCount);
    }

    [Fact]
    public async Task UnlockDuringReadDoesNotUseThePreviousLockedThreshold()
    {
        await using var fixture = new MonitorFixture();
        fixture.Monitor.OnWorkstationLockChanged(true);
        fixture.Monitor.Start();
        var pendingRead = await fixture.NextReadAsync();

        fixture.Monitor.OnWorkstationLockChanged(false);
        pendingRead.Complete(Status(25));
        await fixture.WaitForEvaluationAsync(pendingRead.Number);
        await fixture.DisposeAsync();

        Assert.Empty(fixture.Alerts);
        Assert.Equal(1, fixture.ReadCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    [InlineData(25, true)]
    public async Task UnavailableInvalidOrChargingReadStopsSoundWithoutAlerting(int? percentage, bool charging)
    {
        await using var fixture = new MonitorFixture();
        fixture.Monitor.OnWorkstationLockChanged(true);
        fixture.Monitor.Start();
        var read = await fixture.NextReadAsync();
        read.Complete(percentage is int value ? Status(value, charging) : null);
        await fixture.WaitForSoundStopAsync();
        await fixture.DisposeAsync();

        Assert.Empty(fixture.Alerts);
        Assert.Equal(1, fixture.SoundStopCount);
    }

    [Fact]
    public async Task LockTransitionKeepsCooldownAndAllowsWarningAfterItExpires()
    {
        await using var fixture = new MonitorFixture();
        fixture.Monitor.Start();
        var firstRead = await fixture.NextReadAsync();
        firstRead.Complete(Status(1));
        Assert.Equal(5, (await fixture.NextAlertAsync()).Threshold);

        fixture.Monitor.OnWorkstationLockChanged(true);
        var lockRead = await fixture.NextReadAsync();
        lockRead.Complete(Status(1));
        await fixture.WaitForEvaluationAsync(lockRead.Number);
        fixture.Monitor.OnWorkstationLockChanged(false);
        Assert.Single(fixture.Alerts);

        fixture.Clock.Advance(TimeSpan.FromMinutes(21));
        fixture.Monitor.OnWorkstationLockChanged(true);
        var laterRead = await fixture.NextReadAsync();
        laterRead.Complete(Status(1));
        Assert.Equal(30, (await fixture.NextAlertAsync()).Threshold);
        await fixture.DisposeAsync();

        Assert.Equal(2, fixture.Alerts.Count);
    }

    [Fact]
    public async Task FailedNotificationCanRetryImmediatelyAndSuccessfulRetryStartsCooldown()
    {
        await using var fixture = new MonitorFixture { AcceptNotifications = false };
        fixture.Monitor.Start();
        var firstRead = await fixture.NextReadAsync();
        firstRead.Complete(Status(1));
        Assert.Equal(5, (await fixture.NextAlertAsync()).Threshold);

        fixture.AcceptNotifications = true;
        fixture.Monitor.OnWorkstationLockChanged(true);
        var retryRead = await fixture.NextReadAsync();
        retryRead.Complete(Status(1));
        Assert.Equal(30, (await fixture.NextAlertAsync()).Threshold);

        fixture.Monitor.OnWorkstationLockChanged(false);
        fixture.Monitor.OnWorkstationLockChanged(true);
        var cooldownRead = await fixture.NextReadAsync();
        cooldownRead.Complete(Status(1));
        await fixture.WaitForEvaluationAsync(cooldownRead.Number);
        await fixture.DisposeAsync();

        Assert.Equal(2, fixture.Alerts.Count);
        Assert.Equal(3, fixture.ReadCount);
    }

    [Fact]
    public async Task DisposalDuringReadSuppressesLateAlertAndIgnoresLaterSessionEvents()
    {
        await using var fixture = new MonitorFixture();
        fixture.Monitor.OnWorkstationLockChanged(true);
        fixture.Monitor.Start();
        var pendingRead = await fixture.NextReadAsync();

        fixture.Monitor.Dispose();
        pendingRead.Complete(Status(1));
        await fixture.Monitor.Completion.WaitAsync(TestTimeout);
        fixture.Monitor.OnWorkstationLockChanged(false);
        fixture.Monitor.OnWorkstationLockChanged(true);

        Assert.Empty(fixture.Alerts);
        Assert.Equal(1, fixture.ReadCount);
        Assert.Throws<ObjectDisposedException>(() => fixture.Monitor.Start());
    }

    [Fact]
    public async Task RepeatedStartKeepsOneWorkerAndDuplicateLocksDoNotDuplicateThePendingAlert()
    {
        await using var fixture = new MonitorFixture();
        fixture.Monitor.Start();
        var worker = fixture.Monitor.Completion;
        var pendingRead = await fixture.NextReadAsync();
        fixture.Monitor.Start();
        Assert.Same(worker, fixture.Monitor.Completion);

        for (var i = 0; i < 20; i++)
        {
            fixture.Monitor.OnWorkstationLockChanged(true);
        }

        pendingRead.Complete(Status(25));
        await fixture.NextAlertAsync();
        await fixture.DisposeAsync();

        Assert.Single(fixture.Alerts);
        Assert.Equal(1, fixture.ReadCount);
    }

    private static PulsarBatteryReader.BatteryStatus Status(int percentage, bool charging = false)
        => new(percentage, charging, "Test mouse");

    private sealed record Alert(PulsarBatteryReader.BatteryStatus Status, int Threshold);

    private sealed class ReadRequest(int number)
    {
        private readonly TaskCompletionSource<PulsarBatteryReader.BatteryStatus?> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Number { get; } = number;
        public void Complete(PulsarBatteryReader.BatteryStatus? status) => _result.TrySetResult(status);
        public PulsarBatteryReader.BatteryStatus? Wait() => _result.Task.GetAwaiter().GetResult();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp = TimeSpan.TicksPerSecond;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    private sealed class MonitorFixture : IAsyncDisposable
    {
        private readonly Channel<ReadRequest> _reads = Channel.CreateUnbounded<ReadRequest>();
        private readonly Channel<int> _evaluations = Channel.CreateUnbounded<int>();
        private readonly Channel<Alert> _alerts = Channel.CreateUnbounded<Alert>();
        private readonly Channel<bool> _soundStops = Channel.CreateUnbounded<bool>();
        private readonly ConcurrentBag<ReadRequest> _allReads = [];
        private readonly AppSettings _settings = new() { PollIntervalMinutes = 120 };
        private int _readCount;
        private int _lastEvaluation;
        private int _soundStopCount;
        private int _disposed;

        public MonitorFixture()
        {
            Monitor = new BatteryMonitor(Read, GetSettings, Notify, StopSound, Clock);
        }

        public BatteryMonitor Monitor { get; }
        public ManualClock Clock { get; } = new();
        public ConcurrentQueue<Alert> Alerts { get; } = new();
        public bool AcceptNotifications { get; set; } = true;
        public int ReadCount => Volatile.Read(ref _readCount);
        public int SoundStopCount => Volatile.Read(ref _soundStopCount);

        public async Task<ReadRequest> NextReadAsync()
            => await _reads.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<Alert> NextAlertAsync()
            => await _alerts.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task WaitForEvaluationAsync(int readNumber)
        {
            int evaluated;
            do
            {
                evaluated = await _evaluations.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
            }
            while (evaluated < readNumber);
            Assert.Equal(readNumber, evaluated);
        }

        public async Task WaitForSoundStopAsync()
        {
            await _soundStops.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Monitor.Dispose();
                foreach (var request in _allReads)
                {
                    request.Complete(null);
                }
            }

            await Monitor.Completion.WaitAsync(TestTimeout);
        }

        private PulsarBatteryReader.BatteryStatus? Read()
        {
            var request = new ReadRequest(Interlocked.Increment(ref _readCount));
            _allReads.Add(request);
            if (Volatile.Read(ref _disposed) != 0)
            {
                request.Complete(null);
            }

            _reads.Writer.TryWrite(request);
            return request.Wait();
        }

        private AppSettings GetSettings()
        {
            var readNumber = ReadCount;
            if (readNumber > _lastEvaluation)
            {
                _lastEvaluation = readNumber;
                _evaluations.Writer.TryWrite(readNumber);
            }

            return _settings;
        }

        private bool Notify(PulsarBatteryReader.BatteryStatus status, int threshold)
        {
            var accepted = AcceptNotifications;
            var alert = new Alert(status, threshold);
            Alerts.Enqueue(alert);
            _alerts.Writer.TryWrite(alert);
            return accepted;
        }

        private void StopSound()
        {
            Interlocked.Increment(ref _soundStopCount);
            _soundStops.Writer.TryWrite(true);
        }
    }
}
