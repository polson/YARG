#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using ManagedBass;
using ManagedBass.Asio;
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

        private bool _useSingleMixer;
        private bool _ownsAsio;
        private readonly object _lock = new();
        private readonly HashSet<int> _activeSources = new();
        private readonly System.Threading.Timer _idleTimer;

        private int _mixerHandle;
        private int _deviceId = -1;
        private int _asioLatencyMilliseconds;
        private double _volume = 1;
        private bool _disposed;

        public BassAudioOutput()
        {
            _idleTimer = new System.Threading.Timer(OnIdleTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public int AsioLatencyMilliseconds => _asioLatencyMilliseconds;

        public bool InitializeForDevice(BassOutputDevice device)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                _useSingleMixer = device.IsAsio;
                if (!_useSingleMixer)
                {
                    if (!Bass.Start())
                    {
                        YargLogger.LogFormatError("Failed to start BASS output device: {0}", Bass.LastError);
                        return false;
                    }

                    return true;
                }

                return EnsureMixer() && StartAsio(device.AsioDeviceId);
            }
        }

        public BassSongPlayback CreateSongPlayback(int tempoStreamHandle)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return new BassSongPlayback(tempoStreamHandle, this, 0);
                }

                if (!_useSingleMixer)
                {
                    return new BassSongPlayback(tempoStreamHandle);
                }

                EnsureMixer();
                return new BassSongPlayback(tempoStreamHandle, this, _mixerHandle);
            }
        }

        public bool PlaySample(int sourceHandle, OutputChannel? outputChannel)
        {
            lock (_lock)
            {
                if (_disposed || !EnsureMixer())
                {
                    return false;
                }

                if (!_useSingleMixer)
                {
                    _idleTimer.Change(IDLE_TIMEOUT_MILLISECONDS, Timeout.Infinite);
                }

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

                if (_activeSources.Count == 0 && !_disposed && !_useSingleMixer)
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

        public void SetBufferLength(int length)
        {
            // Decoding outputs are buffered by their endpoint. Standard song mixers
            // receive this setting directly from BassSongPlayback.
        }

        public void SetVolume(double volume)
        {
            lock (_lock)
            {
                _volume = volume;
                if (_useSingleMixer && _mixerHandle != 0 &&
                    !Bass.ChannelSetAttribute(_mixerHandle, ChannelAttribute.Volume, volume))
                {
                    YargLogger.LogFormatError("Failed to set master mixer volume: {0}", Bass.LastError);
                }
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
                StopAsio();
                FreeMixer();
                _useSingleMixer = false;
                _asioLatencyMilliseconds = 0;
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

            var flags = BassFlags.Float | BassFlags.MixerNonStop;
            if (_useSingleMixer)
            {
                flags |= BassFlags.Decode;
                channels = 2;
            }

            int mixerHandle = BassMix.CreateMixerStream(frequency, channels, flags);
            if (mixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create SFX mixer: {0}!", Bass.LastError);
                return false;
            }

            if (!_useSingleMixer)
            {
                SetMixerBufferLength(mixerHandle, 0);
                if (!Bass.ChannelPlay(mixerHandle))
                {
                    YargLogger.LogFormatError("Failed to start SFX mixer: {0}!", Bass.LastError);
                    Bass.StreamFree(mixerHandle);
                    return false;
                }
            }

            _mixerHandle = mixerHandle;
            _deviceId = Bass.CurrentDevice;
            if (_useSingleMixer &&
                !Bass.ChannelSetAttribute(mixerHandle, ChannelAttribute.Volume, _volume))
            {
                YargLogger.LogFormatError("Failed to initialize master mixer volume: {0}", Bass.LastError);
            }
            string mixerName = _useSingleMixer ? "master" : "SFX";
            YargLogger.LogFormatInfo("Created BASS {0} mixer: handle {1}, {2}Hz, {3} channels",
                mixerName, mixerHandle, frequency, channels);
            return true;
        }

        private bool StartAsio(int deviceId)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                return StartAsioInternal(deviceId);
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to start ASIO output");
                return false;
            }
#else
            YargLogger.LogError("ASIO output is only available on Windows");
            return false;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private bool StartAsioInternal(int deviceId)
        {
            if (!BassAsio.Init(deviceId, AsioInitFlags.Thread))
            {
                YargLogger.LogFormatError("Failed to initialize ASIO device: {0}", BassAsio.LastError);
                return false;
            }
            _ownsAsio = true;

            var mixerInfo = Bass.ChannelGetInfo(_mixerHandle);
            if (!BassAsio.CheckRate(mixerInfo.Frequency))
            {
                YargLogger.LogFormatError("ASIO device does not support {0}Hz: {1}",
                    mixerInfo.Frequency, BassAsio.LastError);
                return false;
            }

            BassAsio.Rate = mixerInfo.Frequency;
            if (!BassAsio.ChannelEnableBass(false, 0, _mixerHandle, true))
            {
                YargLogger.LogFormatError("Failed to route master mixer to ASIO: {0}", BassAsio.LastError);
                return false;
            }

            // Zero keeps the buffer size selected by the driver.
            if (!BassAsio.Start(0, 0))
            {
                YargLogger.LogFormatError("Failed to start ASIO output: {0}", BassAsio.LastError);
                return false;
            }

            var info = BassAsio.Info;
            int latency = BassAsio.GetLatency(false);
            _asioLatencyMilliseconds = latency >= 0
                ? (int) Math.Round(latency * 1000.0 / mixerInfo.Frequency)
                : 0;
            YargLogger.LogFormatInfo(
                "Started ASIO '{0}': {1}Hz, preferred buffer {2} samples, output latency {3} samples",
                info.Name, mixerInfo.Frequency, info.PreferredBufferLength, latency);
            return true;
        }
#endif

        private void StopAsio()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!_ownsAsio)
            {
                return;
            }

            BassAsio.Stop();
            if (!BassAsio.Free())
            {
                YargLogger.LogFormatError("Failed to free ASIO device: {0}", BassAsio.LastError);
            }
            _ownsAsio = false;
            _asioLatencyMilliseconds = 0;
#endif
        }

        private void SetMixerBufferLength(int length)
        {
            SetMixerBufferLength(_mixerHandle, length);
        }

        private static void SetMixerBufferLength(int mixerHandle, int length)
        {
            float lengthInSeconds = length / 1000f;
            if (!Bass.ChannelSetAttribute(mixerHandle, ChannelAttribute.Buffer, lengthInSeconds))
            {
                YargLogger.LogFormatError("Failed to set audio output buffer: {0}!", Bass.LastError);
            }
        }

        private void OnIdleTimer(object? _)
        {
            UnityMainThreadCallback.QueueEvent(FreeIfIdle);
        }

        private void FreeIfIdle()
        {
            lock (_lock)
            {
                if (_disposed || _useSingleMixer)
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
                    YargLogger.LogFormatError("Failed to free audio output mixer: {0}!", Bass.LastError);
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
                StopAsio();
                FreeMixer();
            }

            _idleTimer.Dispose();
        }
    }
}
