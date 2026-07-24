#nullable enable
using System;
using System.Collections.Generic;
using YARG.Core.Audio;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Stable facade for song and sample output routing.
    /// </summary>
    internal sealed class BassAudioOutput : IDisposable
    {
        private readonly HashSet<BassSongPlayback> _playbacks = new();
        private IBassOutputBackend? _backend;
        private double _volume = 1;
        private bool _disposed;

        public int HeardLatencyMilliseconds => _backend?.HeardLatencyMilliseconds ?? 0;

        public bool InitializeForDevice(BassOutputDevice device, int asioBufferLength)
        {
            if (_disposed)
            {
                return false;
            }

            IBassOutputBackend backend = device.IsAsio
                ? new BassAsioOutputBackend(asioBufferLength)
                : new BassDeviceOutputBackend();
            if (!backend.Initialize(device))
            {
                backend.Dispose();
                return false;
            }

            backend.SetVolume(_volume);
            _backend = backend;
            return true;
        }

        public void Suspend()
        {
            if (_backend == null)
            {
                return;
            }

            foreach (var playback in _playbacks)
            {
                playback.PrepareForOutputChange();
                _backend.DetachSong(playback.TempoStreamHandle);
            }
            _backend.Dispose();
            _backend = null;
        }

        public bool Resume(BassOutputDevice device, int asioBufferLength)
        {
            if (!InitializeForDevice(device, asioBufferLength))
            {
                return false;
            }

            foreach (var playback in _playbacks)
            {
                if (!_backend!.AttachSong(playback.TempoStreamHandle))
                {
                    foreach (var attachedPlayback in _playbacks)
                    {
                        _backend.DetachSong(attachedPlayback.TempoStreamHandle);
                    }
                    _backend.Dispose();
                    _backend = null;
                    return false;
                }
            }
            foreach (var playback in _playbacks)
            {
                playback.RestoreAfterOutputChange();
            }
            return true;
        }

        public BassSongPlayback CreateSongPlayback(int tempoStreamHandle)
        {
            var playback = new BassSongPlayback(tempoStreamHandle, this);
            if (_backend == null || !_backend.AttachSong(tempoStreamHandle))
            {
                return playback;
            }

            playback.MarkValid();
            _playbacks.Add(playback);
            return playback;
        }

        internal void Remove(BassSongPlayback playback)
        {
            if (_playbacks.Remove(playback))
            {
                _backend?.DetachSong(playback.TempoStreamHandle);
            }
        }

        internal bool IsSongPlaying(int tempoStreamHandle) =>
            _backend?.IsSongPlaying(tempoStreamHandle) == true;
        internal int PlaySong(int tempoStreamHandle, bool restart) =>
            _backend?.PlaySong(tempoStreamHandle, restart) ?? -1;
        internal int PauseSong(int tempoStreamHandle) => _backend?.PauseSong(tempoStreamHandle) ?? -1;
        internal void ResetSongAfterSeek(int tempoStreamHandle) => _backend?.ResetSongAfterSeek(tempoStreamHandle);
        internal void FadeSong(int tempoStreamHandle, double volume, int durationMilliseconds) =>
            _backend?.FadeSong(tempoStreamHandle, volume, durationMilliseconds);
        internal double GetSongVolume(int tempoStreamHandle) => _backend?.GetSongVolume(tempoStreamHandle) ?? 0;
        internal void SetSongVolume(int tempoStreamHandle, double volume) =>
            _backend?.SetSongVolume(tempoStreamHandle, volume);
        internal int GetSongData(int tempoStreamHandle, float[] buffer, int flags) =>
            _backend?.GetSongData(tempoStreamHandle, buffer, flags) ?? -1;
        internal int GetSongLevel(int tempoStreamHandle, float[] level) =>
            _backend?.GetSongLevel(tempoStreamHandle, level) ?? -1;
        internal double GetTempoCommandDelay(int tempoStreamHandle) =>
            _backend?.GetTempoCommandDelay(tempoStreamHandle) ?? 0;
        internal double GetPlaybackStartDelay() => _backend?.PlaybackStartDelay ?? 0;
        internal void SetSongBufferLength(int tempoStreamHandle, int length) =>
            _backend?.SetSongBufferLength(tempoStreamHandle, length);
        internal void SetSongOutputChannel(int tempoStreamHandle, OutputChannel? channel) =>
            _backend?.SetSongOutputChannel(tempoStreamHandle, channel);
        internal int GetSongMixerHandle(int tempoStreamHandle) =>
            _backend?.SongMixerHandle(tempoStreamHandle) ?? 0;
        internal bool OneShotStartsPaused(int tempoStreamHandle) =>
            _backend?.SongMixerRunsContinuously == true && !IsSongPlaying(tempoStreamHandle);

        public bool PlaySample(int sourceHandle, OutputChannel? outputChannel) =>
            _backend?.PlaySample(sourceHandle, outputChannel) == true;
        public void RemoveSample(int sourceHandle) => _backend?.RemoveSample(sourceHandle);
        public void SetSampleOutputChannel(int sourceHandle, OutputChannel? outputChannel) =>
            _backend?.SetSampleOutputChannel(sourceHandle, outputChannel);

        public void SetVolume(double volume)
        {
            _volume = volume;
            _backend?.SetVolume(volume);
        }

        public void ResetForDeviceChange()
        {
            Suspend();
            foreach (var playback in _playbacks)
            {
                playback.Invalidate();
            }
            _playbacks.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ResetForDeviceChange();
        }
    }
}
