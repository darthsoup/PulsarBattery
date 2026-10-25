using Microsoft.Win32;
using PulsarBattery.Tools;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

public sealed class BatteryMonitor : IDisposable
{
    private const int PostLockMonitoringWindowSeconds = 10;
    private const int MonitoringLoopDelaySeconds = 5;
    private const double MinimumPollIntervalMinutes = 0.1;

    private readonly Func<PulsarBatteryReader.BatteryStatus?> _readBatteryStatus;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<PulsarBatteryReader.BatteryStatus, int, bool> _notifyLowBattery;
    private readonly Action _stopLowBatterySound;
    private readonly TimeProvider _timeProvider;
    private readonly bool _observeSessionEvents;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _sessionChanged = new(0, 1);
    private readonly object _lifecycleGate = new();
    private Task? _monitorTask;
    private bool _disposed;
    private bool _sessionEventsRegistered;
    private bool _isWorkstationLocked;
    private bool _lockCheckPending;
    private long _sessionVersion;
    private long? _lockedTimestamp;
    private long? _lastLowBatteryAlertTimestamp;

    public BatteryMonitor()
        : this(CreateBatteryReader(), () => AppSettingsService.Current,
            (status, threshold) => NotificationHelper.NotifyLowBattery(status.Percentage, threshold, status.Model),
            NotificationHelper.StopLowBatterySound, observeSessionEvents: true)
    {
    }

    internal BatteryMonitor(
        Func<PulsarBatteryReader.BatteryStatus?> readBatteryStatus,
        Func<AppSettings> getSettings,
        Func<PulsarBatteryReader.BatteryStatus, int, bool> notifyLowBattery,
        Action stopLowBatterySound,
        TimeProvider? timeProvider = null,
        bool observeSessionEvents = false)
    {
        _readBatteryStatus = readBatteryStatus;
        _getSettings = getSettings;
        _notifyLowBattery = notifyLowBattery;
        _stopLowBatterySound = stopLowBatterySound;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observeSessionEvents = observeSessionEvents;
    }

