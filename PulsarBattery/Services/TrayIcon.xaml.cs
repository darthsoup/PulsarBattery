using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.Tools;
using PulsarBattery.ViewModels;
using System;
using System.ComponentModel;
using WinRT.Interop;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace PulsarBattery.Services;

internal sealed partial class TrayIcon : UserControl, IDisposable, INotifyPropertyChanged
{
    private readonly TrayIconRenderer _renderer = new();

    private WinUIWindow? _window;
    private MainViewModel? _viewModel;
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _refreshTimer;
    private H.NotifyIcon.Core.MessageWindow? _messageWindow;
    private bool _shellCreateRequested;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        private set
        {
            _viewModel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewModel)));
        }
    }

    public TrayIcon()
    {
        InitializeComponent();

        OpenMenuItem.Text = Loc.T("Open");
        ExitMenuItem.Text = Loc.T("Exit");

        TaskbarIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        OpenMenuItem.Command = new RelayCommand(ShowWindow);
        ExitMenuItem.Command = new RelayCommand(ExitApp);
    }

    public void ForceCreate(bool enablesEfficiencyMode = true)
    {
        _shellCreateRequested = true;
        TaskbarIcon.ForceCreate(enablesEfficiencyMode);

        // Forced render as belt and braces, in case the library's stored-handle behaviour ever changes.
        UpdateTrayIcon(force: true);
    }

    public void Initialize(WinUIWindow window, MainViewModel? viewModel = null)
    {
        _window = window;
        _dispatcherQueue = window.DispatcherQueue;
        ViewModel = viewModel;

        // This control never enters a visual tree, so Loading never fires and x:Bind stays dormant.
        // Kick the generated bindings by hand so the tooltip and menu rows track the view model.
        Bindings.Update();

        if (viewModel is not null)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        UpdateTrayIcon(force: true);

        // Backstop for DPI/theme changes while asleep and for silent Shell_NotifyIcon failures (a commit
        // is never proof the shell shows the icon). Forced, because the dedupe cache cannot know about those.
        _refreshTimer = _dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(60);
        _refreshTimer.Tick += (_, _) => HealTrayIcon();
        _refreshTimer.Start();

        // TaskbarCreated MUST force a re-render: the library re-adds with a stored handle our bookkeeping
        // may already have destroyed, and the dedupe cache would otherwise leave the icon blank.
        try
        {
            _messageWindow = TaskbarIcon.TrayIcon.MessageWindow;
            _messageWindow.TaskbarCreated += MessageWindow_IconRefreshNeeded;
            _messageWindow.DpiChanged += MessageWindow_IconRefreshNeeded;
        }
        catch (Exception ex)
        {
            // Best effort: without these events the 60s timer still converges.
            _messageWindow = null;
            Log.Error(nameof(TrayIcon), $"MessageWindow events unavailable: {ex.Message}");
        }
    }

    private void MessageWindow_IconRefreshNeeded(object? sender, EventArgs e)
    {
        // Raised on the message-loop thread; only marshal, do no work here.
        _dispatcherQueue?.TryEnqueue(HealTrayIcon);
    }

    /// <summary>
    /// Re-creates the shell icon if a NIM_ADD was lost. The library swallows those errors and nothing
    /// else ever retries Create.
    /// </summary>
    private void HealTrayIcon()
    {
        if (_disposed || !_shellCreateRequested)
        {
            return;
        }

        try
        {
            var trayIcon = TaskbarIcon.TrayIcon;
            if (!trayIcon.IsCreated)
            {
                trayIcon.Create();
            }
        }
        catch (Exception ex)
        {
            Log.Error(nameof(TrayIcon), $"tray icon re-create failed: {ex.Message}");
        }

        UpdateTrayIcon(force: true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TrayIconState) or null or "")
        {
            UpdateTrayIcon();
        }
    }

    private void UpdateTrayIcon(bool force = false)
    {
        if (_disposed || ViewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            var icon = _renderer.RenderIfChanged(viewModel.TrayIconState, force);
            if (icon is null)
            {
                return;
            }

            try
            {
                TaskbarIcon.Icon = icon;
                _renderer.CommitAssignment();
            }
            catch
            {
                _renderer.AbandonAssignment();
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Error(nameof(TrayIcon), ex);
        }
    }

    private void ShowWindow()
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            EfficiencyMode.Set(false);

            var hwnd = WindowNative.GetWindowHandle(_window);
            var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow?.Show();
            _window.Activate();
        });
    }

    private static void ExitApp() => global::PulsarBattery.App.ExitApplication();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        if (_messageWindow is not null)
        {
            _messageWindow.TaskbarCreated -= MessageWindow_IconRefreshNeeded;
            _messageWindow.DpiChanged -= MessageWindow_IconRefreshNeeded;
            _messageWindow = null;
        }

        _refreshTimer?.Stop();
        _refreshTimer = null;

        TaskbarIcon.Dispose();
        _renderer.Dispose();
    }

    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();

        event EventHandler? System.Windows.Input.ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
