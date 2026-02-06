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
    private readonly TimeSpan _statusCacheDuration;
    
    private (DateTimeOffset timestamp, PulsarBatteryReader.BatteryStatus status)? _cachedBatteryStatus;
    private DateTimeOffset? _lastLowBatteryAlertAt;

    public BatteryMonitor()
    {
        _batteryReader = new PulsarBatteryReader();
        _cancellationTokenSource = new CancellationTokenSource();
        _statusCacheDuration = TimeSpan.FromMinutes(10);
    }

    public void Start()
    {
        Task.Run(() => MonitorBatteryAsync(_cancellationTokenSource.Token));
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
    }

    private async Task MonitorBatteryAsync(CancellationToken cancellationToken)
    {
        var lastUnlockedTime = DateTimeOffset.MinValue;
        var lastCheckTime = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            var isWorkstationLocked = await ConfirmWorkstationIsLockedAsync(cancellationToken);

            if (isWorkstationLocked)
            {
                HandleLockedWorkstation(lastUnlockedTime);
            }
            else
            {
                lastUnlockedTime = DateTimeOffset.UtcNow;
                lastCheckTime = HandleUnlockedWorkstation(lastCheckTime);
            }

            await Task.Delay(TimeSpan.FromSeconds(MonitoringLoopDelaySeconds), cancellationToken);
        }
    }

    private async Task<bool> ConfirmWorkstationIsLockedAsync(CancellationToken cancellationToken)
    {
        var isLocked = IsWorkstationLocked();
        await Task.Delay(TimeSpan.FromSeconds(WorkstationLockConfirmationDelaySeconds), cancellationToken);
        return isLocked && IsWorkstationLocked();
    }

    private void HandleLockedWorkstation(DateTimeOffset lastUnlockedTime)
    {
        if (IsWithinPostLockMonitoringWindow(lastUnlockedTime))
        {
            var threshold = AppSettingsService.Current.AlertThresholdLockedPercent;
            CheckBatteryStatus(threshold);
        }
    }

    private bool IsWithinPostLockMonitoringWindow(DateTimeOffset lastUnlockedTime)
    {
        var timeSinceUnlock = DateTimeOffset.UtcNow - lastUnlockedTime;
        return timeSinceUnlock < TimeSpan.FromSeconds(PostLockMonitoringWindowSeconds);
    }

    private DateTimeOffset HandleUnlockedWorkstation(DateTimeOffset lastCheckTime)
    {
        var pollInterval = GetCurrentPollInterval();

        if (ShouldCheckBatteryStatus(lastCheckTime, pollInterval))
        {
            var threshold = AppSettingsService.Current.AlertThresholdUnlockedPercent;
            CheckBatteryStatus(threshold);
            return DateTimeOffset.UtcNow;
        }

        return lastCheckTime;
    }

    private TimeSpan GetCurrentPollInterval()
    {
        var intervalMinutes = Math.Max(MinimumPollIntervalMinutes, AppSettingsService.Current.PollIntervalMinutes);
        return TimeSpan.FromMinutes(intervalMinutes);
    }

    private bool ShouldCheckBatteryStatus(DateTimeOffset lastCheckTime, TimeSpan interval)
    {
        var timeSinceLastCheck = DateTimeOffset.UtcNow - lastCheckTime;
        return timeSinceLastCheck >= interval;
    }

    private void CheckBatteryStatus(int batteryThreshold)
    {
        var status = ReadBatteryStatusWithCache();
        
        if (status is null)
        {
            Debug.WriteLine("Battery status not available");
            return;
        }

        HandleLowBatteryWarning(status, batteryThreshold);
    }

    private void HandleLowBatteryWarning(PulsarBatteryReader.BatteryStatus status, int batteryThreshold)
    {
        if (status.IsCharging)
        {
            Debug.WriteLine("Battery state is charging or above the threshold");
            _lastLowBatteryAlertAt = null;
            return;
        }

        Debug.WriteLine($"Battery: NotCharging {status.Percentage}% threshold {batteryThreshold}");

        if (status.Percentage >= batteryThreshold)
        {
            _lastLowBatteryAlertAt = null;
            return;
        }

        if (IsLowBatteryAlertAllowed())
        {
            Debug.WriteLine("Warning: Battery level is below threshold and not charging!");
            NotificationHelper.NotifyLowBattery(status.Percentage, batteryThreshold, status.Model);
        }
    }

    private bool IsLowBatteryAlertAllowed()
    {
        var cooldownMinutes = AppSettingsService.Current.AlertCooldownMinutes;
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, cooldownMinutes));

        if (_lastLowBatteryAlertAt is null)
        {
            _lastLowBatteryAlertAt = DateTimeOffset.UtcNow;
            return true;
        }

        var timeSinceLastAlert = DateTimeOffset.UtcNow - _lastLowBatteryAlertAt.Value;
        if (timeSinceLastAlert >= cooldown)
        {
            _lastLowBatteryAlertAt = DateTimeOffset.UtcNow;
            return true;
        }

        return false;
    }

    private PulsarBatteryReader.BatteryStatus? ReadBatteryStatusWithCache()
    {
        var currentStatus = _batteryReader.ReadBatteryStatus();
        
        if (currentStatus is not null)
        {
            UpdateStatusCache(currentStatus);
            return currentStatus;
        }

        return GetCachedStatusIfValid();
    }

    private void UpdateStatusCache(PulsarBatteryReader.BatteryStatus status)
    {
        _cachedBatteryStatus = (DateTimeOffset.UtcNow, status);
    }

    private PulsarBatteryReader.BatteryStatus? GetCachedStatusIfValid()
    {
        if (_cachedBatteryStatus is null)
        {
            return null;
        }

        var (cacheTimestamp, cachedStatus) = _cachedBatteryStatus.Value;
        
        if (IsCacheStillValid(cacheTimestamp))
        {
            Debug.WriteLine("Using cached battery status");
            return cachedStatus;
        }

        return null;
    }

    private bool IsCacheStillValid(DateTimeOffset cacheTimestamp)
    {
        var cacheAge = DateTimeOffset.UtcNow - cacheTimestamp;
        return cacheAge <= _statusCacheDuration;
    }

    private static bool IsWorkstationLocked()
    {
        return GetForegroundWindow() == IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
