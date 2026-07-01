using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        #nullable enable
        private struct StemData
        {
            public readonly SongStem     Stem;
            public readonly float[,]?    VolumeMatrix;
            public readonly StreamHandle StreamHandles;
            public readonly StreamHandle ReverbHandles;

            public StemData(SongStem stem, float[,]? volumeMatrix, StreamHandle streamHandles, StreamHandle reverbHandles)
            {
                Stem = stem;
                VolumeMatrix = volumeMatrix;
                StreamHandles = streamHandles;
                ReverbHandles = reverbHandles;
            }
        }

        #nullable disable

        private const    float WHAMMY_SYNC_INTERVAL_SECONDS = 1f;

        private static bool IsWhammyEnabled => SettingsManager.Settings.UseWhammyFx.Value;
        private        bool IsTempoStreamPlaying => _usesSinglePlaybackMixer
            ? !IsPaused
            : Bass.ChannelIsActive(_tempoStreamHandle) == PlaybackState.Playing;

        private readonly BassAudioManager _bassManager;
        private readonly bool           _usesSinglePlaybackMixer;
        private readonly int            _mixerHandle;
        private readonly List<int>      _sourceHandles = new();
        private readonly int            _tempoStreamHandle;
        private          double         _positionOffset;
        private          bool           _didSetPosition;
        private          int            _songEndHandle;
        private          double         _logicalVolume = 1.0;
        private          OutputChannel  _outputChannel;
        private          bool           _tempoStreamAddedToPlaybackMixer;
        private          float          _speed = 1.0f;
        private          int            _positionFallbackCount;
        private          Timer          _whammySyncTimer;
        private readonly List<StemData> _stemDatas = new();
        private          int            _longestHandle;

        private readonly BassNormalizer _normalizer = new();
        private          bool           _shouldNormalize;
        private          int            _gainDspHandle;
        private          float          _gain = 1.0f;

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
                    _songEndHandle = BassMix.ChannelSetSync(_longestHandle, SyncFlags.End, 0, sync);
                }

                _songEnd += value;
            }
            remove
            {
                _songEnd -= value;
            }
        }

#nullable enable
        internal BassStemMixer(string name, BassAudioManager manager, float speed, double volume, int handle,
            bool clampStemVolume, bool normalize, OutputChannel? outputChannel)
            : base(name, manager, clampStemVolume)
