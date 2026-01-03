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
        catch
        {
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
            catch
            {
            }
        }, token);
    }
}

