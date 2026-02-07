using Microsoft.UI.Xaml;
using PulsarBattery.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;

namespace PulsarBattery
{
    public partial class App : Application
    {
        internal static bool IsExitRequested { get; private set; }
        internal static Window? MainWindow { get; private set; }

        private Window? _window;
        private readonly BatteryMonitor _monitor = new();
        private TrayIcon? _trayIcon;

        public App()
        {
            InitializeComponent();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => NotificationHelper.Unregister();
        }

        internal static void RequestExit()
        {
            IsExitRequested = true;
        }

        internal static void ExitApplication()
        {
            RequestExit();

            try
            {
                MainWindow?.Close();
            }
            catch
            {
                // ignore
            }

            Current?.Exit();
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
            MainWindow = _window;
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
            _trayIcon = new TrayIcon();
            if (_window is MainWindow mainWindow)
            {
                _trayIcon.Initialize(_window, mainWindow.ViewModel);
            }
            else
            {
                _trayIcon.Initialize(_window);
            }

            // Some WinUI scenarios require an explicit creation call.
            try
            {
                _trayIcon.ForceCreate();
            }
            catch
            {
                // ignore
            }

            var sourceExeToDelete = SelfInstallService.TryGetCleanupSourceExePath(Environment.GetCommandLineArgs());
            if (!string.IsNullOrWhiteSpace(sourceExeToDelete))
            {
                _ = Task.Run(async () => await TryDeleteFileWithRetriesAsync(sourceExeToDelete));
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

        private static async Task TryDeleteFileWithRetriesAsync(string path)
        {
            try
            {
                var currentProcessPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(currentProcessPath) &&
                    string.Equals(Path.GetFullPath(currentProcessPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch
            {
                // ignore
            }

            for (var attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    File.Delete(path);
                    if (!File.Exists(path))
                    {
                        return;
                    }
                }
                catch
                {
                    // retry
                }

                try
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
            }
        }
    }
}
