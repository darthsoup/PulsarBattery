using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PulsarBattery.Device;
using PulsarBattery.Models;
using PulsarBattery.Services;
using PulsarBattery.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int MaxHistoryEntries = 500;
    private const int DefaultHistoryPageSize = 50;
    private const double MinimumPollIntervalMinutes = 1.0;
    private const double HistorySaveIntervalSeconds = 15.0;
    private const double DefaultPollIntervalMinutes = 1.0;
    private const double DefaultLogIntervalMinutes = 5.0;
    private const int DefaultUnlockedAlertThresholdPercent = 5;
    private const int DefaultLockedAlertThresholdPercent = 30;
    private const double DefaultAlertCooldownMinutes = 20.0;
    private const string DefaultModelName = "-";

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
    private bool _noDeviceFound;
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
    private bool _minimizeToTrayOnClose;
    private bool _showBatteryInTray;
    private bool _startWithWindows;
    private string _statusText;
    private string? _lowBatterySoundPath;
    private int _currentHistoryPage;
    private DeviceSettings? _deviceSettings;
    private bool _isRefreshingDeviceSettings;
    private string _mouseSettingsError = string.Empty;
    private ConnectionKind _connection;
    private string? _connectionName;
    private string? _firmwareVersion;
    private int? _linkRateHz;
    private int? _voltageMv;
    private int? _signalStrength;
    private string? _dongleFirmwareVersion;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BatteryReading> History { get; }

    public ObservableCollection<BatteryReading> PagedHistory { get; }

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

    public bool NoDeviceFound
    {
        get => _noDeviceFound;
        private set => SetProperty(ref _noDeviceFound, value);
    }

    public Visibility LoadingVisibility => IsLoading && !HasInitialData && !NoDeviceFound ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => HasInitialData && !NoDeviceFound ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoDeviceVisibility => NoDeviceFound && !IsLoading ? Visibility.Visible : Visibility.Collapsed;

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
        private set
        {
            if (SetProperty(ref _modelName, value))
            {
                OnPropertyChanged(nameof(DeviceImage));
                OnPropertyChanged(nameof(DeviceImageVisibility));
            }
        }
    }

    private static readonly Dictionary<string, string> DeviceImagePathByModel = new()
    {
        ["X2 CrazyLight"] = "ms-appx:///Assets/Devices/X2-CrazyLight.png",
        ["X2 V1"] = "ms-appx:///Assets/Devices/X2v1.png",
        ["X2 V3 eS"] = "ms-appx:///Assets/Devices/X2v3-eS.png",
    };

    private readonly Dictionary<string, BitmapImage> _deviceImageCache = new();

    public ImageSource? DeviceImage
    {
        get
        {
            if (!DeviceImagePathByModel.TryGetValue(ModelName, out var path))
            {
                return null;
            }

            if (!_deviceImageCache.TryGetValue(path, out var image))
            {
                image = new BitmapImage(new Uri(path));
                _deviceImageCache[path] = image;
            }

            return image;
        }
    }

    public Visibility DeviceImageVisibility => DeviceImage is null ? Visibility.Collapsed : Visibility.Visible;

    public string ChargingStateText => IsCharging ? Loc.T("Charging") : Loc.T("Not charging");

    /// <summary>
    /// Charging state plus the pack voltage when the device reports it. Kept
    /// separate from <see cref="ChargingStateText"/> so the tray tooltip stays
    /// short.
    /// </summary>
    public string BatteryDetailText => _voltageMv is int mv
        ? $"{ChargingStateText} · {(mv / 1000.0).ToString("0.00", CultureInfo.CurrentCulture)} V"
        : ChargingStateText;

    public string SleepText => _deviceSettings?.SleepSeconds is int seconds
        ? seconds < 60
            ? $"{seconds} s"
            : string.Format(CultureInfo.CurrentCulture, "{0} min", seconds / 60)
        : "—";

    public enum BatteryState { Normal, Charging, Low }

    /// <summary>
    /// Drives the hero card's state colors; Low mirrors <see cref="TrayIconState"/>.IsLow.
    /// </summary>
    public BatteryState BatteryVisualState =>
        IsCharging ? BatteryState.Charging
        : HasInitialData && BatteryPercentage < AlertThresholdUnlockedPercent ? BatteryState.Low
        : BatteryState.Normal;

    public Visibility ChargingGlyphVisibility => IsCharging ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Snapshot for <see cref="Services.TrayIconRenderer"/>; the icon itself
    /// is rendered in code (see TrayIcon.xaml.cs), not via bound properties.
    /// </summary>
    public TrayIconState TrayIconState => new(
        ShowBattery: ShowBatteryInTray,
        HasData: HasInitialData,
        Percentage: BatteryPercentage,
        IsCharging: IsCharging,
        IsLow: HasInitialData && !IsCharging && BatteryPercentage < AlertThresholdUnlockedPercent);

    public string TrayTooltipText => HasInitialData
        ? $"{ModelName} — {BatteryPercentage}% · {ChargingStateText}"
        : "Pulsar Battery";

    private void NotifyTrayProperties()
    {
        OnPropertyChanged(nameof(TrayIconState));
        OnPropertyChanged(nameof(TrayTooltipText));
    }

    /// <summary>The transport itself — the receiver's name, or the cable.</summary>
    public string ConnectionText => _connection switch
    {
        ConnectionKind.Wired => Loc.T("Wired"),
        ConnectionKind.Dongle => _connectionName ?? Loc.T("Wireless"),
        _ => "—",
    };

    /// <summary>
    /// The link underneath the transport: radio band, negotiated rate and
    /// signal quality, in whatever combination the device actually reports.
    /// </summary>
    public string ConnectionDetailText
    {
        get
        {
            var parts = new List<string>(3);

            if (_connection == ConnectionKind.Dongle)
            {
                parts.Add("2.4 GHz");
            }

            if (_linkRateHz is int hz)
            {
                parts.Add($"{hz} Hz");
            }

            if (SignalText.Length > 0)
            {
                parts.Add(SignalText);
            }

            return string.Join(" · ", parts);
        }
    }

    public Visibility ConnectionDetailVisibility =>
        ConnectionDetailText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Signal strength is a small bar count rather than a percentage, so it is
    /// shown as a word. Thresholds follow the Pulsar cMouse notes: 4+ excellent,
    /// 3 good, 2 fair, below that weak.
    /// </summary>
    public string SignalText => _signalStrength switch
    {
        null => string.Empty,
        >= 4 => Loc.T("Excellent"),
        3 => Loc.T("Good"),
        2 => Loc.T("Fair"),
        _ => Loc.T("Weak"),
    };

    public string ConnectionToolTip => _dongleFirmwareVersion is { Length: > 0 } version
        ? string.Format(CultureInfo.CurrentCulture, Loc.T("Receiver firmware {0}"), version)
        : ConnectionText;

    public string FirmwareText => _firmwareVersion ?? "—";

    public Visibility MouseSettingsVisibility => _deviceSettings is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility MouseSettingsUnsupportedVisibility => _deviceSettings is null ? Visibility.Visible : Visibility.Collapsed;

    public string PollingRateText => _deviceSettings?.PollingRateHz is int hz ? $"{hz} Hz" : "—";

    public string DebounceText => _deviceSettings?.DebounceMs is int ms ? $"{ms} ms" : "—";

    public string MotionSyncText => _deviceSettings?.MotionSync is bool motionSync
        ? Loc.T(motionSync ? "On" : "Off")
        : "—";

    public string DpiText => _deviceSettings?.Dpi is int dpi
        ? _deviceSettings?.DpiStage is int stage
            ? $"{dpi} ({string.Format(Loc.T("Stage {0}"), stage)})"
            : dpi.ToString(CultureInfo.CurrentCulture)
        : "—";

    public string DpiStageText => _deviceSettings?.DpiStage is int stage
        ? string.Format(Loc.T("Stage {0}"), stage)
        : string.Empty;

    public int? PollingRateHz => _deviceSettings?.PollingRateHz;

    public int? LodMm10 => _deviceSettings?.LodMm10;

    public int? DpiStage => _deviceSettings?.DpiStage;

    public bool MouseSettingsErrorOpen => _mouseSettingsError.Length > 0;

    public string MouseSettingsErrorText => _mouseSettingsError;

    public bool MotionSyncIsOn
    {
        get => _deviceSettings?.MotionSync ?? false;
        set
        {
            if (_isRefreshingDeviceSettings || _deviceSettings?.MotionSync is null || _deviceSettings.MotionSync == value)
            {
                return;
            }

            _ = ApplyDeviceSettingAsync(new DeviceSettings(MotionSync: value));
        }
    }

    public bool AngleSnapIsOn
    {
        get => _deviceSettings?.AngleSnap ?? false;
        set
        {
            if (_isRefreshingDeviceSettings || _deviceSettings?.AngleSnap is null || _deviceSettings.AngleSnap == value)
            {
                return;
            }

            _ = ApplyDeviceSettingAsync(new DeviceSettings(AngleSnap: value));
        }
    }

    public bool RippleControlIsOn
    {
        get => _deviceSettings?.RippleControl ?? false;
        set
        {
            if (_isRefreshingDeviceSettings || _deviceSettings?.RippleControl is null || _deviceSettings.RippleControl == value)
            {
                return;
            }

            _ = ApplyDeviceSettingAsync(new DeviceSettings(RippleControl: value));
        }
    }

    public double DebounceMsValue
    {
        get => _deviceSettings?.DebounceMs ?? 0;
        set
        {
            if (_isRefreshingDeviceSettings || double.IsNaN(value) || _deviceSettings?.DebounceMs is null)
            {
                return;
            }

            var ms = Math.Clamp((int)Math.Round(value), 0, 30);
            if (_deviceSettings.DebounceMs == ms)
            {
                return;
            }

            _ = ApplyDeviceSettingAsync(new DeviceSettings(DebounceMs: ms));
        }
    }

    public double DpiValue
    {
        get => _deviceSettings?.Dpi ?? 0;
        set
        {
            if (_isRefreshingDeviceSettings || double.IsNaN(value) || _deviceSettings?.Dpi is null)
            {
                return;
            }

            var dpi = Math.Clamp((int)Math.Round(value), 50, 26000);
            if (_deviceSettings.Dpi == dpi)
            {
                return;
            }

            _ = ApplyDeviceSettingAsync(new DeviceSettings(Dpi: dpi));
        }
    }

    public void ApplyPollingRate(int hz)
    {
        if (_isRefreshingDeviceSettings || _deviceSettings?.PollingRateHz == hz)
        {
            return;
        }

        _ = ApplyDeviceSettingAsync(new DeviceSettings(PollingRateHz: hz));
    }

    public void ApplyLod(int mm10)
    {
        if (_isRefreshingDeviceSettings || _deviceSettings?.LodMm10 == mm10)
        {
            return;
        }

        _ = ApplyDeviceSettingAsync(new DeviceSettings(LodMm10: mm10));
    }

    public void ApplyDpiStage(int stage)
    {
        if (_isRefreshingDeviceSettings || _deviceSettings?.DpiStage == stage)
        {
            return;
        }

        _ = ApplyDeviceSettingAsync(new DeviceSettings(DpiStage: stage));
    }

    public void ClearMouseSettingsError()
    {
        SetMouseSettingsError(string.Empty);
    }

    public async Task RefreshDeviceSettingsAsync()
    {
        UpdateDeviceSettings(await ReadDeviceSettingsAsync());
    }

    public string LastUpdatedText => _lastUpdated.HasValue
        ? _lastUpdated.Value.ToString("T", CultureInfo.CurrentCulture)
        : Loc.T("No data yet");

    public string HistoryCountText => string.Format(Loc.T("{0} entries"), History.Count);

    public double PollIntervalMinutes
    {
        get => _pollIntervalMinutes;
        set
        {
            var clampedMinutes = Math.Clamp(value, MinimumPollIntervalMinutes, 120);
            if (SetProperty(ref _pollIntervalMinutes, clampedMinutes))
            {
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
            var clampedMinutes = Math.Clamp(value, 1.0, 240);
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
                // The threshold feeds TrayIconState.IsLow and BatteryVisualState — recolor promptly.
                NotifyTrayProperties();
                OnPropertyChanged(nameof(BatteryVisualState));
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

    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set
        {
            if (SetProperty(ref _minimizeToTrayOnClose, value))
            {
                AppSettingsService.Update(settings => settings with { MinimizeToTrayOnClose = value });
            }
        }
    }

    public bool ShowBatteryInTray
    {
        get => _showBatteryInTray;
        set
        {
            if (SetProperty(ref _showBatteryInTray, value))
            {
                AppSettingsService.Update(settings => settings with { ShowBatteryInTray = value });
                NotifyTrayProperties();
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value))
            {
                return;
            }

            var result = StartupRegistrationService.SetEnabled(value);
            if (!result.Success)
            {
                SetProperty(ref _startWithWindows, result.IsEnabled, nameof(StartWithWindows));
                StatusText = Loc.T("Unable to update startup registration");
                return;
            }

            AppSettingsService.Update(settings => settings with { StartWithWindows = result.IsEnabled });
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? LowBatterySoundPath
    {
        get => _lowBatterySoundPath;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (SetProperty(ref _lowBatterySoundPath, normalized))
            {
                AppSettingsService.Update(settings => settings with { LowBatterySoundPath = normalized });
                OnPropertyChanged(nameof(LowBatterySoundDisplay));
            }
        }
    }

    public string LowBatterySoundDisplay => string.IsNullOrWhiteSpace(LowBatterySoundPath)
        ? Loc.T("Default (Windows low battery sound)")
        : LowBatterySoundPath;

    public Visibility HistoryEmptyVisibility => History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public int CurrentHistoryPage => _currentHistoryPage;

    public int HistoryPageCount => Math.Max(1, (int)Math.Ceiling(History.Count / (double)DefaultHistoryPageSize));

    public string HistoryPageText => $"{CurrentHistoryPage} / {HistoryPageCount}";

    public bool CanGoToPreviousHistoryPage => CurrentHistoryPage > 1;

    public bool CanGoToNextHistoryPage => CurrentHistoryPage < HistoryPageCount;

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
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _showBatteryInTray = settings.ShowBatteryInTray;
        var isRunningFromInstallDirectory = SelfInstallService.IsRunningFromInstallDirectory();
        var isBundledExecutable = SelfInstallService.IsCurrentExecutableBundled();
        var currentExecutablePath = SelfInstallService.GetCurrentExecutablePath();
        _startWithWindows = isRunningFromInstallDirectory &&
            isBundledExecutable &&
            StartupRegistrationService.IsEnabledForExecutable(currentExecutablePath);
        _lowBatterySoundPath = string.IsNullOrWhiteSpace(settings.LowBatterySoundPath) ? null : settings.LowBatterySoundPath;

        if (isRunningFromInstallDirectory && isBundledExecutable && settings.StartWithWindows != _startWithWindows)
        {
            AppSettingsService.Update(current => current with { StartWithWindows = _startWithWindows });
        }

        _statusText = Loc.T("Ready");
        History = new ObservableCollection<BatteryReading>();
        PagedHistory = new ObservableCollection<BatteryReading>();
        _currentHistoryPage = 1;

        _pollTimer = CreatePollTimer();
        _historySaveTimer = CreateHistorySaveTimer();

        History.CollectionChanged += (_, _) => UpdateHistoryPagination();
        UpdateHistoryPagination();
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

    public async Task RetryConnectionAsync()
    {
        await UpdateBatteryStatusAsync();
    }

    public void RefreshNow()
    {
        _ = UpdateBatteryStatusAsync();
    }

    public void NextHistoryPage()
    {
        if (!CanGoToNextHistoryPage)
        {
            return;
        }

        SetCurrentHistoryPage(CurrentHistoryPage + 1);
        UpdatePagedHistory();
    }

    public void PreviousHistoryPage()
    {
        if (!CanGoToPreviousHistoryPage)
        {
            return;
        }

        SetCurrentHistoryPage(CurrentHistoryPage - 1);
        UpdatePagedHistory();
    }

    public async Task ClearHistoryAsync()
    {
        if (!_isHistoryLoaded)
        {
            _isHistoryLoaded = true;
        }

        await EnqueueAsync(() =>
        {
            History.Clear();
            _lastLoggedTime = DateTimeOffset.MinValue;
        });

        SetCurrentHistoryPage(1);
        await SaveHistoryAsync();
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
                    OnPropertyChanged(nameof(BatteryDetailText));
                    OnPropertyChanged(nameof(LastUpdatedText));
                    OnPropertyChanged(nameof(BatteryVisualState));
                    OnPropertyChanged(nameof(ChargingGlyphVisibility));
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(nameof(MainViewModel), ex);
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

    private void UpdateHistoryPagination()
    {
        var targetPage = Math.Clamp(CurrentHistoryPage, 1, HistoryPageCount);
        SetCurrentHistoryPage(targetPage);

        OnPropertyChanged(nameof(HistoryPageCount));
        OnPropertyChanged(nameof(HistoryPageText));
        OnPropertyChanged(nameof(HistoryCountText));
        OnPropertyChanged(nameof(CanGoToPreviousHistoryPage));
        OnPropertyChanged(nameof(CanGoToNextHistoryPage));
        OnPropertyChanged(nameof(HistoryEmptyVisibility));

        UpdatePagedHistory();
    }

    private void UpdatePagedHistory()
    {
        PagedHistory.Clear();

        if (History.Count == 0)
        {
            return;
        }

        var startIndex = (CurrentHistoryPage - 1) * DefaultHistoryPageSize;
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        foreach (var reading in History.Skip(startIndex).Take(DefaultHistoryPageSize))
        {
            PagedHistory.Add(reading);
        }
    }

    private void SetCurrentHistoryPage(int page)
    {
        if (SetProperty(ref _currentHistoryPage, page, nameof(CurrentHistoryPage)))
        {
            OnPropertyChanged(nameof(HistoryPageText));
            OnPropertyChanged(nameof(CanGoToPreviousHistoryPage));
            OnPropertyChanged(nameof(CanGoToNextHistoryPage));
        }
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
        catch (Exception ex)
        {
            Log.Error(nameof(MainViewModel), ex);
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
            NoDeviceFound = false;
            StatusText = Loc.T("Reading battery status...");

            var batteryStatus = await ReadBatteryStatusAsync();

            if (batteryStatus is null)
            {
                UpdateDeviceSettings(null);
                StatusText = Loc.T("No Pulsar mouse detected");
                NoDeviceFound = true;
                IsLoading = false;
                return;
            }

            UpdateBatteryProperties(batteryStatus);
            UpdateDeviceSettings(await ReadDeviceSettingsAsync());
            StatusText = Loc.T("Updated");
            HasInitialData = true;
            NoDeviceFound = false;
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

    private Task<DeviceSettings?> ReadDeviceSettingsAsync()
    {
        return Task.Run(() => _batteryReader.ReadDeviceSettings());
    }

    private void UpdateDeviceSettings(DeviceSettings? settings)
    {
        if (Equals(_deviceSettings, settings))
        {
            return;
        }

        _isRefreshingDeviceSettings = true;
        try
        {
            _deviceSettings = settings;
            OnPropertyChanged(nameof(MouseSettingsVisibility));
            OnPropertyChanged(nameof(MouseSettingsUnsupportedVisibility));
            OnPropertyChanged(nameof(PollingRateText));
            OnPropertyChanged(nameof(DebounceText));
            OnPropertyChanged(nameof(MotionSyncText));
            OnPropertyChanged(nameof(DpiText));
            OnPropertyChanged(nameof(DpiStageText));
            OnPropertyChanged(nameof(SleepText));
            OnPropertyChanged(nameof(PollingRateHz));
            OnPropertyChanged(nameof(LodMm10));
            OnPropertyChanged(nameof(DpiStage));
            OnPropertyChanged(nameof(MotionSyncIsOn));
            OnPropertyChanged(nameof(AngleSnapIsOn));
            OnPropertyChanged(nameof(RippleControlIsOn));
            OnPropertyChanged(nameof(DebounceMsValue));
            OnPropertyChanged(nameof(DpiValue));
        }
        finally
        {
            _isRefreshingDeviceSettings = false;
        }
    }

    private async Task ApplyDeviceSettingAsync(DeviceSettings changes)
    {
        try
        {
            var applied = await Task.Run(() => _batteryReader.ApplyDeviceSettings(changes));
            var settings = await ReadDeviceSettingsAsync();
            UpdateDeviceSettings(settings);

            if (applied != true)
            {
                SetMouseSettingsError(Loc.T("Setting could not be applied"));
            }
            else if (!RequestedChangesMatch(changes, settings))
            {
                SetMouseSettingsError(Loc.T("The mouse reported a different value. It may take effect after reconnecting."));
            }
            else
            {
                ClearMouseSettingsError();
            }
        }
        catch (Exception ex)
        {
            Log.Error(nameof(MainViewModel), ex);
            SetMouseSettingsError(Loc.T("Setting could not be applied"));
        }
    }

    private static bool RequestedChangesMatch(DeviceSettings requested, DeviceSettings? current)
    {
        if (current is null)
        {
            return false;
        }

        return (requested.PollingRateHz is null || requested.PollingRateHz == current.PollingRateHz)
            && (requested.DebounceMs is null || requested.DebounceMs == current.DebounceMs)
            && (requested.MotionSync is null || requested.MotionSync == current.MotionSync)
            && (requested.Dpi is null || requested.Dpi == current.Dpi)
            && (requested.DpiStage is null || requested.DpiStage == current.DpiStage)
            && (requested.LodMm10 is null || requested.LodMm10 == current.LodMm10)
            && (requested.AngleSnap is null || requested.AngleSnap == current.AngleSnap)
            && (requested.RippleControl is null || requested.RippleControl == current.RippleControl);
    }

    private void SetMouseSettingsError(string message)
    {
        if (_mouseSettingsError == message)
        {
            return;
        }

        _mouseSettingsError = message;
        OnPropertyChanged(nameof(MouseSettingsErrorOpen));
        OnPropertyChanged(nameof(MouseSettingsErrorText));
    }

    private void UpdateBatteryProperties(PulsarBatteryReader.BatteryStatus status)
    {
        BatteryPercentage = status.Percentage;
        IsCharging = status.IsCharging;
        ModelName = status.Model;
        _connection = status.Connection;
        _connectionName = status.ConnectionName;
        _firmwareVersion = status.FirmwareVersion;
        _linkRateHz = status.LinkRateHz;
        _voltageMv = status.VoltageMv;
        _signalStrength = status.SignalStrength;
        _dongleFirmwareVersion = status.DongleFirmwareVersion;
        _lastUpdated = DateTimeOffset.Now;

        OnPropertyChanged(nameof(ChargingStateText));
        OnPropertyChanged(nameof(BatteryDetailText));
        OnPropertyChanged(nameof(SignalText));
        OnPropertyChanged(nameof(ConnectionDetailText));
        OnPropertyChanged(nameof(ConnectionDetailVisibility));
        OnPropertyChanged(nameof(ConnectionToolTip));
        OnPropertyChanged(nameof(BatteryVisualState));
        OnPropertyChanged(nameof(ChargingGlyphVisibility));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(FirmwareText));
        OnPropertyChanged(nameof(LastUpdatedText));
        NotifyTrayProperties();
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
        if (propertyName == nameof(IsLoading) || propertyName == nameof(HasInitialData) || propertyName == nameof(NoDeviceFound))
        {
            OnPropertyChanged(nameof(LoadingVisibility));
            OnPropertyChanged(nameof(ContentVisibility));
            OnPropertyChanged(nameof(NoDeviceVisibility));
            OnPropertyChanged(nameof(RefreshingVisibility));
        }

        if (propertyName == nameof(HasInitialData))
        {
            NotifyTrayProperties();
            OnPropertyChanged(nameof(BatteryVisualState));
        }
        
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
