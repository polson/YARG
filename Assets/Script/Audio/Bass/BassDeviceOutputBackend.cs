#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassDeviceOutputBackend : IBassOutputBackend
    {
        private const int IDLE_TIMEOUT_MILLISECONDS = 10_000;

        private readonly object _lock = new();
        private readonly Dictionary<int, int> _songMixers = new();
        private readonly HashSet<int> _activeSamples = new();
        private readonly Timer _idleTimer;
        private int _sampleMixerHandle;
        private int _monitorMixerHandle;
        private readonly HashSet<int> _monitors = new();
        private bool _disposed;
        public int HeardLatencyMilliseconds => Math.Max(0, Bass.Info.Latency);
        public bool SongMixerRunsContinuously => false;
        public double PlaybackStartDelay => BassLatencyProvider.StartupLatency;

        public BassDeviceOutputBackend()
        {
            _idleTimer = new Timer(OnIdleTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public bool Initialize(BassOutputDevice device)
        {
            if (Bass.Start())
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to start BASS output device: {0}", Bass.LastError);
            return false;
        }

        public bool AttachSong(int tempoStreamHandle)
        {
            var info = Bass.ChannelGetInfo(tempoStreamHandle);
            int mixerHandle = BassMix.CreateMixerStream(info.Frequency, info.Channels,
                BassFlags.Float | BassFlags.MixerNonStop);
            if (mixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create output mixer: {0}", Bass.LastError);
                return false;
            }

            if (!BassMix.MixerAddChannel(mixerHandle, tempoStreamHandle, BassFlags.MixerChanNoRampin))
            {
                YargLogger.LogFormatError("Failed to add tempo stream to output mixer: {0}", Bass.LastError);
                Bass.StreamFree(mixerHandle);
                return false;
            }

            _songMixers.Add(tempoStreamHandle, mixerHandle);
            return true;
        }

        public void DetachSong(int tempoStreamHandle)
        {
            if (!_songMixers.Remove(tempoStreamHandle, out int mixerHandle))
            {
                return;
            }

            if (!Bass.StreamFree(mixerHandle))
            {
                YargLogger.LogFormatError("Failed to free output mixer stream: {0}!", Bass.LastError);
            }
        }

        public int SongMixerHandle(int tempoStreamHandle) => _songMixers.GetValueOrDefault(tempoStreamHandle);

        public bool IsSongPlaying(int tempoStreamHandle)
        {
            return Bass.ChannelIsActive(SongMixerHandle(tempoStreamHandle)) is
                PlaybackState.Playing or PlaybackState.Stalled;
        }

        public int PlaySong(int tempoStreamHandle, bool restart)
        {
            int mixerHandle = SongMixerHandle(tempoStreamHandle);
            // A restart is preceded by ResetSongAfterSeek(), so resetting here would clear the
            // buffer a second time. Fill the playback buffer before resuming, then play without
            // another reset so a managed GC has more than the initial startup fill to absorb.
            Bass.ChannelUpdate(mixerHandle, 0);
            if (!Bass.ChannelPlay(mixerHandle, Restart: false))
            {
                return (int) Bass.LastError;
            }
            return 0;
        }

        public int PauseSong(int tempoStreamHandle)
        {
            return Bass.ChannelPause(SongMixerHandle(tempoStreamHandle)) ? 0 : (int) Bass.LastError;
        }

        public void PrepareSongForSeek(int tempoStreamHandle) { }

        public void ResetSongAfterSeek(int tempoStreamHandle)
        {
            if (!Bass.ChannelSetPosition(SongMixerHandle(tempoStreamHandle), 0, PositionFlags.Bytes))
            {
                YargLogger.LogFormatError("Failed to reset output mixer position: {0}!", Bass.LastError);
            }
        }

        public void FadeSong(int tempoStreamHandle, double volume, int durationMilliseconds)
        {
            Bass.ChannelSlideAttribute(SongMixerHandle(tempoStreamHandle), ChannelAttribute.Volume,
                (float) volume, durationMilliseconds);
        }

        public double GetSongVolume(int tempoStreamHandle)
        {
            Bass.ChannelGetAttribute(SongMixerHandle(tempoStreamHandle), ChannelAttribute.Volume,
                out float volume);
            return volume;
        }

        public void SetSongVolume(int tempoStreamHandle, double volume)
        {
            if (!Bass.ChannelSetAttribute(SongMixerHandle(tempoStreamHandle), ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set output mixer volume: {0}", Bass.LastError);
            }
        }

        public int GetSongData(int tempoStreamHandle, float[] buffer, int flags)
        {
            return Bass.ChannelGetData(SongMixerHandle(tempoStreamHandle), buffer, flags);
        }

        public int GetSongLevel(int tempoStreamHandle, float[] level)
        {
            var flags = LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS;
            return Bass.ChannelGetLevel(SongMixerHandle(tempoStreamHandle), level, 0.2f, flags)
                ? (int) Errors.OK
                : (int) Bass.LastError;
        }

        public double GetTempoCommandDelay(int tempoStreamHandle)
        {
            return BassLatencyProvider.GetTempoStreamLatency(SongMixerHandle(tempoStreamHandle));
        }

        public long GetSongPosition(int tempoStreamHandle) =>
            BassMix.ChannelGetPosition(tempoStreamHandle, PositionFlags.Bytes);

        public void SetSongBufferLength(int tempoStreamHandle, int length)
        {
            length = BassHelpers.ClampPlaybackBufferLength(length);
            if (!Bass.ChannelSetAttribute(SongMixerHandle(tempoStreamHandle), ChannelAttribute.Buffer,
                    length / 1000f))
            {
                YargLogger.LogFormatError("Failed to set playback buffer: {0}!", Bass.LastError);
            }
        }

        public void SetSongOutputChannel(int tempoStreamHandle, OutputChannel? channel)
        {
            BassHelpers.UpdateOutputChannels(SongMixerHandle(tempoStreamHandle), channel);
        }

        public bool AttachMonitor(int sourceHandle, double volume)
        {
            if (_monitors.Contains(sourceHandle))
            {
                return SetMonitorVolume(sourceHandle, volume);
            }
            if (!EnsureMonitorMixer() || !SetMonitorVolume(sourceHandle, volume))
            {
                return false;
            }

            var flags = BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin;
            if (!BassMix.MixerAddChannel(_monitorMixerHandle, sourceHandle, flags))
            {
                YargLogger.LogFormatError("Failed to add source to monitor mixer: {0}",
                    Bass.LastError);
                return false;
            }

            _monitors.Add(sourceHandle);
            return true;
        }

        public void DetachMonitor(int sourceHandle)
        {
            if (!_monitors.Remove(sourceHandle))
            {
                return;
            }
            if (!BassMix.MixerRemoveChannel(sourceHandle) && Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to remove source from monitor mixer: {0}",
                    Bass.LastError);
            }
        }

        public bool SetMonitorVolume(int sourceHandle, double volume)
        {
            if (Bass.ChannelSetAttribute(sourceHandle, ChannelAttribute.Volume, volume))
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to set monitor source volume: {0}", Bass.LastError);
            return false;
        }

        public bool PlaySample(int sourceHandle, OutputChannel? outputChannel)
        {
            lock (_lock)
            {
                if (!EnsureSampleMixer())
                {
                    return false;
                }
                _idleTimer.Change(IDLE_TIMEOUT_MILLISECONDS, Timeout.Infinite);

                var flags = BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin;
                if (outputChannel is BassOutputChannel bassOutputChannel)
                {
                    flags |= bassOutputChannel.Flags;
                }

                if (!BassMix.MixerAddChannel(_sampleMixerHandle, sourceHandle, flags) &&
                    Bass.LastError != Errors.Already)
                {
                    YargLogger.LogFormatError("Failed to add sample voice to SFX mixer: {0}!", Bass.LastError);
                    return false;
                }

                _activeSamples.Add(sourceHandle);
                return true;
            }
        }

        public void RemoveSample(int sourceHandle)
        {
            lock (_lock)
            {
                if (!_activeSamples.Remove(sourceHandle))
                {
                    return;
                }
                BassMix.MixerRemoveChannel(sourceHandle);
                if (_activeSamples.Count == 0 && !_disposed)
                {
                    _idleTimer.Change(IDLE_TIMEOUT_MILLISECONDS, Timeout.Infinite);
                }
            }
        }

        public void SetSampleOutputChannel(int sourceHandle, OutputChannel? outputChannel)
        {
            BassFlags flags = outputChannel is BassOutputChannel bassOutputChannel
                ? bassOutputChannel.Flags
                : BassFlags.Default;
            BassMix.ChannelFlags(sourceHandle, flags, BassFlags.SpeakerFront);
        }

        public void SetVolume(double volume) { }

        private bool EnsureSampleMixer()
        {
            if (_sampleMixerHandle != 0)
            {
                return true;
            }

            var info = Bass.Info;
            int frequency = info.SampleRate > 0 ? info.SampleRate : 44100;
            int channels = info.SpeakerCount > 0 ? info.SpeakerCount : 2;
            int mixer = BassMix.CreateMixerStream(frequency, channels,
                BassFlags.Float | BassFlags.MixerNonStop);
            if (mixer == 0)
            {
                YargLogger.LogFormatError("Failed to create SFX mixer: {0}!", Bass.LastError);
                return false;
            }

            Bass.ChannelSetAttribute(mixer, ChannelAttribute.Buffer, 0);
            if (!Bass.ChannelPlay(mixer))
            {
                Bass.StreamFree(mixer);
                return false;
            }

            _sampleMixerHandle = mixer;
            return true;
        }

        private bool EnsureMonitorMixer()
        {
            if (_monitorMixerHandle != 0)
            {
                return true;
            }

            var info = Bass.Info;
            int frequency = info.SampleRate > 0 ? info.SampleRate : 44100;
            int channels = info.SpeakerCount > 0 ? info.SpeakerCount : 2;
            int mixer = BassMix.CreateMixerStream(frequency, channels,
                BassFlags.Float | BassFlags.MixerNonStop);
            if (mixer == 0)
            {
                YargLogger.LogFormatError("Failed to create monitor mixer: {0}", Bass.LastError);
                return false;
            }

            Bass.ChannelSetAttribute(mixer, ChannelAttribute.Buffer, 0);
            if (!Bass.ChannelPlay(mixer))
            {
                YargLogger.LogFormatError("Failed to start monitor mixer: {0}", Bass.LastError);
                Bass.StreamFree(mixer);
                return false;
            }

            _monitorMixerHandle = mixer;
            return true;
        }

        private void OnIdleTimer(object? _)
        {
            UnityMainThreadCallback.QueueEvent(FreeSampleMixerIfIdle);
        }

        private void FreeSampleMixerIfIdle()
        {
            lock (_lock)
            {
                _activeSamples.RemoveWhere(handle => Bass.ChannelIsActive(handle) == PlaybackState.Stopped);
                if (_disposed || _activeSamples.Count != 0)
                {
                    return;
                }
                FreeSampleMixer();
            }
        }

        private void FreeSampleMixer()
        {
            if (_sampleMixerHandle != 0)
            {
                Bass.StreamFree(_sampleMixerHandle);
                _sampleMixerHandle = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            foreach (int tempoStreamHandle in new List<int>(_songMixers.Keys))
            {
                DetachSong(tempoStreamHandle);
            }
            foreach (int monitorHandle in new List<int>(_monitors))
            {
                DetachMonitor(monitorHandle);
            }
            if (_monitorMixerHandle != 0)
            {
                Bass.StreamFree(_monitorMixerHandle);
                _monitorMixerHandle = 0;
            }
            FreeSampleMixer();
            _idleTimer.Dispose();
        }
    }
}
