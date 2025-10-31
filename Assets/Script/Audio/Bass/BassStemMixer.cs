using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    public sealed class BassStemMixer : StemMixer
    {
        private const    float WHAMMY_SYNC_INTERVAL_SECONDS = 1f;

        //The delay which is introduced by the pitch shift effect, in seconds
        private const float PITCH_DELAY_SECONDS = GlobalAudioHandler.WHAMMY_FFT_DEFAULT / 44100f;
        private       bool  IsWhammyEnabled => SettingsManager.Settings.UseWhammyFx.Value;

        private readonly int             _mixerHandle;
        private readonly List<int>       _sourceHandles = new();
        private          int             _tempoStreamHandle;
        private          double          _positionOffset = 0.0;
        private          bool            _didSetPosition = false;
        private          int             _songEndHandle;
        private          float           _speed;
        private          Timer           _whammySyncTimer;

        public override event Action SongEnd
        {
            add
            {
                if (_songEndHandle == 0)
                {
                    void sync(int _, int __, int ___, IntPtr _____)
                    {
                        // Prevent potential race conditions by caching the value as a local
                        var end = _songEnd;
                        if (end != null)
                        {
                            UnityMainThreadCallback.QueueEvent(end.Invoke);
                        }
                    }
                    _songEndHandle = BassMix.ChannelSetSync(_tempoStreamHandle, SyncFlags.End, 0, sync);
                }

                _songEnd += value;
            }
            remove
            {
                _songEnd -= value;
            }
        }

        internal BassStemMixer(string name, BassAudioManager manager, float speed, double volume, int handle, bool clampStemVolume)
            : base(name, manager, clampStemVolume)
        {
            _tempoStreamHandle = BassFx.TempoCreate(handle, BassFlags.SampleOverrideLowestVolume);
            if (_tempoStreamHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create tempo stream: {0}", Bass.LastError);
                return;
            }

            _mixerHandle = handle;
            _speed = speed;
            _whammySyncTimer = new Timer();
            SetVolume_Internal(volume);
        }

        protected override int Play_Internal()
        {
            var playbackState = Bass.ChannelIsActive(_tempoStreamHandle);
            if (playbackState != PlaybackState.Playing)
            {
                if (!Bass.ChannelPlay(_tempoStreamHandle, _didSetPosition))
                {
                    return (int) Bass.LastError;
                }

                _didSetPosition = false;
            }

            if (IsWhammyEnabled)
            {
                _whammySyncTimer.Start(WHAMMY_SYNC_INTERVAL_SECONDS, SyncWhammyDrift);
            }

            return 0;
        }

        /// <summary>.
        /// The BASS PitchShift effect causes the stem playback to drift over time.
        /// It was discovered that we can correct the drift by setting the whammy pitch
        /// to 0% when no pitch shift is applied.
        /// </summary>
        private void SyncWhammyDrift()
        {
            foreach (var channel in Channels)
            {
                if (Mathf.Approximately(channel.GetWhammyPitch(), 1.0f))
                {
                    channel.SetWhammyPitch(percent: 0.0f);
                }
            }
        }

        protected override void FadeIn_Internal(double maxVolume, double duration)
        {
            float scaled = (float) BassAudioManager.ExponentialVolume(maxVolume);
            Bass.ChannelSlideAttribute(_tempoStreamHandle, ChannelAttribute.Volume, scaled, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        protected override void FadeOut_Internal(double duration)
        {
            Bass.ChannelSlideAttribute(_tempoStreamHandle, ChannelAttribute.Volume, 0, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        protected override int Pause_Internal()
        {
            var playbackState = Bass.ChannelIsActive(_tempoStreamHandle);
            if (playbackState == PlaybackState.Playing)
            {
                if (!Bass.ChannelPause(_tempoStreamHandle))
                {
                    return (int) Bass.LastError;
                }
            }

            return 0;
        }

        protected override double GetPosition_Internal()
        {
            long positionBytes = Bass.ChannelGetPosition(_tempoStreamHandle);
            if (positionBytes < 0)
            {
                YargLogger.LogFormatError("Failed to get byte position: {0}!", Bass.LastError);
                return 0.0f;
            }

            double seconds = Bass.ChannelBytes2Seconds(_tempoStreamHandle, positionBytes);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to convert bytes to seconds: {0}!", Bass.LastError);
                return 0.0f;
            }

            return seconds + _positionOffset;
        }

        protected override double GetVolume_Internal()
        {
            if (!Bass.ChannelGetAttribute(_tempoStreamHandle, ChannelAttribute.Volume, out float volume))
            {
                YargLogger.LogFormatError("Failed to get volume: {0}", Bass.LastError);
            }
            return BassAudioManager.LogarithmicVolume(volume);
        }

        protected override void SetPosition_Internal(double position)
        {
            foreach (var channel in _channels)
            {
                channel.SetPosition(position);
            }
            _didSetPosition = true;
            _positionOffset = position;
        }

        protected override void SetVolume_Internal(double volume)
        {
            volume = BassAudioManager.ExponentialVolume(volume);
            if (!Bass.ChannelSetAttribute(_tempoStreamHandle, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set tempo stream volume: {0}", Bass.LastError);
            }
        }

        protected override int GetFFTData_Internal(float[] buffer, int fftSize, bool complex)
        {
            int flags = 0;
            switch (1 << fftSize)
            {
                case 256:
                    flags |= (int) DataFlags.FFT256;
                    break;
                case 512:
                    flags |= (int) DataFlags.FFT512;
                    break;
                case 1024:
                    flags |= (int) DataFlags.FFT1024;
                    break;
                case 2048:
                    flags |= (int) DataFlags.FFT2048;
                    break;
                case 4096:
                    flags |= (int) DataFlags.FFT4096;
                    break;
                default:
                    return -1;
            }

            if (complex)
            {
                flags |= (int) DataFlags.FFTComplex;
            }

            int data = Bass.ChannelGetData(_tempoStreamHandle, buffer, flags);
            if (data < 0)
            {
                return (int) Bass.LastError;
            }
            return data;
        }

        protected override int GetSampleData_Internal(float[] buffer)
        {
            int data = Bass.ChannelGetData(_tempoStreamHandle, buffer, (buffer.Length * 4) | (int) (DataFlags.Float));
            if (data < 0)
            {
                return (int) Bass.LastError;
            }
            return data;
        }

        protected override int GetLevel_Internal(float[] level)
        {
            bool status = Bass.ChannelGetLevel(_tempoStreamHandle, level, 0.2f, LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS);
            if (!status)
            {
                return (int) Bass.LastError;
            }

            return (int) Errors.OK;
        }

        protected override void SetSpeed_Internal(float speed, bool shiftPitch)
        {
            speed = (float) Math.Clamp(speed, 0.05, 50);
            if (_speed == speed)
            {
                return;
            }

            _speed = speed;

            float percentageSpeed = speed * 100;
            float relativeSpeed = percentageSpeed - 100;

            if (!Bass.ChannelSetAttribute(_tempoStreamHandle, ChannelAttribute.Tempo, relativeSpeed))
            {
                YargLogger.LogFormatError("Failed to set channel speed: {0}!", Bass.LastError);
            }

            if (GlobalAudioHandler.IsChipmunkSpeedup && shiftPitch)
            {
                double accurateSemitoneShift = 12 * Math.Log(speed, 2);
                float finalSemitoneShift = (float) Math.Clamp(accurateSemitoneShift, -60, 60);
                if (!Bass.ChannelSetAttribute(_tempoStreamHandle, ChannelAttribute.Pitch, finalSemitoneShift))
                {
                    YargLogger.LogFormatError("Failed to set channel pitch: {0}!", Bass.LastError);
                }
            }
        }

        protected override bool AddChannels_Internal(Stream stream, params StemInfo[] stemInfos)
        {
            if (!BassAudioManager.CreateSourceStream(stream, out int sourceStream))
            {
                YargLogger.LogFormatError("Failed to load stem source stream {stem}: {0}!", Bass.LastError);
                return false;
            }

            _sourceHandles.Add(sourceStream);

            foreach (var (stem, indices, panning) in stemInfos)
            {
                if (!BassAudioManager.CreateSplitStreams(sourceStream, indices, out var streamHandles,
                    out var reverbHandles))
                {
                    YargLogger.LogFormatError("Failed to load stem {0}: {1}!", stem, Bass.LastError);
                    return false;
                }

                var isMultiChannel = indices != null && panning != null;
                var flags = isMultiChannel ? BassFlags.MixerChanMatrix : BassFlags.Default;


                //Delay any non-pitch bended stem by Whammy FFT size samples to align with pitch bended stems
                long bytes = 0;
                if (GlobalAudioHandler.UseWhammyFx && !AudioHelpers.PitchBendAllowedStems.Contains(stem))
                {
                    bytes = Bass.ChannelSeconds2Bytes(_mixerHandle, PITCH_DELAY_SECONDS);
                }

                if (!BassMix.MixerAddChannel(_mixerHandle, streamHandles.Stream, flags, bytes, 0) ||
                    !BassMix.MixerAddChannel(_mixerHandle, reverbHandles.Stream, flags, bytes, 0))
                {
                    YargLogger.LogFormatError("Failed to add channel {0} to mixer: {1}!", stem, Bass.LastError);
                    return false;
                }

                if (isMultiChannel)
                {
                    // First array = left pan, second = right pan
                    float[,] volumeMatrix = new float[2, indices.Length];

                    const int LEFT_PAN = 0;
                    const int RIGHT_PAN = 1;
                    for (int i = 0; i < indices.Length; ++i)
                    {
                        volumeMatrix[LEFT_PAN, i] = panning[2 * i];
                    }

                    for (int i = 0; i < indices.Length; ++i)
                    {
                        volumeMatrix[RIGHT_PAN, i] = panning[2 * i + 1];
                    }

                    if (!BassMix.ChannelSetMatrix(streamHandles.Stream, volumeMatrix) ||
                        !BassMix.ChannelSetMatrix(reverbHandles.Stream, volumeMatrix))
                    {
                        YargLogger.LogFormatError("Failed to set {stem} matrices: {0}!", Bass.LastError);
                        return false;
                    }
                }

                CreateChannel(stem, sourceStream, streamHandles, reverbHandles);
            }

            return true;
        }

        protected override bool RemoveChannel_Internal(SongStem stemToRemove)
        {
            int index = _channels.FindIndex(channel => channel.Stem == stemToRemove);
            if (index == -1)
            {
                return false;
            }
            _channels[index].Dispose();
            _channels.RemoveAt(index);
            UpdateThreading();
            return true;
        }

        protected override void ToggleBuffer_Internal(bool enable)
        {
            _BufferSetter(enable, Bass.PlaybackBufferLength);
        }

        protected override void SetBufferLength_Internal(int length)
        {
            _BufferSetter(SettingsManager.Settings.EnablePlaybackBuffer.Value, length);
        }

        private void _BufferSetter(bool enable, int length)
        {
            if (!enable)
            {
                length = 0;
            }

            if (!Bass.ChannelSetAttribute(_mixerHandle, ChannelAttribute.Buffer, length))
            {
                YargLogger.LogFormatError("Failed to set playback buffer: {0}!", Bass.LastError);
            }
        }

        protected override void DisposeManagedResources()
        {
            _whammySyncTimer.Stop();
            _whammySyncTimer = null;
            if (_channels.Count == 0)
            {
                return;
            }

            foreach (var channel in Channels)
            {
                channel.Dispose();
            }

            foreach (var sourceHandle in _sourceHandles)
            {
                Bass.StreamFree(sourceHandle);
            }
        }

        protected override void DisposeUnmanagedResources()
        {
            if (_mixerHandle != 0)
            {
                if (!Bass.StreamFree(_mixerHandle))
                {
                    YargLogger.LogFormatError("Failed to free mixer stream (THIS WILL LEAK MEMORY!): {0}!", Bass.LastError);
                }
            }

            if (_tempoStreamHandle != 0)
            {
                if (!Bass.StreamFree(_tempoStreamHandle))
                {
                    YargLogger.LogFormatError("Failed to free tempo stream: {0}!", Bass.LastError);
                }
            }
        }

        private void CreateChannel(SongStem stem, int sourceHandle, StreamHandle streamHandles, StreamHandle reverbHandles)
        {
            var pitchparams = BassAudioManager.SetPitchParams(stem, _speed, streamHandles, reverbHandles);
            var stemchannel = new BassStemChannel(_manager, stem, _clampStemVolume, pitchparams, streamHandles, reverbHandles);
            _length = BassAudioManager.GetLengthInSeconds(_tempoStreamHandle);
            _channels.Add(stemchannel);
            UpdateThreading();
        }

        private void UpdateThreading()
        {
            if (0 < _channels.Count && _channels.Count <= GlobalAudioHandler.MAX_THREADS)
            {
                // Mixer processing threads (for some reason this attribute is undocumented in ManagedBass?)
                // https://www.un4seen.com/forum/?topic=19491.msg136328#msg136328
                if (!Bass.ChannelSetAttribute(_mixerHandle, (ChannelAttribute) 86017, _channels.Count))
                {
                    YargLogger.LogFormatError("Failed to set mixer processing threads: {0}!", Bass.LastError);
                }
            }
        }
    }
}