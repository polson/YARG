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
    /// <summary>
    /// Owns sample routing for current audio output topology.
    /// </summary>
    internal sealed class BassAudioOutput : IDisposable
    {
        private const int IDLE_TIMEOUT_MILLISECONDS = 10_000;

        private readonly object _lock = new();
        private readonly HashSet<int> _activeSources = new();
        private readonly System.Threading.Timer _idleTimer;

        private int _mixerHandle;
        private int _deviceId = -1;
        private bool _disposed;

        public BassAudioOutput()
        {
            _idleTimer = new System.Threading.Timer(OnIdleTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public bool PlaySample(int sourceHandle, OutputChannel? outputChannel)
        {
            lock (_lock)
            {
                if (_disposed || !EnsureMixer())
                {
                    return false;
                }

                _idleTimer.Change(IDLE_TIMEOUT_MILLISECONDS, Timeout.Infinite);

                var flags = BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin;
                if (outputChannel is BassOutputChannel bassOutputChannel)
                {
                    flags |= bassOutputChannel.Flags;
                }

                if (!BassMix.MixerAddChannel(_mixerHandle, sourceHandle, flags) &&
                    Bass.LastError != Errors.Already)
                {
                    YargLogger.LogFormatError("Failed to add sample voice to SFX mixer: {0}!", Bass.LastError);
                    return false;
                }

                _activeSources.Add(sourceHandle);
                return true;
            }
        }

        public void RemoveSample(int sourceHandle)
        {
            lock (_lock)
            {
                if (!_activeSources.Remove(sourceHandle))
                {
                    return;
                }

                if (_mixerHandle != 0 && !BassMix.MixerRemoveChannel(sourceHandle) &&
                    Bass.LastError != Errors.Handle)
                {
                    YargLogger.LogFormatError("Failed to remove sample voice from SFX mixer: {0}!", Bass.LastError);
                }

                if (_activeSources.Count == 0 && !_disposed)
                {
                    _idleTimer.Change(IDLE_TIMEOUT_MILLISECONDS, Timeout.Infinite);
                }
            }
        }

        public void SetSampleOutputChannel(int sourceHandle, OutputChannel? outputChannel)
        {
            lock (_lock)
            {
                BassFlags flags = outputChannel is BassOutputChannel bassOutputChannel
                    ? bassOutputChannel.Flags
                    : BassFlags.Default;
                BassMix.ChannelFlags(sourceHandle, flags, BassFlags.SpeakerFront);
            }
        }

        /// <summary>
        /// Releases resources belonging to the current output device while allowing later reuse.
        /// </summary>
        public void ResetForDeviceChange()
        {
            lock (_lock)
            {
                _activeSources.Clear();
                _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                FreeMixer();
            }
        }

        private bool EnsureMixer()
        {
            if (_mixerHandle != 0)
            {
                return true;
            }

            var info = Bass.Info;
            int frequency = info.SampleRate > 0 ? info.SampleRate : 44100;
            int channels = info.SpeakerCount > 0 ? info.SpeakerCount : 2;

            int mixerHandle = BassMix.CreateMixerStream(frequency, channels,
                BassFlags.Float | BassFlags.MixerNonStop);
            if (mixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create SFX mixer: {0}!", Bass.LastError);
                return false;
            }

            if (!Bass.ChannelSetAttribute(mixerHandle, ChannelAttribute.Buffer, 0f))
            {
                YargLogger.LogFormatError("Failed to disable SFX mixer buffering: {0}!", Bass.LastError);
            }

            if (!Bass.ChannelPlay(mixerHandle))
            {
                YargLogger.LogFormatError("Failed to start SFX mixer: {0}!", Bass.LastError);
                Bass.StreamFree(mixerHandle);
                return false;
            }

            _mixerHandle = mixerHandle;
            _deviceId = Bass.CurrentDevice;
            YargLogger.LogFormatInfo("Created BASS SFX mixer: handle {0}, {1}Hz, {2} channels",
                mixerHandle, frequency, channels);
            return true;
        }

        private void OnIdleTimer(object? _)
        {
            UnityMainThreadCallback.QueueEvent(FreeIfIdle);
        }

        private void FreeIfIdle()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _activeSources.RemoveWhere(sourceHandle =>
                    Bass.ChannelIsActive(sourceHandle) == PlaybackState.Stopped);
                if (_activeSources.Count != 0)
                {
                    _idleTimer.Change(IDLE_TIMEOUT_MILLISECONDS, Timeout.Infinite);
                    return;
                }

                FreeMixer();
            }
        }

        private void FreeMixer()
        {
            if (_mixerHandle == 0)
            {
                return;
            }

            int currentDevice = Bass.CurrentDevice;
            try
            {
                if (_deviceId >= 0)
                {
                    Bass.CurrentDevice = _deviceId;
                }

                if (!Bass.StreamFree(_mixerHandle))
                {
                    YargLogger.LogFormatError("Failed to free SFX mixer: {0}!", Bass.LastError);
                }
            }
            finally
            {
                Bass.CurrentDevice = currentDevice;
                _mixerHandle = 0;
                _deviceId = -1;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _activeSources.Clear();
                _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                FreeMixer();
            }

            _idleTimer.Dispose();
        }
    }
}
