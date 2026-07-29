#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Stable facade for song and sample output routing.
    /// Lifecycle and route mutations are owned by the main thread. Native BASS calls are never
    /// made while a managed registry lock is held.
    /// </summary>
    internal sealed class BassAudioOutput : IDisposable
    {
        private readonly Action _asioReinitializeRequested;
        private readonly HashSet<BassSongPlayback> _playbacks = new();
        private readonly HashSet<BassMonitorRoute> _monitorRoutes = new();
        private IBassOutputBackend? _backend;
        private int _outputDeviceId = -1;
        private double _volume = 1;
        private bool _disposed;

        public int HeardLatencyMilliseconds => _backend?.HeardLatencyMilliseconds ?? 0;
        public BassAudioOutput(Action asioReinitializeRequested)
        {
            _asioReinitializeRequested = asioReinitializeRequested;
        }

        public bool InitializeForDevice(BassOutputDevice device, int asioBufferLength)
        {
            if (!InitializeBackend(device, asioBufferLength))
            {
                return false;
            }

            if (AttachMonitorRoutes(device.DeviceId))
            {
                return true;
            }

            DisposeBackend();
            return false;
        }

        private bool InitializeBackend(BassOutputDevice device, int asioBufferLength)
        {
            if (_disposed || _backend != null)
            {
                return false;
            }

            IBassOutputBackend backend = device.IsAsio
                ? new BassAsioOutputBackend(asioBufferLength, _asioReinitializeRequested)
                : new BassDeviceOutputBackend();
            if (!backend.Initialize(device))
            {
                backend.Dispose();
                return false;
            }

            backend.SetVolume(_volume);
            _backend = backend;
            _outputDeviceId = device.DeviceId;
            return true;
        }

        public void Suspend()
        {
            if (_backend == null)
            {
                return;
            }

            DetachMonitorRoutes();
            foreach (var playback in _playbacks)
            {
                playback.PrepareForOutputChange();
                _backend.DetachSong(playback.TempoStreamHandle);
            }
            DisposeBackend();
        }

        public bool Resume(BassOutputDevice device, int asioBufferLength)
        {
            if (!InitializeBackend(device, asioBufferLength))
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
                    DisposeBackend();
                    return false;
                }
            }

            if (!AttachMonitorRoutes(device.DeviceId))
            {
                foreach (var attachedPlayback in _playbacks)
                {
                    _backend!.DetachSong(attachedPlayback.TempoStreamHandle);
                }
                DisposeBackend();
                return false;
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

        public BassMonitorRoute? RegisterMonitor(BassMonitorSource source, double volume)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }
            if (_disposed)
            {
                return null;
            }
            foreach (var existingRoute in _monitorRoutes)
            {
                if (existingRoute.Source.Handle == source.Handle)
                {
                    YargLogger.LogFormatError(
                        "Monitor source {0} is already registered", source.Handle);
                    return null;
                }
            }

            var route = new BassMonitorRoute(this, source, volume);
            _monitorRoutes.Add(route);
            if (_backend == null)
            {
                return route;
            }

            int originalDevice = source.GetDevice();
            if (originalDevice < 0)
            {
                YargLogger.LogFormatError("Failed to get monitor source device: {0}",
                    Bass.LastError);
            }
            if (originalDevice >= 0 && source.MoveToDevice(_outputDeviceId) &&
                source.ResetToLive() && _backend.AttachMonitor(source.Handle, volume))
            {
                route.IsAttached = true;
                return route;
            }

            if (originalDevice >= 0)
            {
                source.MoveToDevice(originalDevice);
            }
            _monitorRoutes.Remove(route);
            route.InvalidateOwner();
            return null;
        }

        internal void Remove(BassMonitorRoute route)
        {
            if (!_monitorRoutes.Contains(route))
            {
                return;
            }
            if (route.IsAttached)
            {
                _backend?.DetachMonitor(route.Source.Handle);
                route.IsAttached = false;
            }
            _monitorRoutes.Remove(route);
        }

        internal void SetMonitorVolume(BassMonitorRoute route, double volume)
        {
            if (_monitorRoutes.Contains(route) && route.IsAttached)
            {
                _backend?.SetMonitorVolume(route.Source.Handle, volume);
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
        internal long GetSongPosition(int tempoStreamHandle) =>
            _backend?.GetSongPosition(tempoStreamHandle) ?? -1;
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

        internal IReadOnlyList<AsioInputDescriptor> GetAsioInputDescriptors() =>
            _backend is BassAsioOutputBackend asioBackend
                ? asioBackend.GetInputDescriptors()
                : Array.Empty<AsioInputDescriptor>();

        internal AsioInputAcquireResult TryAcquireAsioInput(string driverId, int channelIndex,
            out BassAsioInputLease? lease)
        {
            lease = null;
            return _backend is BassAsioOutputBackend asioBackend
                ? asioBackend.TryAcquireInput(driverId, channelIndex, out lease)
                : AsioInputAcquireResult.NoAsioBackend;
        }

        internal bool TryGetAsioInputLevel(int channelIndex, out double level)
        {
            if (_backend is BassAsioOutputBackend asioBackend)
            {
                return asioBackend.TryGetInputLevel(channelIndex, out level);
            }
            level = 0;
            return false;
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

        private bool AttachMonitorRoutes(int deviceId)
        {
            if (_backend == null || _monitorRoutes.Count == 0)
            {
                return true;
            }

            var routes = new List<(BassMonitorRoute Route, int OriginalDevice)>(_monitorRoutes.Count);
            foreach (var route in _monitorRoutes)
            {
                int originalDevice = route.Source.GetDevice();
                if (originalDevice < 0)
                {
                    YargLogger.LogFormatError(
                        "Failed to get monitor source device: {0}", Bass.LastError);
                    return false;
                }
                routes.Add((route, originalDevice));
            }

            int migratedCount = 0;
            int attachedCount = 0;
            for (int i = 0; i < routes.Count; i++)
            {
                var route = routes[i].Route;
                if (!route.Source.MoveToDevice(deviceId))
                {
                    break;
                }
                migratedCount++;

                if (!route.Source.ResetToLive() ||
                    !_backend.AttachMonitor(route.Source.Handle, route.Volume))
                {
                    break;
                }
                route.IsAttached = true;
                attachedCount++;
            }

            if (attachedCount == routes.Count)
            {
                return true;
            }

            for (int i = attachedCount - 1; i >= 0; i--)
            {
                var route = routes[i].Route;
                _backend.DetachMonitor(route.Source.Handle);
                route.IsAttached = false;
            }
            for (int i = migratedCount - 1; i >= 0; i--)
            {
                routes[i].Route.Source.MoveToDevice(routes[i].OriginalDevice);
            }
            return false;
        }

        private void DetachMonitorRoutes()
        {
            if (_backend == null)
            {
                return;
            }
            foreach (var route in _monitorRoutes)
            {
                if (!route.IsAttached)
                {
                    continue;
                }
                _backend.DetachMonitor(route.Source.Handle);
                route.IsAttached = false;
            }
        }

        private void DisposeBackend()
        {
            _backend?.Dispose();
            _backend = null;
            _outputDeviceId = -1;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ResetForDeviceChange();
            foreach (var route in _monitorRoutes)
            {
                route.InvalidateOwner();
            }
            _monitorRoutes.Clear();
        }
    }
}
