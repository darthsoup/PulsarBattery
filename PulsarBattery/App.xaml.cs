using Microsoft.UI.Xaml;
using PulsarBattery.Services;
using PulsarBattery.Tools;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PulsarBattery;

public partial class App : Application
{
    private const string AppUserModelId = "PulsarBattery.Desktop";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    internal static bool IsExitRequested { get; private set; }
    internal static bool IsShutdownComplete { get; private set; }
    internal static bool IsTrayAvailable => Current is App app && app._trayIcon?.IsAvailable == true;
    internal static Window? MainWindow { get; private set; }

    private Window? _window;
    private readonly BatteryMonitor _monitor = new();
    private TrayIcon? _trayIcon;
    private bool _shutdownStarted;
    private bool _resourcesDisposed;

    public App()
    {
        Log.Initialize(HasCommandLineArg("--verbose"));
        TrySetAppUserModelId();
        InitializeComponent();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            NotificationHelper.Unregister();
            // Process termination can bypass the orderly window-close path.
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
        }
    }

    internal static void RequestExit()
    {
        IsExitRequested = true;
    }

    internal static async void ExitApplication()
    {
        if (MainWindow?.DispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(ExitApplication);
            return;
        }

        if (Current is not App app || app._shutdownStarted)
        {
            return;
        }

        app._shutdownStarted = true;
        RequestExit();

        try
        {
            app._monitor.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(App), ex);
        }

        try
        {
            if (MainWindow is global::PulsarBattery.MainWindow mainWindow)
            {
                await mainWindow.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(nameof(App), ex);
        }

        AppSettingsService.Flush();
        app.DisposeResources();
        IsShutdownComplete = true;

        try
        {
            MainWindow?.Close();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(App), ex);
        }

        app.Exit();
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        try
        {
            _monitor.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(App), ex);
        }

        try
        {
            _trayIcon?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(App), ex);
        }

        NotificationHelper.Unregister();
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

        _window = new MainWindow();
        MainWindow = _window;
        _window.Closed += (_, _) =>
        {
            RequestExit();
            try
            {
                if (!IsShutdownComplete && _window is MainWindow mw)
                {
                    mw.Stop();
                }
            }
            catch (Exception ex)
            {
                Log.Error(nameof(App), ex);
            }

            AppSettingsService.Flush();
            DisposeResources();
            MainWindow = null;
        };

        _monitor.Start();

        var startInTray = ShouldStartInTray();
        if (!startInTray)
        {
            _window.Activate();
        }

        try
        {
            _trayIcon = new TrayIcon();
            _trayIcon.Initialize(_window, ((MainWindow)_window).ViewModel);
            _trayIcon.ForceCreate(enablesEfficiencyMode: startInTray);
        }
        catch (Exception ex)
        {
            Log.Error(nameof(App), ex);
        }

        // A hidden window must remain reachable even when Explorer rejects the tray icon.
        if (startInTray && !IsTrayAvailable)
        {
            ((MainWindow)_window).ShowAndActivate();
        }

        _ = Task.Run(async () =>
        {
            var sourceExeToDelete = SelfInstallService.TryGetCleanupSourceExePath(Environment.GetCommandLineArgs());
            if (!string.IsNullOrWhiteSpace(sourceExeToDelete))
            {
                await TryDeleteFileWithRetriesAsync(sourceExeToDelete);
            }
        });
    }

    private static bool ShouldStartInTray()
    {
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
