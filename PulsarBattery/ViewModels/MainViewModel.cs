using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PulsarBattery.Models;
using PulsarBattery.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int MaxHistoryEntries = 500;
    private const double MinimumPollIntervalMinutes = 0.1;
    private const double HistorySaveIntervalSeconds = 15.0;
    private const double DefaultPollIntervalMinutes = 1.0;
    private const double DefaultLogIntervalMinutes = 5.0;
    private const int DefaultUnlockedAlertThresholdPercent = 5;
    private const int DefaultLockedAlertThresholdPercent = 30;
    private const double DefaultAlertCooldownMinutes = 20.0;
    private const string DefaultModelName = "-";
    private const string InitialStatusText = "Ready";

    private readonly PulsarBatteryReader _batteryReader;
    private readonly HistoryStore _historyStore;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _historySaveTimer;
    private readonly SemaphoreSlim _historySaveLock;
    private readonly SemaphoreSlim _batteryUpdateLock;

    private DateTimeOffset _lastLoggedTime;
    private bool _isHistoryLoaded;
    private bool _isLoading;
    private bool _hasInitialData;
    private int _batteryPercentage;
    private bool _isCharging;
    private string _modelName;
    private DateTimeOffset? _lastUpdated;
    private double _pollIntervalMinutes;
    private double _logIntervalMinutes;
    private int _alertThresholdUnlockedPercent;
    private int _alertThresholdLockedPercent;
    private bool _enableBeeps;
    private double _alertCooldownMinutes;
    private string _statusText;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BatteryReading> History { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasInitialData
    {
        get => _hasInitialData;
        private set => SetProperty(ref _hasInitialData, value);
    }

    public Visibility LoadingVisibility => IsLoading && !HasInitialData ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => HasInitialData ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RefreshingVisibility => IsLoading && HasInitialData ? Visibility.Visible : Visibility.Collapsed;

    public int BatteryPercentage
    {
        get => _batteryPercentage;
        private set => SetProperty(ref _batteryPercentage, value);
    }

    public bool IsCharging
    {
        get => _isCharging;
        private set => SetProperty(ref _isCharging, value);
    }

    public string ModelName
    {
        get => _modelName;
        private set => SetProperty(ref _modelName, value);
    }

    public string ChargingStateText => IsCharging ? "Charging" : "Not charging";

    public string LastUpdatedText => _lastUpdated.HasValue
        ? $"Last updated: {_lastUpdated.Value.ToString("T", CultureInfo.CurrentCulture)}"
        : "No data yet";

    public static double GlobalPollIntervalMinutes { get; private set; } = DefaultPollIntervalMinutes;

    public double PollIntervalMinutes
    {
        get => _pollIntervalMinutes;
        set
        {
            var clampedMinutes = Math.Clamp(value, MinimumPollIntervalMinutes, 120);
            if (SetProperty(ref _pollIntervalMinutes, clampedMinutes))
            {
                GlobalPollIntervalMinutes = clampedMinutes;
                UpdatePollTimerInterval();
                AppSettingsService.Update(settings => settings with { PollIntervalMinutes = clampedMinutes });
            }
        }
    }

    public double LogIntervalMinutes
    {
        get => _logIntervalMinutes;
        set
        {
            var clampedMinutes = Math.Clamp(value, 0.1, 240);
            if (SetProperty(ref _logIntervalMinutes, clampedMinutes))
            {
                AppSettingsService.Update(settings => settings with { LogIntervalMinutes = clampedMinutes });
            }
        }
    }

    public int AlertThresholdUnlockedPercent
    {
        get => _alertThresholdUnlockedPercent;
        set
        {
            var clamped = Math.Clamp(value, 1, 100);
            if (SetProperty(ref _alertThresholdUnlockedPercent, clamped))
            {
                AppSettingsService.Update(settings => settings with { AlertThresholdUnlockedPercent = clamped });
            }
        }
    }

    public int AlertThresholdLockedPercent
    {
        get => _alertThresholdLockedPercent;
        set
        {
            var clamped = Math.Clamp(value, 1, 100);
            if (SetProperty(ref _alertThresholdLockedPercent, clamped))
            {
                AppSettingsService.Update(settings => settings with { AlertThresholdLockedPercent = clamped });
            }
        }
    }

    public bool EnableBeeps
    {
        get => _enableBeeps;
        set
        {
            if (SetProperty(ref _enableBeeps, value))
            {
                AppSettingsService.Update(settings => settings with { EnableBeeps = value });
            }
        }
    }

    public double AlertCooldownMinutes
    {
        get => _alertCooldownMinutes;
        set
        {
            var clampedMinutes = Math.Clamp(value, 0, 24 * 60);
            if (SetProperty(ref _alertCooldownMinutes, clampedMinutes))
            {
                AppSettingsService.Update(settings => settings with { AlertCooldownMinutes = clampedMinutes });
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Visibility HistoryEmptyVisibility => History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public MainViewModel()
    {
        _batteryReader = new PulsarBatteryReader();
        _historyStore = new HistoryStore();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException($"{nameof(MainViewModel)} must be constructed on the UI thread.");
        _historySaveLock = new SemaphoreSlim(1, 1);
        _batteryUpdateLock = new SemaphoreSlim(1, 1);
        _lastLoggedTime = DateTimeOffset.MinValue;
        _modelName = DefaultModelName;

        var settings = AppSettingsService.Current;
        _pollIntervalMinutes = settings.PollIntervalMinutes <= 0 ? DefaultPollIntervalMinutes : settings.PollIntervalMinutes;
        _logIntervalMinutes = settings.LogIntervalMinutes <= 0 ? DefaultLogIntervalMinutes : settings.LogIntervalMinutes;
        _alertThresholdUnlockedPercent = settings.AlertThresholdUnlockedPercent <= 0 ? DefaultUnlockedAlertThresholdPercent : settings.AlertThresholdUnlockedPercent;
        _alertThresholdLockedPercent = settings.AlertThresholdLockedPercent <= 0 ? DefaultLockedAlertThresholdPercent : settings.AlertThresholdLockedPercent;
        _enableBeeps = settings.EnableBeeps;
        _alertCooldownMinutes = settings.AlertCooldownMinutes < 0 ? DefaultAlertCooldownMinutes : settings.AlertCooldownMinutes;

        _statusText = InitialStatusText;
        History = new ObservableCollection<BatteryReading>();

        GlobalPollIntervalMinutes = _pollIntervalMinutes;

        _pollTimer = CreatePollTimer();
        _historySaveTimer = CreateHistorySaveTimer();

        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HistoryEmptyVisibility));
    }

    public void Start()
    {
        _ = LoadHistoryAsync();
        _ = UpdateBatteryStatusAsync();
        _pollTimer.Start();
        _historySaveTimer.Start();
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _historySaveTimer.Stop();
        _ = SaveHistoryAsync();
    }

    public void RefreshNow()
    {
        _ = UpdateBatteryStatusAsync();
    }

    private DispatcherTimer CreatePollTimer()
    {
        var timer = new DispatcherTimer();
        UpdatePollTimerInterval(timer);
        timer.Tick += async (_, _) => await UpdateBatteryStatusAsync();
        return timer;
    }

    private DispatcherTimer CreateHistorySaveTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(HistorySaveIntervalSeconds)
        };
        timer.Tick += async (_, _) => await SaveHistoryAsync();
        return timer;
    }

    private Task EnqueueAsync(Action action)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    private async Task LoadHistoryAsync()
    {
        if (_isHistoryLoaded)
        {
            return;
        }

        _isHistoryLoaded = true;

        try
        {
            var historicalReadings = await _historyStore.LoadAsync().ConfigureAwait(false);
            
            if (historicalReadings.Count > 0)
            {
                await PopulateHistoryCollectionAsync(historicalReadings);
                
                // Load cached data from most recent history entry
                await EnqueueAsync(() =>
                {
                    var mostRecent = historicalReadings[0];
                    BatteryPercentage = mostRecent.Percentage;
                    IsCharging = mostRecent.IsCharging;
                    ModelName = mostRecent.Model;
                    _lastUpdated = mostRecent.Timestamp;
                    HasInitialData = true;
                    OnPropertyChanged(nameof(ChargingStateText));
                    OnPropertyChanged(nameof(LastUpdatedText));
                });
            }
        }
        catch
        {
        }
    }

    private async Task PopulateHistoryCollectionAsync(IReadOnlyList<BatteryReading> readings)
    {
        await EnqueueAsync(() =>
        {
            foreach (var reading in readings)
            {
                History.Add(reading);
            }
        });
    }

    private async Task SaveHistoryAsync()
    {
        if (!_isHistoryLoaded || !await TryAcquireSaveLockAsync())
        {
            return;
        }

        try
        {
            var snapshot = CreateHistorySnapshot();
            await _historyStore.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _historySaveLock.Release();
        }
    }

    private async Task<bool> TryAcquireSaveLockAsync()
    {
        return await _historySaveLock.WaitAsync(0).ConfigureAwait(false);
    }

    private BatteryReading[] CreateHistorySnapshot()
    {
        var snapshot = new BatteryReading[History.Count];
        for (var i = 0; i < History.Count; i++)
        {
            snapshot[i] = History[i];
        }
        return snapshot;
    }

    private async Task UpdateBatteryStatusAsync()
    {
        if (!await _batteryUpdateLock.WaitAsync(0))
        {
            return;
        }

        try
        {
        IsLoading = true;
        StatusText = "Reading battery status...";
        
        var batteryStatus = await ReadBatteryStatusAsync();
        
        if (batteryStatus is null)
        {
            StatusText = "No device found";
            IsLoading = false;
            return;
        }

        UpdateBatteryProperties(batteryStatus);
        StatusText = "Updated";
        HasInitialData = true;
        IsLoading = false;

        if (ShouldLogCurrentReading())
        {
            LogBatteryReading(batteryStatus);
        }
        }
        finally
        {
            _batteryUpdateLock.Release();
        }
    }

    private Task<PulsarBatteryReader.BatteryStatus?> ReadBatteryStatusAsync()
    {
        return Task.Run(() => _batteryReader.ReadBatteryStatus());
    }

    private void UpdateBatteryProperties(PulsarBatteryReader.BatteryStatus status)
    {
        BatteryPercentage = status.Percentage;
        IsCharging = status.IsCharging;
        ModelName = status.Model;
        _lastUpdated = DateTimeOffset.Now;
        
        OnPropertyChanged(nameof(ChargingStateText));
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private bool ShouldLogCurrentReading()
    {
        if (IsFirstLog())
        {
            return true;
        }

        return HasLogIntervalElapsed();
    }

    private bool IsFirstLog()
    {
        return _lastLoggedTime == DateTimeOffset.MinValue;
    }

    private bool HasLogIntervalElapsed()
    {
        var timeSinceLastLog = DateTimeOffset.Now - _lastLoggedTime;
        return timeSinceLastLog >= TimeSpan.FromMinutes(LogIntervalMinutes);
    }

    private void LogBatteryReading(PulsarBatteryReader.BatteryStatus status)
    {
        _lastLoggedTime = DateTimeOffset.Now;
        
        var reading = new BatteryReading(_lastLoggedTime, status.Percentage, status.IsCharging, status.Model);
        History.Insert(0, reading);
        
        TrimHistoryToMaxEntries();
    }

    private void TrimHistoryToMaxEntries()
    {
        while (History.Count > MaxHistoryEntries)
        {
            History.RemoveAt(History.Count - 1);
        }
    }

    private void UpdatePollTimerInterval()
    {
        UpdatePollTimerInterval(_pollTimer);
    }

    private void UpdatePollTimerInterval(DispatcherTimer timer)
    {
        var clampedMinutes = Math.Max(MinimumPollIntervalMinutes, PollIntervalMinutes);
        timer.Interval = TimeSpan.FromMinutes(clampedMinutes);
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        
        // Update visibility properties when loading state changes
        if (propertyName == nameof(IsLoading) || propertyName == nameof(HasInitialData))
        {
            OnPropertyChanged(nameof(LoadingVisibility));
            OnPropertyChanged(nameof(ContentVisibility));
            OnPropertyChanged(nameof(RefreshingVisibility));
        }
        
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
