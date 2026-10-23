using PulsarBattery.Tools;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

internal static class AppSettingsService
{
    private static readonly SettingsStore Store = new();
    private static readonly object Gate = new();

    private static AppSettings _current = AppSettings.Sanitize(AppSettings.CreateDefaultsFromEnvironment());
    private static CancellationTokenSource? _pendingSave;
    private static long _version;
    private static long _persistedVersion;

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
        long initialVersion;
        lock (Gate)
        {
            initialVersion = _version;
        }

        AppSettings? loaded = null;
        try
        {
            loaded = await Store.TryLoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(nameof(AppSettingsService), ex);
        }

        lock (Gate)
        {
            if (_version == initialVersion)
            {
                _current = AppSettings.Sanitize(loaded ?? AppSettings.CreateDefaultsFromEnvironment());
            }
        }
    }

    public static void Update(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (Gate)
        {
            var updated = AppSettings.Sanitize(update(_current));
            if (updated == _current)
            {
                return;
            }

            _current = updated;
            _version++;
            ScheduleSave_NoLock(updated, _version);
        }
    }

    /// <summary>Persists unsaved settings immediately, including changes inside the debounce window.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            if (_version == _persistedVersion)
            {
                return;
            }

            _pendingSave?.Cancel();
            _pendingSave = null;

            try
            {
                // Holding Gate prevents a newer update from being overwritten by this exit flush.
                // Store uses ConfigureAwait(false) and never takes Gate.
                Store.SaveAsync(_current).GetAwaiter().GetResult();
                _persistedVersion = _version;
            }
            catch (Exception ex)
            {
                Log.Error(nameof(AppSettingsService), ex);
            }
        }
    }

    private static void ScheduleSave_NoLock(AppSettings snapshot, long version)
    {
        _pendingSave?.Cancel();

        var pendingSave = new CancellationTokenSource();
        _pendingSave = pendingSave;
        var token = pendingSave.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false);
                await Store.SaveAsync(snapshot, token).ConfigureAwait(false);
                lock (Gate)
                {
                    _persistedVersion = Math.Max(_persistedVersion, version);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Error(nameof(AppSettingsService), ex);
            }
            finally
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_pendingSave, pendingSave))
                    {
                        _pendingSave = null;
                    }

                    pendingSave.Dispose();
                }
            }
        });
    }
}

