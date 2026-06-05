using H.NotifyIcon;
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
    private WinUIWindow? _window;
    private MainViewModel? _viewModel;

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
        => TaskbarIcon.ForceCreate(enablesEfficiencyMode);

    public void Initialize(WinUIWindow window, MainViewModel? viewModel = null)
    {
        _window = window;
        ViewModel = viewModel;
    }

    private void ShowWindow()
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow?.Show();
            _window.Activate();
        });
    }

    private void ExitApp()
    {
        try
        {
            global::PulsarBattery.App.RequestExit();
        }
        catch
        {
            // ignore
        }

        try
        {
            _window?.Close();
        }
        catch
        {
            // ignore
        }

        Microsoft.UI.Xaml.Application.Current?.Exit();
    }

    public void Dispose()
    {
        TaskbarIcon.Dispose();
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



