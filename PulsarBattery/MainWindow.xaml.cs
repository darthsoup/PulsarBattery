using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PulsarBattery.Pages;
using PulsarBattery.Tools;
using PulsarBattery.ViewModels;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Windows.System;
using WinRT.Interop;

namespace PulsarBattery;

public sealed partial class MainWindow : Window
{
    private const string EmbeddedIconResourceName = "PulsarBattery.Assets.icon.ico";

    // Logical (DIP) minimum below which page layouts become unreadable.
    private const int MinWindowWidthDip = 480;
    private const int MinWindowHeightDip = 420;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private readonly MainViewModel _viewModel = new();
    private AppWindow? _appWindow;
    private uint _windowDpi;
    private bool _isStopping;
    private static string? _extractedEmbeddedIconPath;

    internal MainViewModel ViewModel => _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode;
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        AppWindow.TitleBar.BackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.InactiveBackgroundColor = Colors.Transparent;
        TrySetWindowIcon();

        EnsureAppWindowInitialized();

        RootGrid.DataContext = _viewModel;
        _viewModel.Start();

        ConfigureNavigationItem(DashboardItem, "Dashboard", "Ctrl+1");
        ConfigureNavigationItem(MouseItem, "Mouse", "Ctrl+2");
        ConfigureNavigationItem(HistoryItem, "History", "Ctrl+3");
        NavView.Loaded += NavView_Loaded;

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        NavView.SelectedItem = DashboardItem;
        NavigateTo("dashboard");
    }

    internal void Stop()
    {
        _isStopping = true;
        _viewModel.Stop();
    }

    internal Task StopAsync()
    {
        _isStopping = true;
        return _viewModel.StopAsync();
    }

    internal void ShowAndActivate()
    {
        if (_isStopping || App.IsExitRequested)
        {
            return;
        }

        EfficiencyMode.Set(false);
        EnsureAppWindowInitialized();
        if (_appWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _appWindow?.Show();
        Activate();
    }

    private static void ConfigureNavigationItem(NavigationViewItem item, string label, string shortcut)
    {
        var text = Loc.T(label);
        item.Content = text;
        AutomationProperties.SetName(item, text);
        AutomationProperties.SetAcceleratorKey(item, shortcut);
        ToolTipService.SetToolTip(item, $"{text} ({shortcut})");
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // The built-in settings item is created by the framework and only
        // available once the NavigationView has loaded.
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            ConfigureNavigationItem(settingsItem, "Settings", "Ctrl+4");
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isStopping || App.IsExitRequested)
        {
            return;
        }

        var isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        _viewModel.SetWindowActive(isActive);
        if (isActive)
        {
            EfficiencyMode.Set(false);
            EnsureAppWindowInitialized();
            _viewModel.RefreshNow();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Activated -= MainWindow_Activated;
        NavView.Loaded -= NavView_Loaded;
        if (_appWindow is not null)
        {
            _appWindow.Closing -= AppWindow_Closing;
            _appWindow.Changed -= AppWindow_Changed;
        }
    }

    private void EnsureAppWindowInitialized()
    {
        if (_appWindow is not null)
        {
            return;
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Closing += AppWindow_Closing;
            _appWindow.Changed += AppWindow_Changed;
        }
        catch (Exception ex)
        {
            Log.Error(nameof(MainWindow), ex);
            return;
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
            var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var width = Math.Min((int)Math.Round(900 * scale), workArea.Width);
            var height = Math.Min((int)Math.Round(820 * scale), workArea.Height);
            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                workArea.X + (workArea.Width - width) / 2,
                workArea.Y + (workArea.Height - height) / 2,
                width,
                height));
            UpdateMinimumWindowSize();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(MainWindow), ex);
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args) => UpdateMinimumWindowSize();

    private void UpdateMinimumWindowSize()
    {
        try
        {
            var dpi = Math.Max(96u, GetDpiForWindow(WindowNative.GetWindowHandle(this)));
            if (dpi == _windowDpi || _appWindow?.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            _windowDpi = dpi;
            var scale = dpi / 96.0;
            var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            // AppWindow dimensions use physical pixels, unlike the page's logical dimensions.
            presenter.PreferredMinimumWidth = Math.Min((int)Math.Ceiling(MinWindowWidthDip * scale), workArea.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)Math.Ceiling(MinWindowHeightDip * scale), workArea.Height);
        }
        catch (Exception ex)
        {
            _windowDpi = 0;
            Log.Error(nameof(MainWindow), ex);
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var assetIconPath = Path.Combine(baseDir, "Assets", "icon.ico");
            if (File.Exists(assetIconPath))
            {
                AppWindow.SetIcon(assetIconPath);
                return;
            }

            var rootIconPath = Path.Combine(baseDir, "icon.ico");
            if (File.Exists(rootIconPath))
            {
                AppWindow.SetIcon(rootIconPath);
                return;
            }

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                try
                {
                    AppWindow.SetIcon(processPath);
                    return;
                }
                catch
                {
                }
            }

            var embeddedIconPath = EnsureEmbeddedIconOnDisk();
            if (!string.IsNullOrWhiteSpace(embeddedIconPath) && File.Exists(embeddedIconPath))
            {
                AppWindow.SetIcon(embeddedIconPath);
            }
        }
        catch
        {
        }
    }

    private static string? EnsureEmbeddedIconOnDisk()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_extractedEmbeddedIconPath) && File.Exists(_extractedEmbeddedIconPath))
            {
                return _extractedEmbeddedIconPath;
            }

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedIconResourceName);
            if (stream is null)
            {
                return null;
            }

            var iconDirectory = Path.Combine(Path.GetTempPath(), "PulsarBattery");
            Directory.CreateDirectory(iconDirectory);

            var iconPath = Path.Combine(iconDirectory, "icon.ico");
            using (var output = File.Create(iconPath))
            {
                stream.CopyTo(output);
            }

            _extractedEmbeddedIconPath = iconPath;
            return _extractedEmbeddedIconPath;
        }
        catch
        {
            return null;
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.IsShutdownComplete)
        {
            return;
        }

        args.Cancel = true;
        if (App.IsExitRequested)
        {
            return;
        }

        if (Services.AppSettingsService.Current.MinimizeToTrayOnClose && App.IsTrayAvailable)
        {
            try
            {
                _viewModel.SetWindowActive(false);
                sender.Hide();
                EfficiencyMode.Set(true);
            }
            catch (Exception ex)
            {
                Log.Error(nameof(MainWindow), ex);
            }

            return;
        }

        // Keep the dispatcher alive until the final history snapshot is persisted.
        DispatcherQueue.TryEnqueue(App.ExitApplication);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isStopping || App.IsExitRequested)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            NavigateTo("settings");
            return;
        }

        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigationAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isStopping || App.IsExitRequested)
        {
            return;
        }

        NavView.SelectedItem = sender.Key switch
        {
            VirtualKey.Number1 => DashboardItem,
            VirtualKey.Number2 => MouseItem,
            VirtualKey.Number3 => HistoryItem,
            VirtualKey.Number4 => NavView.SettingsItem,
            _ => NavView.SelectedItem
        };
        args.Handled = true;
    }

    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isStopping && !App.IsExitRequested)
        {
            _viewModel.RefreshNow();
        }

        args.Handled = true;
    }

    /// <summary>Selecting the item also updates the navigation highlight.</summary>
    internal void SelectHistoryTab() => NavView.SelectedItem = HistoryItem;

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "mouse" => typeof(MouseSettingsPage),
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