    internal Task Completion => _monitorTask ?? Task.CompletedTask;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitorTask is not null)
            {
                return;
            }

            var cancellationToken = _cancellationTokenSource.Token;
            _monitorTask = Task.Run(() => MonitorBatteryAsync(cancellationToken));
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_sessionEventsRegistered)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _sessionEventsRegistered = false;
            }

            _cancellationTokenSource.Cancel();
            if (_monitorTask is null)
            {
                DisposeWaitResources();
            }
            else
            {
                // A synchronous HID read can outlive shutdown; release wait resources after the loop exits.
                _ = _monitorTask.ContinueWith(
                    _ => DisposeWaitResources(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private void DisposeWaitResources()
    {
        _sessionChanged.Dispose();
        _cancellationTokenSource.Dispose();
    }

    private async Task MonitorBatteryAsync(CancellationToken cancellationToken)
    {
        if (_observeSessionEvents)
        {
            SubscribeToSessionChanges();
        }

        long? lastCheckTimestamp = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // SystemEvents can miss WTS registration during Windows startup; reconcile on the normal tick.
                if (_observeSessionEvents)
                {
                    RefreshSessionState();
                }

                bool shouldCheck;
                lock (_lifecycleGate)
                {
                    shouldCheck = _isWorkstationLocked
                        ? _lockCheckPending || (_lockedTimestamp is long lockedAt &&
                            _timeProvider.GetElapsedTime(lockedAt) < TimeSpan.FromSeconds(PostLockMonitoringWindowSeconds))
                        : lastCheckTimestamp is null ||
                            _timeProvider.GetElapsedTime(lastCheckTimestamp.Value) >= GetCurrentPollInterval();
                }

                if (shouldCheck)
                {
                    CheckBatteryStatus(cancellationToken);
                    lastCheckTimestamp = _timeProvider.GetTimestamp();
                }

                await _sessionChanged.WaitAsync(TimeSpan.FromSeconds(MonitoringLoopDelaySeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(nameof(BatteryMonitor), $"Battery monitor iteration failed: {ex}");
                try
                {
                    await _sessionChanged.WaitAsync(TimeSpan.FromSeconds(MonitoringLoopDelaySeconds), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void SubscribeToSessionChanges()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Subscribe on the worker so session delivery does not wait for the UI dispatcher.
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _sessionEventsRegistered = true;
            }
            catch (Exception ex)
            {
                Log.Error(nameof(BatteryMonitor), $"Session event registration failed: {ex}");
            }
        }

        RefreshSessionState();
    }

    private void RefreshSessionState()
    {
        long version;
        lock (_lifecycleGate)
        {
            version = _sessionVersion;
        }

        var isLocked = ReadSessionLockState();
        lock (_lifecycleGate)
        {
            if (!_disposed && version == _sessionVersion && isLocked is bool locked)
            {
                OnWorkstationLockChanged(locked);
            }
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
        {
            OnWorkstationLockChanged(args.Reason == SessionSwitchReason.SessionLock);
        }
    }

    internal void OnWorkstationLockChanged(bool isLocked)
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _sessionVersion++;
            if (_isWorkstationLocked == isLocked)
            {
                return;
            }

            _isWorkstationLocked = isLocked;
            _lockedTimestamp = isLocked ? _timeProvider.GetTimestamp() : null;
            _lockCheckPending = isLocked;
            if (_sessionChanged.CurrentCount == 0)
            {
                _sessionChanged.Release();
            }

            Log.Info(nameof(BatteryMonitor), isLocked ? "Session locked; battery check requested." : "Session unlocked.");
        }
    }

    private TimeSpan GetCurrentPollInterval()
    {
        var configuredMinutes = _getSettings().PollIntervalMinutes;
        var intervalMinutes = double.IsFinite(configuredMinutes)
            ? Math.Clamp(configuredMinutes, MinimumPollIntervalMinutes, 24 * 60)
            : MinimumPollIntervalMinutes;
        return TimeSpan.FromMinutes(intervalMinutes);
    }

    private void CheckBatteryStatus(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = _timeProvider.GetTimestamp();
        var status = _readBatteryStatus();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lockCheckPending = false;
            _sessionChanged.Wait(0);
            var settings = _getSettings();
            // A lock/unlock may arrive during HID I/O; choose the threshold only after the fresh read.
            var threshold = _isWorkstationLocked
                ? settings.AlertThresholdLockedPercent
                : settings.AlertThresholdUnlockedPercent;
            Log.Info(nameof(BatteryMonitor), $"Battery check completed in {_timeProvider.GetElapsedTime(started).TotalMilliseconds:F0} ms; locked={_isWorkstationLocked}, threshold={threshold}, level={status?.Percentage.ToString() ?? "unavailable"}.");

            // Cached levels are useful for display, but must never warn for a disconnected or sleeping mouse.
            if (status is null || status.Percentage is < 0 or > 100)
            {
                _stopLowBatterySound();
                return;
            }

            HandleLowBatteryWarning(status, threshold, settings);
        }
    }

    private void HandleLowBatteryWarning(PulsarBatteryReader.BatteryStatus status, int batteryThreshold, AppSettings settings)
    {
        if (status.IsCharging)
        {
            _lastLowBatteryAlertTimestamp = null;
            _stopLowBatterySound();
            return;
        }

        if (status.Percentage >= Math.Max(settings.AlertThresholdUnlockedPercent, settings.AlertThresholdLockedPercent))
        {
            _lastLowBatteryAlertTimestamp = null;
        }

        if (status.Percentage >= batteryThreshold)
        {
            return;
        }

        var cooldown = TimeSpan.FromMinutes(double.IsFinite(settings.AlertCooldownMinutes)
            ? Math.Clamp(settings.AlertCooldownMinutes, 0, 24 * 60)
            : 20);
        if (_lastLowBatteryAlertTimestamp is null ||
            _timeProvider.GetElapsedTime(_lastLowBatteryAlertTimestamp.Value) >= cooldown)
        {
            if (_notifyLowBattery(status, batteryThreshold))
            {
                _lastLowBatteryAlertTimestamp = _timeProvider.GetTimestamp();
                Log.Info(nameof(BatteryMonitor), "Low-battery notification submitted.");
            }
        }
    }

    private static Func<PulsarBatteryReader.BatteryStatus?> CreateBatteryReader()
    {
        var reader = new PulsarBatteryReader();
        return () => reader.ReadBatteryStatus();
    }

    private static bool? ReadSessionLockState()
    {
        const int WtsSessionInfoEx = 25;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, WtsSessionInfoEx, out var buffer, out var size))
        {
            return null;
        }

        try
        {
            // WTSINFOEXW: level at 0, eight-byte-aligned union at 8, SessionFlags at 16 (x64).
            if (size < 20 || Marshal.ReadInt32(buffer) != 1)
            {
                return null;
            }

            return Marshal.ReadInt32(buffer, 16) switch { 0 => true, 1 => false, _ => null };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
