using PulsarBattery.Tools;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PulsarBattery.Services;

internal sealed record AlarmAudioOutput(string Id, bool IsEnabled, Func<Uri, IAlarmPlayback> CreatePlayback);

internal interface IAlarmPlayback : IDisposable
{
    event EventHandler? Completed;
    void Play();
}

internal sealed class AlarmSoundPlayer(
    Func<Task<IReadOnlyList<AlarmAudioOutput>>> enumerateOutputs,
    Func<Uri, IAlarmPlayback> createDefaultPlayback) : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<IAlarmPlayback> _playbacks = new(ReferenceEqualityComparer.Instance);
    private long _generation;
    private bool _disposed;
    private Task _pendingStart = Task.CompletedTask;

    internal Task PendingStart
    {
        get { lock (_gate) return _pendingStart; }
    }

    public bool Play(Uri soundUri, bool allOutputs)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            StopLocked();
            var generation = _generation;
            if (!allOutputs)
            {
                _pendingStart = Task.CompletedTask;
                return TryStartLocked(soundUri, createDefaultPlayback, generation);
            }

            // Enumeration must not block the UI or delay Stop while Windows resolves endpoints.
            _pendingStart = Task.Run(() => PlayOnAllOutputsAsync(soundUri, generation));
            return true;
        }
    }

    private async Task PlayOnAllOutputsAsync(Uri soundUri, long generation)
    {
        IReadOnlyList<AlarmAudioOutput> outputs;
        try
        {
            outputs = await enumerateOutputs().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(nameof(AlarmSoundPlayer), $"Audio output enumeration failed: {ex}");
            outputs = Array.Empty<AlarmAudioOutput>();
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var started = false;
        foreach (var output in outputs)
        {
            if (!output.IsEnabled || string.IsNullOrWhiteSpace(output.Id) || !ids.Add(output.Id))
            {
                continue;
            }

            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    return;
                }

                started |= TryStartLocked(soundUri, output.CreatePlayback, generation);
            }
        }

        if (!started)
        {
            lock (_gate)
            {
                TryStartLocked(soundUri, createDefaultPlayback, generation);
            }
        }
    }

    private bool TryStartLocked(Uri soundUri, Func<Uri, IAlarmPlayback> createPlayback, long generation)
    {
        if (_disposed || generation != _generation)
        {
            return false;
        }

        IAlarmPlayback? playback = null;
        try
        {
            playback = createPlayback(soundUri);
            playback.Completed += OnPlaybackCompleted;
            _playbacks.Add(playback);
            playback.Play();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(nameof(AlarmSoundPlayer), $"Starting alarm on an audio output failed: {ex}");
            if (playback is not null)
            {
                _playbacks.Remove(playback);
                ReleasePlayback(playback);
            }

            return false;
        }
    }

    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (sender is IAlarmPlayback playback && _playbacks.Remove(playback))
            {
                ReleasePlayback(playback);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _generation++;
        var playbacks = new List<IAlarmPlayback>(_playbacks);
        _playbacks.Clear();
        foreach (var playback in playbacks)
        {
            ReleasePlayback(playback);
        }
    }

    private void ReleasePlayback(IAlarmPlayback playback)
    {
        try
        {
            playback.Completed -= OnPlaybackCompleted;
        }
        catch (Exception ex)
        {
            Log.Warn(nameof(AlarmSoundPlayer), $"Removing audio completion handler failed: {ex.Message}");
        }

        try
        {
            playback.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(nameof(AlarmSoundPlayer), $"Releasing an alarm audio output failed: {ex}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }
    }
}
