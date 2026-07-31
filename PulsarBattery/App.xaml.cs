using Microsoft.UI.Xaml;
using PulsarBattery.Services;
using PulsarBattery.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;

namespace PulsarBattery;

public partial class App : Application
{
    private const string AppUserModelId = "PulsarBattery.Desktop";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    internal static bool IsExitRequested { get; private set; }
    internal static Window? MainWindow { get; private set; }

    private Window? _window;
    private readonly BatteryMonitor _monitor = new();
    private TrayIcon? _trayIcon;

    public App()
    {
        Log.Initialize(HasCommandLineArg("--verbose"));
        TrySetAppUserModelId();
        InitializeComponent();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            NotificationHelper.Unregister();
            // Backstop for exit paths that bypass ExitApplication (e.g. closing the
            // window with "minimize to tray" disabled).
            AppSettingsService.Flush();
        };
    }

    private static bool HasCommandLineArg(string name)
    {
        try
        {
            return Environment.GetCommandLineArgs().Any(arg =>
                string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetAppUserModelId()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // ignore
        }
    }

    internal static void RequestExit()
    {
        IsExitRequested = true;
    }

    internal static void ExitApplication()
    {
        RequestExit();
        AppSettingsService.Flush();

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
        catch (Exception ex)
        {
            // The app still starts with default settings, but leave a trace.
            Log.Error(nameof(App), ex);
        }

        // Initialize after settings are loaded so a saved language override applies.
        LocalizationService.Initialize(AppSettingsService.Current.Language);

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

        // Some WinUI scenarios require an explicit creation call. Only drop
        // into efficiency mode when starting hidden in the tray.
        try
        {
            _trayIcon.ForceCreate(enablesEfficiencyMode: startInTray);
        }
        catch (Exception ex)
        {
            Log.Error(nameof(App), ex);
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