#nullable disable
        {
            _bassManager = manager;
            _usesSinglePlaybackMixer = manager.UsesSinglePlaybackMixer;

            BassFlags tempoFlags = BassFlags.SampleOverrideLowestVolume;
            if (_usesSinglePlaybackMixer)
            {
                tempoFlags |= BassFlags.Decode;
            }

            _tempoStreamHandle = BassFx.TempoCreate(handle, tempoFlags);
            if (_tempoStreamHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create tempo stream: {0}", Bass.LastError);
                return;
            }

            _mixerHandle = handle;
            _shouldNormalize = normalize && SettingsManager.Settings.EnableNormalization.Value;
            if (_shouldNormalize)
            {
                AddGainDSP();
            }

            _whammySyncTimer = new Timer();
            _bassManager.MasterVolumeChanged += OnMasterVolumeChanged;
            SetVolume_Internal(volume);
            SetOutputChannel_Internal(outputChannel);
            SetSpeed_Internal(speed, true);

            if (!_usesSinglePlaybackMixer)
            {
                SetBufferLength_Internal(SettingsManager.Settings.PlaybackBufferLength.Value);
            }
        }


        private void AddGainDSP()
        {
            _gainDspHandle = Bass.ChannelSetDSP(_mixerHandle, (handle, channel, buffer, length, user) =>
            {
                BassHelpers.ApplyGain(_gain, buffer, length);
            });

            if (_gainDspHandle == 0)
            {
                YargLogger.LogFormatError("Failed to add gain DSP: {0}!", Bass.LastError);
            }
        }


        private void OnMasterVolumeChanged(double volume)
        {
            SetTempoStreamVolume(_logicalVolume);
        }

        private double GetTempoStreamVolume(double logicalVolume)
        {
            double scaledVolume = BassAudioManager.ExponentialVolume(logicalVolume);
            return _usesSinglePlaybackMixer ? scaledVolume : scaledVolume * _bassManager.MasterVolume;
        }

        private void SetTempoStreamVolume(double logicalVolume)
        {
            double scaledVolume = GetTempoStreamVolume(logicalVolume);
            if (!Bass.ChannelSetAttribute(_tempoStreamHandle, ChannelAttribute.Volume, scaledVolume))
            {
                YargLogger.LogFormatError("Failed to set tempo stream volume: {0}", Bass.LastError);
            }
        }

        private bool AddTempoStream(bool paused)
        {
            if (!_usesSinglePlaybackMixer)
            {
                return true;
            }

            if (_tempoStreamAddedToPlaybackMixer)
            {
                return true;
            }

            var pausedFlag = paused ? BassFlags.MixerChanPause : BassFlags.Default;
            var flags = BassFlags.MixerChanBuffer | pausedFlag;
            bool added = _bassManager.AddToGlobalMusicPlaybackMixer(_tempoStreamHandle, _outputChannel, flags);
            _tempoStreamAddedToPlaybackMixer = added;
            return added;
        }

        private void RemoveTempoStream()
        {
            if (!_usesSinglePlaybackMixer || !_tempoStreamAddedToPlaybackMixer)
            {
                return;
            }

            _bassManager.RemoveFromPlaybackMixer(_tempoStreamHandle);
            _tempoStreamAddedToPlaybackMixer = false;
        }

        private bool PlayTempoStream()
        {
            if (!_usesSinglePlaybackMixer)
            {
                if (!Bass.ChannelPlay(_tempoStreamHandle, _didSetPosition))
                {
                    YargLogger.LogFormatError("Failed to play tempo stream: {0}", Bass.LastError);
                    return false;
                }

                _didSetPosition = false;
                return true;
            }

            if (!AddTempoStream(paused: true))
            {
                return false;
            }

            if ((int) BassMix.ChannelFlags(_tempoStreamHandle, BassFlags.Default, BassFlags.MixerChanPause) == -1)
            {
                YargLogger.LogFormatError("Failed to resume tempo stream: {0}", Bass.LastError);
                return false;
            }
            return true;
        }

        private bool PauseTempoStream()
        {
            if (!_usesSinglePlaybackMixer)
            {
                if (!Bass.ChannelPause(_tempoStreamHandle))
                {
                    YargLogger.LogFormatError("Failed to pause tempo stream: {0}", Bass.LastError);
                    return false;
                }
                return true;
            }

            if (!_tempoStreamAddedToPlaybackMixer)
            {
                return true;
            }

            if ((int) BassMix.ChannelFlags(_tempoStreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause) == -1)
            {
                YargLogger.LogFormatError("Failed to pause tempo stream: {0}", Bass.LastError);
                return false;
            }
            return true;
        }

        protected override int Play_Internal()
        {
            if (_shouldNormalize)
            {
                _gain = _normalizer.Gain;
                _normalizer.OnGainAdjusted -= OnGainAdjusted;
                _normalizer.OnGainAdjusted += OnGainAdjusted;
            }

            if (_usesSinglePlaybackMixer || !IsTempoStreamPlaying)
            {
                if (!PlayTempoStream())
                {
                    return (int) Bass.LastError;
                }
            }

            UnpauseDelay = 0;

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

        private void OnGainAdjusted(float adjustedGain)
        {
            _gain = adjustedGain;
        }

        protected override void FadeIn_Internal(double maxVolume, double duration)
        {
            _logicalVolume = maxVolume;
            float scaled = (float) GetTempoStreamVolume(maxVolume);
            Bass.ChannelSlideAttribute(_tempoStreamHandle, ChannelAttribute.Volume, scaled, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        protected override void FadeOut_Internal(double duration)
        {
            _logicalVolume = 0;
            Bass.ChannelSlideAttribute(_tempoStreamHandle, ChannelAttribute.Volume, 0, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        protected override int Pause_Internal()
        {
            if (!_usesSinglePlaybackMixer && !IsTempoStreamPlaying)
            {
                return 0;
            }

            if (!PauseTempoStream())
            {
                return (int) Bass.LastError;
            }

            return 0;
        }

        protected override double GetPosition_Internal()
        {
            return Math.Max(0, GetTempoStreamPosition_Internal());
        }

        protected override double GetSyncPosition_Internal()
        {
            return GetTempoStreamPosition_Internal() - GetAudibleSyncLatency_Internal();
        }

        private double GetTempoStreamPosition_Internal()
        {
            long playedBytes = _usesSinglePlaybackMixer
                ? BassMix.ChannelGetPosition(_tempoStreamHandle, PositionFlags.Bytes)
                : Bass.ChannelGetPosition(_tempoStreamHandle, PositionFlags.Bytes);

            if (playedBytes < 0)
            {
                _positionFallbackCount++;
                if (_positionFallbackCount == 1 || _positionFallbackCount % 1000 == 0)
                {
                    YargLogger.LogFormatWarning(
                        "Failed to get tempo stream playback position. " +
                        "Falling back to decoding position. Count: {0}, played bytes: {1}, error: {2}",
                        _positionFallbackCount, playedBytes, Bass.LastError);
                }

                return GetDecodingPosition_Internal();
            }

            double seconds = Bass.ChannelBytes2Seconds(_tempoStreamHandle, playedBytes);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to convert played bytes to seconds: {0}!", Bass.LastError);
                return GetDecodingPosition_Internal();
            }

            return seconds + _positionOffset;
        }

        protected override double GetEstimatedOutputLatency_Internal()
        {
            return GetAudibleSyncLatency_Internal();
        }

        protected override double GetAudibleSyncLatency_Internal()
        {
            if (!_usesSinglePlaybackMixer)
            {
                return 0;
            }

            return GetConfiguredOutputLatency() + GetDeviceOutputLatency();
        }

        protected override double GetCommandLatency_Internal()
        {
            return GetConfiguredOutputLatency() + GetDeviceOutputLatency();
        }

        protected override double GetStartLatency_Internal()
        {
            return GetDeviceOutputLatency();
        }

        private static double GetDeviceOutputLatency()
        {
            return Math.Max(0, GlobalAudioHandler.PlaybackLatency) / 1000.0;
        }

        private static double GetConfiguredOutputLatency()
        {
            int bufferLength = SettingsManager.Settings?.PlaybackBufferLength.Value ?? 0;
            int minimumLength = GlobalAudioHandler.MinimumBufferLength;
            if (bufferLength > 0 && minimumLength > 0 && bufferLength < minimumLength)
            {
                bufferLength = minimumLength;
            }

            return Math.Max(0, bufferLength) / 1000.0;
        }

        private void FlushTempoStreamBuffer()
        {
            if (!BassMix.ChannelSetPosition(_tempoStreamHandle, 0, PositionFlags.Bytes))
            {
                Bass.ChannelSetPosition(_tempoStreamHandle, 0);
            }
        }

        protected override double GetDecodingPosition_Internal()
        {
            long positionBytes = Bass.ChannelGetPosition(_mixerHandle, PositionFlags.Bytes);
            bool isMixerPosition = positionBytes >= 0;

            if (!isMixerPosition)
            {
                return _positionOffset;
            }

            double seconds = Bass.ChannelBytes2Seconds(_mixerHandle, positionBytes);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to convert bytes to seconds: {0}!", Bass.LastError);
                return _positionOffset;
            }

            return seconds + _positionOffset;
        }



        protected override double GetVolume_Internal()
        {
            return _logicalVolume;
        }

        protected override void SetPosition_Internal(double position)
        {
            bool wasPlaying = !IsPaused;
            Pause_Internal();
            RemoveTempoStream();
            FlushTempoStreamBuffer();

            var channels = BassMix.MixerGetChannels(_mixerHandle);
            if (channels != null)
            {
                foreach (var channel in channels)
                {
                    if (!BassMix.MixerRemoveChannel(channel))
                    {
                        YargLogger.LogDebug("Failed to remove channel from mixer");
                    }
                }
            }
            AddChannelsToMixer(_stemDatas);

            foreach (var channel in _channels)
            {
                channel.SetPosition(position);
            }
            _didSetPosition = true;
            _positionOffset = position;

            if (_usesSinglePlaybackMixer)
            {
                AddTempoStream(paused: true);
            }

            YargLogger.LogFormatDebug(
                "Set BASS stem mixer position. Single mixer: {0}, configured buffer: {1:0.000000}, device latency: {2:0.000000}, " +
                "audible latency: {3:0.000000}, command latency: {4:0.000000}, requested position: {5:0.000000}, " +
                "raw position: {6:0.000000}, sync position: {7:0.000000}",
                _usesSinglePlaybackMixer, GetConfiguredOutputLatency(), GetDeviceOutputLatency(), GetAudibleSyncLatency_Internal(),
                GetCommandLatency_Internal(), position, GetPosition_Internal(), GetSyncPosition_Internal()
            );

            if (wasPlaying)
            {
                Play_Internal();
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            _logicalVolume = volume;
            SetTempoStreamVolume(volume);
        }

        private int GetPlaybackDataHandle()
        {
            if (_usesSinglePlaybackMixer)
            {
                return _bassManager.GetGlobalMusicPlaybackMixerHandle();
            }

            return _tempoStreamHandle;
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

            int dataHandle = GetPlaybackDataHandle();
            if (dataHandle == 0)
            {
                return -1;
            }

            int data = Bass.ChannelGetData(dataHandle, buffer, flags);
            if (data < 0)
            {
                return (int) Bass.LastError;
            }
            return data;
        }

        protected override int GetSampleData_Internal(float[] buffer)
        {
            int dataHandle = GetPlaybackDataHandle();
            if (dataHandle == 0)
            {
                return -1;
            }

            int data = Bass.ChannelGetData(dataHandle, buffer, (buffer.Length * 4) | (int) (DataFlags.Float));
            if (data < 0)
            {
                return (int) Bass.LastError;
            }
            return data;
        }

        protected override int GetLevel_Internal(float[] level)
        {
            int dataHandle = GetPlaybackDataHandle();
            if (dataHandle == 0)
            {
                return -1;
            }

            bool status = Bass.ChannelGetLevel(dataHandle, level, 0.2f, LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS);
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

            BassAudioManager.SetSpeed(speed, _tempoStreamHandle, shiftPitch);
        }

        protected override bool AddChannels_Internal(Stream stream, params StemInfo[] stemInfos)
        {
            if (_shouldNormalize)
            {
                if (!_normalizer.AddStream(stream, stemInfos))
                {
                    YargLogger.LogError("Failed to add stream to normalizer. Disabling normalization.");
                    _shouldNormalize = false;
                }
            }

            if (!BassAudioManager.CreateSourceStream(stream, out int sourceStream))
            {
                YargLogger.LogFormatError("Failed to load stem source stream: {0}!", Bass.LastError);
                return false;
            }

            _sourceHandles.Add(sourceStream);

            List<StemData> stemDatas = new();
            var groupedByStem = stemInfos.GroupBy(info => info.Stem);
            foreach (var group in groupedByStem)
            {
                var stem = group.Key;
                var allIndices = group
                    .Where(info => info.Indices != null)
                    .SelectMany(info => info.Indices)
                    .ToArray();

                var handles = BassAudioManager.CreateSplitStreams(sourceStream, allIndices);
                if (handles == null)
                {
                    YargLogger.LogFormatError("Failed to load stem {0}: {1}!", stem, Bass.LastError);
                    continue;
                }

                var (streamHandle, reverbHandle) = handles.Value;
                float[,] volumeMatrix = BuildVolumeMatrix(group, allIndices.Length);
                stemDatas.Add(new StemData(stem, volumeMatrix, streamHandle, reverbHandle));
            }

            if (!stemDatas.Any())
            {
                YargLogger.LogError("Failed to load any stems!");
                return false;
            }

            if (!AddChannelsToMixer(stemDatas))
            {
                return false;
            }
            _stemDatas.AddRange(stemDatas);

            foreach (var stemStreamData in stemDatas)
            {
                CreateChannel(
                    stem: stemStreamData.Stem,
                    sourceHandle: sourceStream,
                    streamHandles: stemStreamData.StreamHandles,
                    reverbHandles: stemStreamData.ReverbHandles
                );
            }

            return true;
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _outputChannel = channel;
            if (!_usesSinglePlaybackMixer)
            {
                BassHelpers.UpdateOutputChannels(_tempoStreamHandle, channel);
                return;
            }

            bool wasPlaying = !IsPaused;
            RemoveTempoStream();
            AddTempoStream(!wasPlaying);
        }

        protected override void SetOutputDevice_Internal(OutputDevice device)
        {
            if (device is not BassOutputDevice bassDevice)
            {
                return;
            }

            if (_usesSinglePlaybackMixer != _bassManager.UsesSinglePlaybackMixer)
            {
                if (!IsPaused)
                {
                    Pause();
                }
                RemoveTempoStream();
                YargLogger.LogWarning("BASS playback mixer mode changed for active stem mixer. Playback stopped; reload mixer to use new topology.");
                return;
            }

            bool wasPlaying = !IsPaused;
            double position = GetPosition_Internal();
            RemoveTempoStream();

            foreach (StemData stemData in _stemDatas)
            {
                if (!Bass.ChannelSetDevice(stemData.ReverbHandles.Stream, bassDevice.DeviceId))
                {
                    YargLogger.LogFormatError("Failed to change device for reverb handle: {0}", Bass.LastError);
                }

                if (!Bass.ChannelSetDevice(stemData.StreamHandles.Stream, bassDevice.DeviceId))
                {
                    YargLogger.LogFormatError("Failed to change device for stream handle: {0}", Bass.LastError);
                }
            }

            foreach (int handle in _sourceHandles)
            {
                if (!Bass.ChannelSetDevice(handle, bassDevice.DeviceId))
                {
                    YargLogger.LogFormatError("Failed to change device for source handle: {0}", Bass.LastError);
                }
            }

            if (_mixerHandle != 0 && !Bass.ChannelSetDevice(_mixerHandle, bassDevice.DeviceId))
            {
                YargLogger.LogFormatError("Failed to change device for mixer handle: {0}", Bass.LastError);
            }

            if (_tempoStreamHandle != 0 && !Bass.ChannelSetDevice(_tempoStreamHandle, bassDevice.DeviceId))
            {
                YargLogger.LogFormatError("Failed to change device for tempo stream handle: {0}", Bass.LastError);
            }

            AddTempoStream(true);
            SetPosition_Internal(position);
            if (wasPlaying)
            {
                Play_Internal();
            }
        }

        private bool AddChannelsToMixer(IEnumerable<StemData> stemStreamDataList)
        {
            foreach (var stemStreamData in stemStreamDataList)
            {
                var stem = stemStreamData.Stem;
                var streamHandles = stemStreamData.StreamHandles;
                var reverbHandles = stemStreamData.ReverbHandles;
                var volumeMatrix = stemStreamData.VolumeMatrix;

                // Delay any non-pitch bended stem by Whammy FFT size samples to align with pitch bended stems
                long bytes = 0;
                if (GlobalAudioHandler.UseWhammyFx && !AudioHelpers.PitchBendAllowedStems.Contains(stem))
                {
                    Bass.ChannelGetAttribute(streamHandles.Stream, ChannelAttribute.Frequency, out var freq);
                    var seconds = GlobalAudioHandler.WHAMMY_FFT_DEFAULT / freq;
                    bytes = Bass.ChannelSeconds2Bytes(_mixerHandle, seconds);
                }

                var flags = volumeMatrix != null ? BassFlags.MixerChanMatrix : BassFlags.Default;
                if (!BassMix.MixerAddChannel(_mixerHandle, streamHandles.Stream, flags, bytes, 0) ||
                    !BassMix.MixerAddChannel(_mixerHandle, reverbHandles.Stream, flags, bytes, 0))
                {
                    YargLogger.LogFormatError("Failed to add channel {0} to mixer: {1}!", stem, Bass.LastError);
                    return false;
                }

                if (volumeMatrix == null)
                {
                    continue;
                }

                if (!BassMix.ChannelSetMatrix(streamHandles.Stream, volumeMatrix) || !BassMix.ChannelSetMatrix(reverbHandles.Stream, volumeMatrix))
                {
                    YargLogger.LogFormatError("Failed to set {0} matrices: {1}!", stem, Bass.LastError);
                    return false;
                }
            }
            return true;
        }



        internal static float[,] BuildVolumeMatrix(StemInfo info)
        {
            if (info.Indices == null || info.Panning == null)
            {
                return null;
            }
            return BuildVolumeMatrix(new[] { info }, info.Indices.Length);
        }

#nullable enable
        private static float[,]? BuildVolumeMatrix(IEnumerable<StemInfo> infos, int totalChannels)
#nullable disable
        {
            if (totalChannels == 0)
            {
                return null;
            }

            float[,] volumeMatrix = new float[2, totalChannels];
            const int leftPan = 0;
            const int rightPan = 1;

            int channelIndex = 0;
            foreach (var info in infos)
            {
                var panning = info.Panning;
                for (int i = 0; i < info.Indices.Length; ++i)
                {
                    volumeMatrix[leftPan, channelIndex] = panning[2 * i];
                    volumeMatrix[rightPan, channelIndex] = panning[2 * i + 1];
                    channelIndex++;
                }
            }
            return volumeMatrix;
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
            _stemDatas.RemoveAll(stem => stem.Stem == stemToRemove);
            UpdateThreading();
            return true;
        }

        protected override void SetBufferLength_Internal(int length)
        {
            if (!_usesSinglePlaybackMixer)
            {
                if (length > 0 && GlobalAudioHandler.MinimumBufferLength > 0 && length < GlobalAudioHandler.MinimumBufferLength)
                {
                    length = GlobalAudioHandler.MinimumBufferLength;
                }

                float lengthInSeconds = length / 1000f;
                if (!Bass.ChannelSetAttribute(_tempoStreamHandle, ChannelAttribute.Buffer, lengthInSeconds))
                {
                    YargLogger.LogFormatError("Failed to set tempo stream buffer: {0}!", Bass.LastError);
                }
                return;
            }

            // Playback buffering belongs to the global music playback mixer.
        }

        protected override void DisposeManagedResources()
        {
            _whammySyncTimer.Stop();
            _whammySyncTimer = null;
            _stemDatas.Clear();

            if (_gainDspHandle != 0)
            {
                Bass.ChannelRemoveDSP(_mixerHandle, _gainDspHandle);
            }

            _bassManager.MasterVolumeChanged -= OnMasterVolumeChanged;
            _normalizer.OnGainAdjusted -= OnGainAdjusted;
            _normalizer.Dispose();

            foreach (var channel in Channels)
            {
                channel.Dispose();
            }
        }

        protected override void DisposeUnmanagedResources()
        {
            RemoveTempoStream();

            bool mixerFreedByTempo = false;
            if (_tempoStreamHandle != 0)
            {
                if (!Bass.StreamFree(_tempoStreamHandle))
                {
                    YargLogger.LogFormatError("Failed to free tempo stream: {0}!", Bass.LastError);
                }
                else
                {
                    // BASS_FX_FREESOURCE is automatically set because of flag value overlaps,
                    // meaning BASS_FX has already automatically freed _mixerHandle.
                    mixerFreedByTempo = true;
                }
            }

            if (_mixerHandle != 0 && !mixerFreedByTempo)
            {
                if (!Bass.StreamFree(_mixerHandle))
                {
                    YargLogger.LogFormatError("Failed to free mixer stream (THIS WILL LEAK MEMORY!): {0}!", Bass.LastError);
                }
            }

            foreach (var sourceHandle in _sourceHandles)
            {
                if (!Bass.StreamFree(sourceHandle))
                {
                    YargLogger.LogFormatError("Failed to free source stream (THIS WILL LEAK MEMORY!): {0}!", Bass.LastError);
                }
            }
        }

        private void CreateChannel(SongStem stem, int sourceHandle, StreamHandle streamHandles, StreamHandle reverbHandles)
        {
            var pitchparams = BassAudioManager.SetPitchParams(stem, _speed, streamHandles, reverbHandles);
            var stemchannel = new BassStemChannel(_manager, stem, _clampStemVolume, sourceHandle, pitchparams, streamHandles, reverbHandles);
            double length = BassAudioManager.GetLengthInSeconds(streamHandles.Stream);
            if (length > _length)
            {
                _longestHandle = streamHandles.Stream;
                _length = length;
            }
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
