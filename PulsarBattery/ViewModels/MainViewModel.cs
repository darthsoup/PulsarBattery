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
    private const string DefaultModelName = "-";
    private const string InitialStatusText = "Ready";

    private readonly PulsarBatteryReader _batteryReader;
    private readonly HistoryStore _historyStore;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _historySaveTimer;
    private readonly SemaphoreSlim _historySaveLock;

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
            if (SetProperty(ref _pollIntervalMinutes, value))
            {
                GlobalPollIntervalMinutes = value;
                UpdatePollTimerInterval();
            }
        }
    }

    public double LogIntervalMinutes
    {
        get => _logIntervalMinutes;
        set => SetProperty(ref _logIntervalMinutes, value);
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
        _historySaveLock = new SemaphoreSlim(1, 1);
        _lastLoggedTime = DateTimeOffset.MinValue;
        _modelName = DefaultModelName;
        _pollIntervalMinutes = DefaultPollIntervalMinutes;
        _logIntervalMinutes = DefaultLogIntervalMinutes;
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
                var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                dispatcherQueue.TryEnqueue(() =>
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
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        await Task.Run(() =>
        {
            dispatcherQueue.TryEnqueue(() =>
            {
                foreach (var reading in readings)
                {
                    History.Add(reading);
                }
            });
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
        }
        
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}