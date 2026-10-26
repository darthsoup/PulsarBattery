using PulsarBattery.Tools;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;

namespace PulsarBattery.Services;

internal static class WindowsAlarmAudio
{
    public static IAlarmPlayback CreateDefaultPlayback(Uri soundUri) => new Playback(soundUri, null);

    public static async Task<IReadOnlyList<AlarmAudioOutput>> EnumerateOutputsAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        var outputs = new List<AlarmAudioOutput>(devices.Count);
        foreach (var device in devices)
        {
            outputs.Add(new AlarmAudioOutput(device.Id, device.IsEnabled, uri => new Playback(uri, device)));
        }

        Log.Info(nameof(WindowsAlarmAudio), $"Found {outputs.Count} audio outputs for the low-battery alarm.");
        return outputs;
    }

    private sealed class Playback : IAlarmPlayback
    {
        private MediaPlayer? _player;
        private MediaSource? _source;
        private readonly string _outputName;
        private int _completionPosted;
        private int _disposed;

        public event EventHandler? Completed;

        public Playback(Uri soundUri, DeviceInformation? device)
        {
            _outputName = device?.Name ?? "Windows default output";
            try
            {
                _player = new MediaPlayer
                {
                    AudioCategory = MediaPlayerAudioCategory.Alerts,
                    AutoPlay = false,
                    IsLoopingEnabled = false,
                };
                _player.CommandManager.IsEnabled = false;
                if (device is not null)
                {
                    _player.AudioDevice = device;
                }

                _player.MediaOpened += OnMediaOpened;
                _player.MediaEnded += OnMediaEnded;
                _player.MediaFailed += OnMediaFailed;
                _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
                _source = MediaSource.CreateFromUri(soundUri);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Play()
        {
            var player = _player ?? throw new ObjectDisposedException(nameof(Playback));
            player.Source = _source;
            player.Play();
            try
            {
                Log.Info(nameof(WindowsAlarmAudio), $"Low-battery sound requested on '{_outputName}'; sound level={player.AudioStateMonitor.SoundLevel}.");
            }
            catch (Exception ex)
            {
                Log.Warn(nameof(WindowsAlarmAudio), $"Sound started; audio-level diagnostics unavailable: {ex.Message}");
            }
        }

        private void OnMediaOpened(MediaPlayer sender, object args) =>
            Log.Info(nameof(WindowsAlarmAudio), $"Low-battery sound opened on '{_outputName}'.");

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                Log.Info(nameof(WindowsAlarmAudio), $"Low-battery audio on '{_outputName}' state={sender.PlaybackState}.");
            }
            catch (Exception ex)
            {
                Log.Warn(nameof(WindowsAlarmAudio), $"Audio state unavailable during teardown: {ex.Message}");
            }
        }

        private void OnMediaEnded(MediaPlayer sender, object args) => PostCompletion();

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Log.Error(nameof(WindowsAlarmAudio), $"Low-battery sound failed on '{_outputName}': {args.ErrorMessage}; error={args.ExtendedErrorCode}");
            PostCompletion();
        }

        private void PostCompletion()
        {
            if (Interlocked.Exchange(ref _completionPosted, 1) != 0)
            {
                return;
            }

            // Release the native callback before disposal can wait for its player resources.
            _ = Task.Run(() => Completed?.Invoke(this, EventArgs.Empty));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var player = _player;
            _player = null;
            var source = _source;
            _source = null;
            if (player is not null)
            {
                try
                {
                    player.MediaOpened -= OnMediaOpened;
                    player.MediaEnded -= OnMediaEnded;
                    player.MediaFailed -= OnMediaFailed;
                    player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
                    player.Pause();
                    player.Source = null;
                }
                catch (Exception ex)
                {
                    Log.Warn(nameof(WindowsAlarmAudio), $"Stopping audio on '{_outputName}' failed: {ex.Message}");
                }

                try
                {
                    player.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn(nameof(WindowsAlarmAudio), $"Disposing audio on '{_outputName}' failed: {ex.Message}");
                }
            }

            try
            {
                source?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(nameof(WindowsAlarmAudio), $"Disposing alarm source failed: {ex.Message}");
            }
        }
    }
}
