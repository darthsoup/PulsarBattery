using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.Pages;
using PulsarBattery.ViewModels;
using System;
using System.IO;
using System.Reflection;
using WinRT.Interop;

namespace PulsarBattery
{
    public sealed partial class MainWindow : Window
    {
        private const string EmbeddedIconResourceName = "PulsarBattery.Assets.icon.ico";

        private readonly MainViewModel _viewModel = new();
        private AppWindow? _appWindow;
        private bool _isResizing;
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

            Activated += MainWindow_Activated;

            NavView.SelectedItem = DashboardItem;
            NavigateTo("dashboard");
        }

        internal void Stop() => _viewModel.Stop();

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                EnsureAppWindowInitialized();
                _viewModel.RefreshNow();
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

                // A small utility window: good default.
                _appWindow.Resize(new Windows.Graphics.SizeInt32(900, 820));

                // Prevent excessive width. There's no MaxWidth API on AppWindow, so we clamp.
                _appWindow.Changed += AppWindow_Changed;
                _appWindow.Closing += AppWindow_Closing;
            }
            catch
            {
                // best-effort sizing
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
                        // fall back to embedded icon resource
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
                // best-effort icon setup
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
            if (App.IsExitRequested)
            {
                return;
            }

            // Check if minimize to tray on close is enabled
            var shouldMinimizeToTray = Services.AppSettingsService.Current.MinimizeToTrayOnClose;

            if (shouldMinimizeToTray)
            {
                args.Cancel = true;

                try
                {
                    sender.Hide();
                }
                catch
                {
                    // ignore
                }
            }
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_isResizing)
            {
                return;
            }

            // Clamp width to 1000px.
            if (args.DidSizeChange && sender.Size.Width > 1000)
            {
                try
                {
                    _isResizing = true;
                    sender.Resize(new Windows.Graphics.SizeInt32(1000, sender.Size.Height));
                }
                finally
                {
                    _isResizing = false;
                }
            }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                NavigateTo("settings");
                return;
            }

            var tag = (args.SelectedItemContainer?.Tag as string) ?? "dashboard";
            NavigateTo(tag);
        }

        private void NavigateTo(string tag)
        {
            HeaderTitle.Text = tag switch
            {
                "history" => "History",
                "settings" => "Settings",
                _ => "Dashboard"
            };

            var pageType = tag switch
            {
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
}
