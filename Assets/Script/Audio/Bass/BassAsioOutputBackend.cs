#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassAsioOutputBackend : IBassOutputBackend
    {
        private readonly HashSet<int> _songs = new();
        private readonly HashSet<int> _samples = new();
        private int _masterMixerHandle;
        private bool _ownsAsio;
        private double _volume = 1;

        public int HeardLatencyMilliseconds { get; private set; }
        public bool SongMixerRunsContinuously => true;
        public double PlaybackStartDelay => 0;

        public bool Initialize(BassOutputDevice device)
        {
            if (!CreateMasterMixer())
            {
                return false;
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                return StartAsio(device.AsioDeviceId);
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

        public bool AttachSong(int tempoStreamHandle)
        {
            var flags = BassFlags.MixerChanNoRampin | BassFlags.MixerChanBuffer |
                BassFlags.MixerChanPause;
            if (!BassMix.MixerAddChannel(_masterMixerHandle, tempoStreamHandle, flags))
            {
                YargLogger.LogFormatError("Failed to add tempo stream to master mixer: {0}", Bass.LastError);
                return false;
            }
            _songs.Add(tempoStreamHandle);
            return true;
        }

        public void DetachSong(int tempoStreamHandle)
        {
            if (_songs.Remove(tempoStreamHandle) && !BassMix.MixerRemoveChannel(tempoStreamHandle) &&
                Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to remove tempo stream from master mixer: {0}!", Bass.LastError);
            }
        }

        public int SongMixerHandle(int tempoStreamHandle) => _masterMixerHandle;

        public bool IsSongPlaying(int tempoStreamHandle)
        {
            bool active = Bass.ChannelIsActive(tempoStreamHandle) is
                PlaybackState.Playing or PlaybackState.Stalled;
            return active && !BassMix.ChannelHasFlag(tempoStreamHandle, BassFlags.MixerChanPause);
        }

        public int PlaySong(int tempoStreamHandle, bool restart)
        {
            return BassMix.ChannelFlags(tempoStreamHandle, BassFlags.Default, BassFlags.MixerChanPause) < 0
                ? (int) Bass.LastError
                : 0;
        }

        public int PauseSong(int tempoStreamHandle)
        {
            return BassMix.ChannelFlags(tempoStreamHandle, BassFlags.MixerChanPause,
                BassFlags.MixerChanPause) < 0 ? (int) Bass.LastError : 0;
        }

        public void ResetSongAfterSeek(int tempoStreamHandle) { }

        public void FadeSong(int tempoStreamHandle, double volume, int durationMilliseconds)
        {
            Bass.ChannelSlideAttribute(tempoStreamHandle, ChannelAttribute.Volume,
                (float) volume, durationMilliseconds);
        }

        public double GetSongVolume(int tempoStreamHandle)
        {
            Bass.ChannelGetAttribute(tempoStreamHandle, ChannelAttribute.Volume, out float volume);
            return volume;
        }

        public void SetSongVolume(int tempoStreamHandle, double volume)
        {
            if (!Bass.ChannelSetAttribute(tempoStreamHandle, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set song volume: {0}", Bass.LastError);
            }
        }

        public int GetSongData(int tempoStreamHandle, float[] buffer, int flags)
        {
            return BassMix.ChannelGetData(tempoStreamHandle, buffer, flags);
        }

        public int GetSongLevel(int tempoStreamHandle, float[] level)
        {
            var flags = LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS;
            return BassMix.ChannelGetLevel(tempoStreamHandle, level, 0.2f, flags) >= 0
                ? (int) Errors.OK
                : (int) Bass.LastError;
        }

        // Tempo decoding is pulled directly by ASIO. No BASS playback queue exists here.
        public double GetTempoCommandDelay(int tempoStreamHandle) => 0;

        public void SetSongBufferLength(int tempoStreamHandle, int length) { }

        public void SetSongOutputChannel(int tempoStreamHandle, OutputChannel? channel)
        {
            BassFlags flags = channel is BassOutputChannel bassOutputChannel
                ? bassOutputChannel.Flags
                : BassFlags.Default;
            BassMix.ChannelFlags(tempoStreamHandle, flags, BassFlags.SpeakerFront);
        }

        public bool PlaySample(int sourceHandle, OutputChannel? outputChannel)
        {
            var flags = BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin;
            if (outputChannel is BassOutputChannel bassOutputChannel)
            {
                flags |= bassOutputChannel.Flags;
            }
            if (!BassMix.MixerAddChannel(_masterMixerHandle, sourceHandle, flags) &&
                Bass.LastError != Errors.Already)
            {
                return false;
            }
            _samples.Add(sourceHandle);
            return true;
        }

        public void RemoveSample(int sourceHandle)
        {
            if (_samples.Remove(sourceHandle))
            {
                BassMix.MixerRemoveChannel(sourceHandle);
            }
        }

        public void SetSampleOutputChannel(int sourceHandle, OutputChannel? outputChannel)
        {
            BassFlags flags = outputChannel is BassOutputChannel bassOutputChannel
                ? bassOutputChannel.Flags
                : BassFlags.Default;
            BassMix.ChannelFlags(sourceHandle, flags, BassFlags.SpeakerFront);
        }

        public void SetVolume(double volume)
        {
            _volume = volume;
            if (_masterMixerHandle != 0 &&
                !Bass.ChannelSetAttribute(_masterMixerHandle, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set master mixer volume: {0}", Bass.LastError);
            }
        }

        private bool CreateMasterMixer()
        {
            var info = Bass.Info;
            int frequency = info.SampleRate > 0 ? info.SampleRate : 44100;
            _masterMixerHandle = BassMix.CreateMixerStream(frequency, 2,
                BassFlags.Float | BassFlags.MixerNonStop | BassFlags.Decode);
            if (_masterMixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create ASIO master mixer: {0}!", Bass.LastError);
                return false;
            }
            Bass.ChannelSetAttribute(_masterMixerHandle, ChannelAttribute.Volume, _volume);
            return true;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private bool StartAsio(int deviceId)
        {
            if (!BassAsio.Init(deviceId, AsioInitFlags.Thread))
            {
                YargLogger.LogFormatError("Failed to initialize ASIO device: {0}", BassAsio.LastError);
                return false;
            }
            _ownsAsio = true;

            var mixerInfo = Bass.ChannelGetInfo(_masterMixerHandle);
            if (!BassAsio.CheckRate(mixerInfo.Frequency))
            {
                YargLogger.LogFormatError("ASIO device does not support {0}Hz: {1}",
                    mixerInfo.Frequency, BassAsio.LastError);
                return false;
            }
            BassAsio.Rate = mixerInfo.Frequency;
            if (!BassAsio.ChannelEnableBass(false, 0, _masterMixerHandle, true) ||
                !BassAsio.Start(0, 0))
            {
                YargLogger.LogFormatError("Failed to start ASIO output: {0}", BassAsio.LastError);
                return false;
            }

            int latency = BassAsio.GetLatency(false);
            HeardLatencyMilliseconds = latency >= 0
                ? (int) Math.Round(latency * 1000.0 / mixerInfo.Frequency)
                : 0;
            return true;
        }
#endif

        public void Dispose()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_ownsAsio)
            {
                BassAsio.Stop();
                BassAsio.Free();
                _ownsAsio = false;
            }
#endif
            _songs.Clear();
            _samples.Clear();
            if (_masterMixerHandle != 0)
            {
                Bass.StreamFree(_masterMixerHandle);
                _masterMixerHandle = 0;
            }
        }
    }
}
