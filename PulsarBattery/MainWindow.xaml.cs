using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PulsarBattery.Pages;
using PulsarBattery.Services;
using PulsarBattery.ViewModels;
using System;
using WinRT.Interop;

namespace PulsarBattery
{
    public sealed partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel = new();
        private AppWindow? _appWindow;
        private bool _isResizing;

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            SetDefaultWindowSize();

            var iconSource = TrayIconService.CreateTitleBarIconSource();
            if (iconSource is not null)
            {
                AppTitleBar.IconSource = iconSource;
            }

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
                _viewModel.RefreshNow();
            }
        }

        private void SetDefaultWindowSize()
        {
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

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (App.IsExitRequested)
            {
                return;
            }

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
