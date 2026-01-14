using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.ViewModels;
using System;
using WinRT.Interop;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace PulsarBattery.Services;

internal sealed partial class TrayIcon : UserControl, IDisposable
{
    private WinUIWindow? _window;

    public TrayIcon()
    {
        InitializeComponent();

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

        if (viewModel is not null)
        {
            TaskbarIcon.DataContext = viewModel;
        }
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



