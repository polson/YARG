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
        private readonly int _outputMixerHandle;
        private readonly HashSet<BassOneShotChannel> _oneShotChannels = new();

        public bool IsValid => _outputMixerHandle != 0;

        public bool IsPlaying => Bass.ChannelIsActive(_outputMixerHandle) is
            PlaybackState.Playing or PlaybackState.Stalled;

        public BassSongPlayback(int tempoStreamHandle)
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
            }
        }

        public int Play(bool restart)
        {
            if (IsPlaying)
            {
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

            return Bass.ChannelPause(_outputMixerHandle)
                ? 0
                : (int) Bass.LastError;
        }

        public void ResetAfterSeek()
        {
            // Resetting tempo source alone does not reliably clear BASSmix's buffered
            // source-position history.
            if (!Bass.ChannelSetPosition(_outputMixerHandle, 0, PositionFlags.Bytes))
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
            Bass.ChannelSlideAttribute(_outputMixerHandle, ChannelAttribute.Volume, scaled,
                (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public void FadeOut(double duration)
        {
            Bass.ChannelSlideAttribute(_outputMixerHandle, ChannelAttribute.Volume, 0,
                (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public double GetVolume()
        {
            if (!Bass.ChannelGetAttribute(_outputMixerHandle, ChannelAttribute.Volume, out float volume))
            {
                YargLogger.LogFormatError("Failed to get volume: {0}", Bass.LastError);
            }
            return BassAudioManager.LogarithmicVolume(volume);
        }

        public void SetVolume(double volume)
        {
            volume = BassAudioManager.ExponentialVolume(volume);
            if (!Bass.ChannelSetAttribute(_outputMixerHandle, ChannelAttribute.Volume, volume))
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

            int result = Bass.ChannelGetData(_outputMixerHandle, buffer, flags);
            return result < 0 ? (int) Bass.LastError : result;
        }

        public int GetSampleData(float[] buffer)
        {
            int flags = buffer.Length * sizeof(float) | (int) DataFlags.Float;
            int result = Bass.ChannelGetData(_outputMixerHandle, buffer, flags);
            return result < 0 ? (int) Bass.LastError : result;
        }

        public int GetLevel(float[] level)
        {
            bool succeeded = Bass.ChannelGetLevel(_outputMixerHandle, level, 0.2f,
                LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS);
            return succeeded ? (int) Errors.OK : (int) Bass.LastError;
        }

        public double GetLatency()
        {
            return BassLatencyProvider.GetTempoStreamLatency(_outputMixerHandle);
        }

        public void SetBufferLength(int length)
        {
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
            BassHelpers.UpdateOutputChannels(_outputMixerHandle, channel);
        }

        public void SetOutputDevice(BassOutputDevice device)
        {
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
                outputLeadTime
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

            if (_outputMixerHandle != 0 && !Bass.StreamFree(_outputMixerHandle))
            {
                YargLogger.LogFormatError(
                    "Failed to free output mixer stream (THIS WILL LEAK MEMORY!): {0}!",
                    Bass.LastError);
            }
        }
    }
}
