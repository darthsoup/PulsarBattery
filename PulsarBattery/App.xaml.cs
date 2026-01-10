using Microsoft.UI.Xaml;
using PulsarBattery.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PulsarBattery
{
    public partial class App : Application
    {
        internal static bool IsExitRequested { get; private set; }

        private Window? _window;
        private readonly BatteryMonitor _monitor = new();
        private TrayIconService? _trayIcon;

        public App()
        {
            InitializeComponent();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => NotificationHelper.Unregister();
        }

        internal static void RequestExit()
        {
            IsExitRequested = true;
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                await AppSettingsService.InitializeAsync();
            }
            catch
            {
                // ignore
            }

            NotificationHelper.Init();

            _monitor.Start();

            _window = new MainWindow();
            _window.Closed += (_, _) =>
            {
                try
                {
                    if (_window is MainWindow mw)
                    {
                        mw.Stop();
                    }
                }
                catch
                {
                    // ignore
                }

                _monitor.Dispose();
                _trayIcon?.Dispose();
            };

            var startInTray = ShouldStartInTray(args);
            if (!startInTray)
            {
                _window.Activate();
            }

            // Create tray icon once the window exists (it can remain hidden on startup).
            _trayIcon = new TrayIconService();
            _trayIcon.Initialize(_window);

            // Some WinUI scenarios require an explicit creation call.
            try
            {
                _trayIcon.ForceCreate();
            }
            catch
            {
                // ignore
            }
        }

        private static bool ShouldStartInTray(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                var raw = args.Arguments ?? string.Empty;
                if (raw.IndexOf("--background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    raw.IndexOf("--tray", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                return Environment.GetCommandLineArgs().Any(static arg =>
                    string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}
