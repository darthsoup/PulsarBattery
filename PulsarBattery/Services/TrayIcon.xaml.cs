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

        // Assign commands directly to the auto-generated fields
        TaskbarIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        OpenMenuItem.Command = new RelayCommand(ShowWindow);
        ExitMenuItem.Command = new RelayCommand(ExitApp);
    }

    public void ForceCreate(bool enablesEfficiencyMode = true)
    {
        _shellCreateRequested = true;
        TaskbarIcon.ForceCreate(enablesEfficiencyMode);

        // Belt and braces: the pre-creation Icon assignment in Initialize is
        // stored and used by the initial shell add, but a forced render here
        // guarantees a correct icon even if that stored-handle behavior ever
        // changes in the library.
        UpdateTrayIcon(force: true);
    }

    public void Initialize(WinUIWindow window, MainViewModel? viewModel = null)
    {
        _window = window;
        _dispatcherQueue = window.DispatcherQueue;
        ViewModel = viewModel;

        // This control never enters a visual tree, so its Loading event never
        // fires and x:Bind stays dormant. Kick the generated bindings by hand
        // so the tooltip and menu rows actually track the view model.
        Bindings.Update();

        if (viewModel is not null)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        UpdateTrayIcon(force: true);

        // Backstop: heals DPI/theme changes while the mouse is asleep, silent
        // Shell_NotifyIcon failures (H.NotifyIcon swallows them, so a "commit"
        // is never proof the shell shows the icon), and a tray icon lost to a
        // failed shell add. Forced, because the dedupe cache cannot know about
        // any of those.
        _refreshTimer = _dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(60);
        _refreshTimer.Tick += (_, _) => HealTrayIcon();
        _refreshTimer.Start();

        // Immediate repaint on explorer restart / DPI change. TaskbarCreated
        // MUST force a re-render: the library re-adds the icon with its stored
        // handle, which our bookkeeping may already have destroyed, and the
        // dedupe cache would otherwise leave the tray icon blank.
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
    /// Re-creates the shell icon if it was lost (failed NIM_ADD at startup or
    /// during an explorer restart — the library swallows those errors and
    /// nothing else ever retries Create) and forces a fresh render+assign.
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
