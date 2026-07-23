using System;
using System.Collections.Generic;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Owns final playback stage for one song.
    /// </summary>
    internal sealed class BassSongPlayback : IDisposable
    {
        private readonly int _tempoStreamHandle;
        private readonly BassAudioOutput _audioOutput;
        private readonly bool _usesMaster;
        private int _outputMixerHandle;
        private readonly HashSet<BassOneShotChannel> _oneShotChannels = new();
        private bool _isValid;

        public bool IsValid => _isValid;

        public bool IsPlaying
        {
            get
            {
                int handle = _usesMaster ? _tempoStreamHandle : _outputMixerHandle;
                return Bass.ChannelIsActive(handle) is PlaybackState.Playing or PlaybackState.Stalled;
            }
        }

        internal BassSongPlayback(int tempoStreamHandle)
        {
            _tempoStreamHandle = tempoStreamHandle;

            var tempoInfo = Bass.ChannelGetInfo(tempoStreamHandle);
            _outputMixerHandle = BassMix.CreateMixerStream(tempoInfo.Frequency, tempoInfo.Channels,
                BassFlags.Float | BassFlags.MixerNonStop);
            if (_outputMixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create output mixer: {0}", Bass.LastError);
                return;
            }

            if (!BassMix.MixerAddChannel(_outputMixerHandle, tempoStreamHandle,
                BassFlags.MixerChanNoRampin))
            {
                YargLogger.LogFormatError("Failed to add tempo stream to output mixer: {0}", Bass.LastError);
                Bass.StreamFree(_outputMixerHandle);
                _outputMixerHandle = 0;
                return;
            }

            _isValid = true;
        }

        internal BassSongPlayback(int tempoStreamHandle, BassAudioOutput audioOutput,
            int masterMixerHandle)
        {
            _tempoStreamHandle = tempoStreamHandle;
            _audioOutput = audioOutput;
            _usesMaster = true;
            _outputMixerHandle = masterMixerHandle;
            if (_outputMixerHandle == 0)
            {
                return;
            }

            var flags = BassFlags.MixerChanNoRampin |
                BassFlags.MixerChanBuffer |
                BassFlags.MixerChanPause;
            if (!BassMix.MixerAddChannel(_outputMixerHandle, tempoStreamHandle, flags))
            {
                YargLogger.LogFormatError("Failed to add tempo stream to master mixer: {0}",
                    Bass.LastError);
                return;
            }

            _isValid = true;
        }

        public int Play(bool restart)
        {
            if (IsPlaying)
            {
                return 0;
            }

            if (_usesMaster)
            {
                if (BassMix.ChannelFlags(_tempoStreamHandle, BassFlags.Default,
                        BassFlags.MixerChanPause) < 0)
                {
                    return (int) Bass.LastError;
                }

                foreach (var channel in _oneShotChannels)
                {
                    channel.SetPlaybackPaused(false);
                }
                return 0;
            }

            // Prime stream before playback to avoid initial decode/buffer-fill delay.
            Bass.ChannelUpdate(_outputMixerHandle, 0);
            return Bass.ChannelPlay(_outputMixerHandle, Restart: restart)
                ? 0
                : (int) Bass.LastError;
        }

        public int Pause()
        {
            if (!IsPlaying)
            {
                return 0;
            }

            if (_usesMaster)
            {
                if (BassMix.ChannelFlags(_tempoStreamHandle, BassFlags.MixerChanPause,
                        BassFlags.MixerChanPause) < 0)
                {
                    return (int) Bass.LastError;
                }

                foreach (var channel in _oneShotChannels)
                {
                    channel.SetPlaybackPaused(true);
                }
                return 0;
            }

            return Bass.ChannelPause(_outputMixerHandle)
                ? 0
                : (int) Bass.LastError;
        }

        public void ResetAfterSeek()
        {
            // Private playback mixers retain source-position history after source reset.
            if (!_usesMaster &&
                !Bass.ChannelSetPosition(_outputMixerHandle, 0, PositionFlags.Bytes))
            {
                YargLogger.LogFormatError("Failed to reset output mixer position: {0}!", Bass.LastError);
            }

            foreach (var channel in _oneShotChannels)
            {
                channel.ResetAfterSeek();
            }
        }

        public void PrepareForSeek()
        {
            foreach (var channel in _oneShotChannels)
            {
                channel.PrepareForSeek();
            }
        }

        public void ResetAfterSpeedChange()
        {
            foreach (var channel in _oneShotChannels)
            {
                channel.ResetAfterSpeedChange();
            }
        }

        public void FadeIn(double maxVolume, double duration)
        {
            float scaled = (float) BassAudioManager.ExponentialVolume(maxVolume);
            int volumeHandle = _usesMaster ? _tempoStreamHandle : _outputMixerHandle;
            Bass.ChannelSlideAttribute(volumeHandle, ChannelAttribute.Volume, scaled,
                (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public void FadeOut(double duration)
        {
            int volumeHandle = _usesMaster ? _tempoStreamHandle : _outputMixerHandle;
            Bass.ChannelSlideAttribute(volumeHandle, ChannelAttribute.Volume, 0,
                (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public double GetVolume()
        {
            int volumeHandle = _usesMaster ? _tempoStreamHandle : _outputMixerHandle;
            if (!Bass.ChannelGetAttribute(volumeHandle, ChannelAttribute.Volume, out float volume))
            {
                YargLogger.LogFormatError("Failed to get volume: {0}", Bass.LastError);
            }
            return BassAudioManager.LogarithmicVolume(volume);
        }

        public void SetVolume(double volume)
        {
            volume = BassAudioManager.ExponentialVolume(volume);
            int volumeHandle = _usesMaster ? _tempoStreamHandle : _outputMixerHandle;
            if (!Bass.ChannelSetAttribute(volumeHandle, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set output mixer volume: {0}", Bass.LastError);
            }
        }

        public int GetFFTData(float[] buffer, int fftSize, bool complex)
        {
            int flags = (1 << fftSize) switch
            {
                256  => (int) DataFlags.FFT256,
                512  => (int) DataFlags.FFT512,
                1024 => (int) DataFlags.FFT1024,
                2048 => (int) DataFlags.FFT2048,
                4096 => (int) DataFlags.FFT4096,
                _    => -1,
            };
            if (flags < 0)
            {
                return -1;
            }
            if (complex)
            {
                flags |= (int) DataFlags.FFTComplex;
            }

            int result = _usesMaster
                ? BassMix.ChannelGetData(_tempoStreamHandle, buffer, flags)
                : Bass.ChannelGetData(_outputMixerHandle, buffer, flags);
            return result < 0 ? (int) Bass.LastError : result;
        }

        public int GetSampleData(float[] buffer)
        {
            int flags = buffer.Length * sizeof(float) | (int) DataFlags.Float;
            int result = _usesMaster
                ? BassMix.ChannelGetData(_tempoStreamHandle, buffer, flags)
                : Bass.ChannelGetData(_outputMixerHandle, buffer, flags);
            return result < 0 ? (int) Bass.LastError : result;
        }

        public int GetLevel(float[] level)
        {
            var flags = LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS;
            if (_usesMaster)
            {
                return BassMix.ChannelGetLevel(_tempoStreamHandle, level, 0.2f, flags) >= 0
                    ? (int) Errors.OK
                    : (int) Bass.LastError;
            }

            bool succeeded = Bass.ChannelGetLevel(_outputMixerHandle, level, 0.2f, flags);
            return succeeded ? (int) Errors.OK : (int) Bass.LastError;
        }

        public double GetLatency()
        {
            return BassLatencyProvider.GetTempoStreamLatency(_outputMixerHandle);
        }

        public void SetBufferLength(int length)
        {
            if (_usesMaster)
            {
                _audioOutput.SetBufferLength(length);
                return;
            }

            length = BassHelpers.ClampPlaybackBufferLength(length);
            float lengthInSeconds = length / 1000f;
            if (!Bass.ChannelSetAttribute(_outputMixerHandle, ChannelAttribute.Buffer, lengthInSeconds))
            {
                YargLogger.LogFormatError("Failed to set playback buffer: {0}!", Bass.LastError);
            }
        }

#nullable enable
        public void SetOutputChannel(OutputChannel? channel)
#nullable disable
        {
            if (!_usesMaster)
            {
                BassHelpers.UpdateOutputChannels(_outputMixerHandle, channel);
                return;
            }

            BassFlags flags = channel is BassOutputChannel bassOutputChannel
                ? bassOutputChannel.Flags
                : BassFlags.Default;
            BassMix.ChannelFlags(_tempoStreamHandle, flags, BassFlags.SpeakerFront);
        }

        public void SetOutputDevice(BassOutputDevice device)
        {
            if (_usesMaster)
            {
                return;
            }

            if (!Bass.ChannelSetDevice(_outputMixerHandle, device.DeviceId))
            {
                YargLogger.LogFormatError("Failed to change device for output mixer handle: {0}",
                    Bass.LastError);
            }
        }

        public OneShotChannel CreateOneShotChannel(int sampleStream,
            IReadOnlyList<double> scheduledPlays, Func<long, double> getSongPosition,
            Func<float> getSpeed, double outputLeadTime)
        {
            var channel = new BassOneShotChannel(
                _outputMixerHandle,
                _tempoStreamHandle,
                sampleStream,
                scheduledPlays,
                getSongPosition,
                getSpeed,
                outputLeadTime,
                playbackPaused: _usesMaster && !IsPlaying
            );
            channel.Disposed += OnOneShotDisposed;
            _oneShotChannels.Add(channel);
            return channel;
        }

        private void OnOneShotDisposed(BassOneShotChannel channel)
        {
            _oneShotChannels.Remove(channel);
        }

        public void Dispose()
        {
            // One-shot decoders are independent streams and must be freed before their mixer.
            foreach (var channel in _oneShotChannels.ToArray())
            {
                channel.Dispose();
            }
            _oneShotChannels.Clear();

            if (_usesMaster && _isValid &&
                !BassMix.MixerRemoveChannel(_tempoStreamHandle) && Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to remove tempo stream from master mixer: {0}!",
                    Bass.LastError);
            }
            else if (!_usesMaster && _outputMixerHandle != 0 && !Bass.StreamFree(_outputMixerHandle))
            {
                YargLogger.LogFormatError(
                    "Failed to free output mixer stream (THIS WILL LEAK MEMORY!): {0}!",
                    Bass.LastError);
            }

            _isValid = false;
        }
    }
}
