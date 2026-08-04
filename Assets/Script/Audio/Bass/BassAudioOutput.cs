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
    ///
    /// The facade borrows an <see cref="IBassOutputBackend"/> from the active transport: it
    /// attaches routes to it and detaches before the transport tears it down. The facade never
    /// creates, selects, or disposes a backend.
    /// </summary>
    internal sealed class BassAudioOutput : IDisposable
    {
        private readonly HashSet<BassSongPlayback> _playbacks     = new();
        private readonly HashSet<BassMonitorRoute> _monitorRoutes = new();
        private          IBassOutputBackend?       _backend;
        private          int                       _outputDeviceId = -1;
        private          double                    _volume         = 1;
        private          bool                      _disposed;

        public int HeardLatencyMilliseconds => _backend?.HeardLatencyMilliseconds ?? 0;

        /// <summary>
        /// Detaches every route from the borrowed backend without disposing it. The owning
        /// transport remains responsible for tearing the backend down.
        /// </summary>
        public void SuspendRoutes()
        {
            if (_backend == null)
            {
                return;
            }

            DetachMonitorRoutes();
            SuspendSongPlaybacks();
        }

        /// <summary>
        /// Borrows an initialized backend from the active transport and reattaches all routes.
        /// Reapplies the cached master volume. On failure, rolls back to no backend.
        /// </summary>
        public bool AttachBackend(IBassOutputBackend backend, int deviceId)
        {
            if (_disposed || _backend != null)
            {
                return false;
            }

            _backend = backend;
            _outputDeviceId = deviceId;
            backend.SetVolume(_volume);

            if (AttachSongPlaybacks() && AttachMonitorRoutes(deviceId))
            {
                RestoreSongPlaybacks();
                return true;
            }

            DetachSongPlaybacks();
            DetachBackend();
            return false;
        }

        /// <summary>
        /// Drops the borrowed backend reference. Callers must have detached routes first and
        /// must keep the owning transport alive until this call completes.
        /// </summary>
        public void DetachBackend()
        {
            _backend = null;
            _outputDeviceId = -1;
        }

        public void ResetForDeviceChange()
        {
            SuspendRoutes();
            DetachBackend();
            foreach (var playback in _playbacks)
            {
                playback.Invalidate();
            }

            _playbacks.Clear();
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

            if (HasMonitorRouteFor(source.Handle))
            {
                YargLogger.LogFormatError("Monitor source {0} is already registered", source.Handle);
                return null;
            }

            var route = new BassMonitorRoute(this, source, volume);
            _monitorRoutes.Add(route);
            if (_backend == null)
            {
                return route;
            }

            if (TryAttachMonitorRoute(route))
            {
                return route;
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
                route.MarkDetached();
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

        internal bool IsSongPlaying(int tempoStreamHandle) => _backend?.IsSongPlaying(tempoStreamHandle) == true;

        internal int PlaySong(int tempoStreamHandle, bool restart) =>
            _backend?.PlaySong(tempoStreamHandle, restart) ?? -1;

        internal int PauseSong(int tempoStreamHandle) => _backend?.PauseSong(tempoStreamHandle) ?? -1;
        internal void PrepareSongForSeek(int tempoStreamHandle) => _backend?.PrepareSongForSeek(tempoStreamHandle);
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

        internal long GetSongPosition(int tempoStreamHandle) => _backend?.GetSongPosition(tempoStreamHandle) ?? -1;

        internal double GetTempoCommandDelay(int tempoStreamHandle) =>
            _backend?.GetTempoCommandDelay(tempoStreamHandle) ?? 0;

        internal double GetPlaybackStartDelay() => _backend?.PlaybackStartDelay ?? 0;

        internal void SetSongBufferLength(int tempoStreamHandle, int length) =>
            _backend?.SetSongBufferLength(tempoStreamHandle, length);

        internal void SetSongOutputChannel(int tempoStreamHandle, OutputChannel? channel) =>
            _backend?.SetSongOutputChannel(tempoStreamHandle, channel);

        internal int GetSongMixerHandle(int tempoStreamHandle) => _backend?.SongMixerHandle(tempoStreamHandle) ?? 0;

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

        private void SuspendSongPlaybacks()
        {
            foreach (var playback in _playbacks)
            {
                playback.PrepareForOutputChange();
                _backend!.DetachSong(playback.TempoStreamHandle);
            }
        }

        private bool AttachSongPlaybacks()
        {
            foreach (var playback in _playbacks)
            {
                if (!_backend!.AttachSong(playback.TempoStreamHandle))
                {
                    return false;
                }
            }

            return true;
        }

        private void DetachSongPlaybacks()
        {
            foreach (var playback in _playbacks)
            {
                _backend!.DetachSong(playback.TempoStreamHandle);
            }
        }

        private void RestoreSongPlaybacks()
        {
            foreach (var playback in _playbacks)
            {
                playback.RestoreAfterOutputChange();
            }
        }

        private bool HasMonitorRouteFor(int sourceHandle)
        {
            foreach (var route in _monitorRoutes)
            {
                if (route.Source.Handle == sourceHandle)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryAttachMonitorRoute(BassMonitorRoute route)
        {
            int originalDevice = route.Source.GetDevice();
            if (originalDevice < 0)
            {
                YargLogger.LogFormatError("Failed to get monitor source device: {0}", Bass.LastError);
                return false;
            }

            bool attached = route.Source.MoveToDevice(_outputDeviceId) && route.Source.ResetToLive() &&
                _backend!.AttachMonitor(route.Source.Handle, route.Volume);
            if (attached)
            {
                route.MarkAttached();
                return true;
            }

            route.Source.MoveToDevice(originalDevice);
            return false;
        }

        private bool AttachMonitorRoutes(int deviceId)
        {
            if (_backend == null || _monitorRoutes.Count == 0)
            {
                return true;
            }

            var routes = CaptureMonitorRouteDevices();
            if (routes == null)
            {
                return false;
            }

            int movedRouteCount = 0;
            int attachedRouteCount = 0;
            foreach (var (route, _) in routes)
            {
                if (!route.Source.MoveToDevice(deviceId))
                {
                    break;
                }

                movedRouteCount++;

                if (!route.Source.ResetToLive() || !_backend.AttachMonitor(route.Source.Handle, route.Volume))
                {
                    break;
                }

                route.MarkAttached();
                attachedRouteCount++;
            }

            if (attachedRouteCount == routes.Count)
            {
                return true;
            }

            RollbackMonitorRoutes(routes, attachedRouteCount, movedRouteCount);
            return false;
        }

        private List<(BassMonitorRoute Route, int OriginalDevice)>? CaptureMonitorRouteDevices()
        {
            var routes = new List<(BassMonitorRoute Route, int OriginalDevice)>(_monitorRoutes.Count);
            foreach (var route in _monitorRoutes)
            {
                int originalDevice = route.Source.GetDevice();
                if (originalDevice < 0)
                {
                    YargLogger.LogFormatError("Failed to get monitor source device: {0}", Bass.LastError);
                    return null;
                }

                routes.Add((route, originalDevice));
            }

            return routes;
        }

        private void RollbackMonitorRoutes(List<(BassMonitorRoute Route, int OriginalDevice)> routes,
            int attachedRouteCount, int movedRouteCount)
        {
            for (int i = attachedRouteCount - 1; i >= 0; i--)
            {
                var route = routes[i].Route;
                _backend!.DetachMonitor(route.Source.Handle);
                route.MarkDetached();
            }

            for (int i = movedRouteCount - 1; i >= 0; i--)
            {
                routes[i].Route.Source.MoveToDevice(routes[i].OriginalDevice);
            }
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
                route.MarkDetached();
            }
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
