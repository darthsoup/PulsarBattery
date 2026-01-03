using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Windows.Input;
using WinRT.Interop;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace PulsarBattery.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private WinUIWindow? _window;

    public TrayIconService()
    {
        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Pulsar Battery",
            Visibility = Visibility.Visible,
            ContextMenuMode = ContextMenuMode.PopupMenu,
        };

        _taskbarIcon.NoLeftClickDelay = true;
        _taskbarIcon.LeftClickCommand = new RelayCommand(ShowWindow);

        var menu = new MenuFlyout();
        menu.Items.Add(CreateCommandMenuItem("Open", ShowWindow));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateCommandMenuItem("Exit", ExitApp));
        _taskbarIcon.ContextFlyout = menu;

        TrySetIconFromAssets();
    }

    public void ForceCreate(bool enablesEfficiencyMode = true) => _taskbarIcon.ForceCreate(enablesEfficiencyMode);

    public void Initialize(WinUIWindow window)
    {
        _window = window;

        // Best-effort: ensure the window icon matches the tray icon if we have an .ico on disk.
        var iconPath = FindAssetIconPath();
        if (!string.IsNullOrEmpty(iconPath) && string.Equals(Path.GetExtension(iconPath), ".ico", StringComparison.OrdinalIgnoreCase))
        {
            TrySetWindowIcon(window, iconPath);
        }
    }

    public static IconSource? CreateTitleBarIconSource()
    {
        var assetPath = FindAssetIconPath();
        if (string.IsNullOrEmpty(assetPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(assetPath);
        var uri = new Uri($"ms-appx:///Assets/{fileName}");
        return new BitmapIconSource { UriSource = uri };
    }

    internal static string? FindAssetIconPath()
    {
        var assetDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (!Directory.Exists(assetDir))
        {
            return null;
        }

        var preferred = new[] { "AppIcon.ico", "AppIcon.png", "icon.ico", "icon.png", "pulsar.ico", "pulsar.png" };
        foreach (var name in preferred)
        {
            var candidate = Path.Combine(assetDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var f in Directory.EnumerateFiles(assetDir, "*.*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(f);
            if (string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }

        return null;
    }

    private static MenuFlyoutItem CreateCommandMenuItem(string text, Action execute)
    {
        return new MenuFlyoutItem
        {
            Text = text,
            Command = new RelayCommand(execute),
        };
    }

    private void TrySetIconFromAssets()
    {
        var assetPath = FindAssetIconPath();
        if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
        {
            return;
        }

        var fileName = Path.GetFileName(assetPath);
        var uri = new Uri($"ms-appx:///Assets/{fileName}");

        try
        {
            _taskbarIcon.IconSource = new BitmapImage(uri);
        }
        catch
        {
            // best-effort icon
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var hwnd = WindowNative.GetWindowHandle(_window);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow?.Show();
                }
                catch
                {
                    // ignore
                }

                _window.Activate();
            });
        }
        catch
        {
            // ignore
        }
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

    private static void TrySetWindowIcon(WinUIWindow window, string iconPath)
    {
        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(window));
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow?.SetIcon(iconPath);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _taskbarIcon.Dispose();
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();

        public event EventHandler? CanExecuteChanged;
    }
}
