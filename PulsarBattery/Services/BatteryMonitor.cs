using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

public sealed class BatteryMonitor : IDisposable
{
    private const int WorkstationLockConfirmationDelaySeconds = 2;
    private const int PostLockMonitoringWindowSeconds = 10;
    private const int MonitoringLoopDelaySeconds = 5;
    private const double MinimumPollIntervalMinutes = 0.1;

    private readonly PulsarBatteryReader _batteryReader;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly object _lifecycleGate = new();
    private Task? _monitorTask;
    private bool _disposed;
    private long? _lastLowBatteryAlertTimestamp;

    public BatteryMonitor()
    {
        _batteryReader = new PulsarBatteryReader();
        _cancellationTokenSource = new CancellationTokenSource();
    }

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
            _cancellationTokenSource.Cancel();
            if (_monitorTask is null)
            {
                _cancellationTokenSource.Dispose();
            }
            else
            {
                // A synchronous HID read can outlive shutdown; release its token source after the loop exits.
                _ = _monitorTask.ContinueWith(
                    _ => _cancellationTokenSource.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private async Task MonitorBatteryAsync(CancellationToken cancellationToken)
    {
        long? lastUnlockedTimestamp = null;
        long? lastCheckTimestamp = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var isWorkstationLocked = await ConfirmWorkstationIsLockedAsync(cancellationToken);

                if (isWorkstationLocked)
                {
                    HandleLockedWorkstation(lastUnlockedTimestamp, cancellationToken);
                }
                else
                {
                    lastUnlockedTimestamp = Stopwatch.GetTimestamp();
                    lastCheckTimestamp = HandleUnlockedWorkstation(lastCheckTimestamp, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Battery monitor iteration failed: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(MonitoringLoopDelaySeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> ConfirmWorkstationIsLockedAsync(CancellationToken cancellationToken)
    {
        if (!IsWorkstationLocked())
        {
            return false;
        }

        await Task.Delay(TimeSpan.FromSeconds(WorkstationLockConfirmationDelaySeconds), cancellationToken);
        return IsWorkstationLocked();
    }

    private void HandleLockedWorkstation(long? lastUnlockedTimestamp, CancellationToken cancellationToken)
    {
        if (lastUnlockedTimestamp is not null &&
            Stopwatch.GetElapsedTime(lastUnlockedTimestamp.Value) < TimeSpan.FromSeconds(PostLockMonitoringWindowSeconds))
        {
            var threshold = AppSettingsService.Current.AlertThresholdLockedPercent;
            CheckBatteryStatus(threshold, cancellationToken);
        }
    }

    private long? HandleUnlockedWorkstation(long? lastCheckTimestamp, CancellationToken cancellationToken)
    {
        var pollInterval = GetCurrentPollInterval();

        if (lastCheckTimestamp is null || Stopwatch.GetElapsedTime(lastCheckTimestamp.Value) >= pollInterval)
        {
            var threshold = AppSettingsService.Current.AlertThresholdUnlockedPercent;
            CheckBatteryStatus(threshold, cancellationToken);
            return Stopwatch.GetTimestamp();
        }

        return lastCheckTimestamp;
    }

    private TimeSpan GetCurrentPollInterval()
    {
        var configuredMinutes = AppSettingsService.Current.PollIntervalMinutes;
        var intervalMinutes = double.IsFinite(configuredMinutes)
            ? Math.Clamp(configuredMinutes, MinimumPollIntervalMinutes, 24 * 60)
            : MinimumPollIntervalMinutes;
        return TimeSpan.FromMinutes(intervalMinutes);
    }

    private void CheckBatteryStatus(int batteryThreshold, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = _batteryReader.ReadBatteryStatus();
        cancellationToken.ThrowIfCancellationRequested();
        
        // Cached levels are useful for display, but must never warn for a disconnected or sleeping mouse.
        if (status is null || status.Percentage is < 0 or > 100)
        {
            Debug.WriteLine("Battery status not available");
            NotificationHelper.StopLowBatterySound();
            return;
        }

        lock (_lifecycleGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HandleLowBatteryWarning(status, batteryThreshold);
        }
    }

    private void HandleLowBatteryWarning(PulsarBatteryReader.BatteryStatus status, int batteryThreshold)
    {
        if (status.IsCharging)
        {
            _lastLowBatteryAlertTimestamp = null;
            NotificationHelper.StopLowBatterySound();
            return;
        }

        Debug.WriteLine($"Battery: NotCharging {status.Percentage}% threshold {batteryThreshold}");
        var settings = AppSettingsService.Current;
        if (status.Percentage >= Math.Max(settings.AlertThresholdUnlockedPercent, settings.AlertThresholdLockedPercent))
        {
            _lastLowBatteryAlertTimestamp = null;
        }

        if (status.Percentage >= batteryThreshold)
        {
            return;
        }

        if (IsLowBatteryAlertAllowed(_lastLowBatteryAlertTimestamp))
        {
            Debug.WriteLine("Warning: Battery level is below threshold and not charging!");
            if (NotificationHelper.NotifyLowBattery(status.Percentage, batteryThreshold, status.Model))
            {
                _lastLowBatteryAlertTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private static bool IsLowBatteryAlertAllowed(long? lastAlertTimestamp)
    {
        var cooldownMinutes = AppSettingsService.Current.AlertCooldownMinutes;
        var cooldown = TimeSpan.FromMinutes(double.IsFinite(cooldownMinutes)
            ? Math.Clamp(cooldownMinutes, 0, 24 * 60)
            : 20);
        return lastAlertTimestamp is null || Stopwatch.GetElapsedTime(lastAlertTimestamp.Value) >= cooldown;
    }

    private static bool IsWorkstationLocked()
    {
        return GetForegroundWindow() == IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
