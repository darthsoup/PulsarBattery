using PulsarBattery.Tools;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

internal static class AppSettingsService
{
    private static readonly SettingsStore Store = new();
    private static readonly object Gate = new();

    private static AppSettings _current = AppSettings.CreateDefaultsFromEnvironment();
    private static CancellationTokenSource? _pendingSave;

    public static AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AppSettings? loaded = null;
        try
        {
            loaded = await Store.TryLoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(nameof(AppSettingsService), ex);
        }

        lock (Gate)
        {
            _current = AppSettings.Sanitize(loaded ?? AppSettings.CreateDefaultsFromEnvironment());
        }
    }

    public static void Update(Func<AppSettings, AppSettings> update)
    {
        AppSettings updated;

        lock (Gate)
        {
            updated = AppSettings.Sanitize(update(_current));
            _current = updated;
            ScheduleSave_NoLock(updated);
        }
    }

    /// <summary>
    /// Cancels any pending debounced save and writes the current settings immediately.
    /// Call on app exit so changes made within the debounce window are not lost.
    /// </summary>
    public static void Flush()
    {
        AppSettings snapshot;

        lock (Gate)
        {
            if (_pendingSave is null)
            {
                // No Update() has ever run, so there is nothing to persist.
                return;
            }

            _pendingSave.Cancel();
            _pendingSave.Dispose();
            _pendingSave = null;
            snapshot = _current;
        }

        try
        {
            // SettingsStore awaits with ConfigureAwait(false) throughout, so blocking here
            // cannot deadlock the UI thread; the write is a tiny local file.
            Store.SaveAsync(snapshot).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(AppSettingsService), ex);
        }
    }

    private static void ScheduleSave_NoLock(AppSettings snapshot)
    {
        _pendingSave?.Cancel();
        _pendingSave?.Dispose();

        _pendingSave = new CancellationTokenSource();
        var token = _pendingSave.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false);
                await Store.SaveAsync(snapshot, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error(nameof(AppSettingsService), ex);
            }
        }, token);
    }
}

