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
        private        bool IsTempoStreamPlaying => _tempoStream.IsPlaying;

        private readonly BassAudioManager _bassManager;
        private readonly int            _mixerHandle;
        private readonly List<int>      _sourceHandles = new();
        private readonly BassTempoStream _tempoStream;
        private          double         _positionOffset;
        private          bool           _didSetPosition;
        private          int            _songEndHandle;
        private          OutputChannel  _outputChannel;
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
            bool clampStemVolume, bool normalize, OutputChannel? outputChannel, bool isDecodeStream)
            : base(name, manager, clampStemVolume)
#nullable disable
        {
            _bassManager = manager;
            _mixerHandle = handle;

            if (isDecodeStream)
            {
                _tempoStream = new DecodeBassTempoStream(manager, handle);
            }
            else
            {
                _tempoStream = new DirectBassTempoStream(manager, handle);
            }

            if (_tempoStream.Handle == 0)
            {
                return;
            }

            _shouldNormalize = normalize && SettingsManager.Settings.EnableNormalization.Value;
            if (_shouldNormalize)
            {
                AddGainDSP();
            }

            _whammySyncTimer = new Timer();
            SetVolume_Internal(volume);
            SetOutputChannel_Internal(outputChannel);
            SetSpeed_Internal(speed, true);

            _tempoStream.SetBufferLength(SettingsManager.Settings.PlaybackBufferLength.Value);
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

        protected override int Play_Internal()
        {
            if (_shouldNormalize)
            {
                _gain = _normalizer.Gain;
                _normalizer.OnGainAdjusted -= OnGainAdjusted;
                _normalizer.OnGainAdjusted += OnGainAdjusted;
            }

            if (_tempoStream.IsDecodeStream || !IsTempoStreamPlaying)
            {
                if (!_tempoStream.Play(_didSetPosition))
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

        /// <summary>
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
            _tempoStream.FadeIn(maxVolume, duration);
        }

        protected override void FadeOut_Internal(double duration)
        {
            _tempoStream.FadeOut(duration);
        }

        protected override int Pause_Internal()
        {
            if (!_tempoStream.IsDecodeStream && !IsTempoStreamPlaying)
            {
                return 0;
            }

            if (!_tempoStream.Pause())
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
            return _tempoStream.GetPosition(_positionOffset, GetDecodingPosition_Internal);
        }

        protected override double GetEstimatedOutputLatency_Internal()
        {
            return GetAudibleSyncLatency_Internal();
        }

        protected override double GetAudibleSyncLatency_Internal()
        {
            return _tempoStream.GetAudibleSyncLatency();
        }

        protected override double GetCommandLatency_Internal()
        {
            return DecodeBassTempoStream.GetConfiguredOutputLatency() + DecodeBassTempoStream.GetDeviceOutputLatency();
        }

        protected override double GetStartLatency_Internal()
        {
            double latency = DecodeBassTempoStream.GetDeviceOutputLatency();

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // Direct playback channels on Windows need the BASS device buffer filled after start/seek.
            // ChannelGetPosition already accounts for this during steady-state playback, but audible
            // resume/seek alignment still needs the physical device-buffer delay.
            if (!_tempoStream.IsDecodeStream)
            {
                latency += Math.Max(0, Bass.DeviceBufferLength) / 1000.0;
            }
#endif

            return latency;
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
            return _tempoStream.GetVolume();
        }

        protected override void SetPosition_Internal(double position)
        {
            bool wasPlaying = !IsPaused;
            Pause_Internal();
            _tempoStream.RemoveFromMixer();
            _tempoStream.FlushBuffer();

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

            _tempoStream.AddToMixer(_outputChannel, paused: true);

            YargLogger.LogFormatDebug(
                "Set BASS stem mixer position. Single mixer: {0}, configured buffer: {1:0.000000}, device latency: {2:0.000000}, " +
                "audible latency: {3:0.000000}, command latency: {4:0.000000}, requested position: {5:0.000000}, " +
                "raw position: {6:0.000000}, sync position: {7:0.000000}",
                _tempoStream.IsDecodeStream, DecodeBassTempoStream.GetConfiguredOutputLatency(), DecodeBassTempoStream.GetDeviceOutputLatency(), GetAudibleSyncLatency_Internal(),
                GetCommandLatency_Internal(), position, GetPosition_Internal(), GetSyncPosition_Internal()
            );

            if (wasPlaying)
            {
                Play_Internal();
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            _tempoStream.SetVolume(volume);
        }

        private int GetPlaybackDataHandle()
        {
            return _tempoStream.PlaybackDataHandle;
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

            _tempoStream.SetSpeed(speed, shiftPitch);
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
            _tempoStream.SetOutputChannel(channel, !IsPaused);
        }

        protected override void SetOutputDevice_Internal(OutputDevice device)
        {
            if (device is not BassOutputDevice bassDevice)
            {
                return;
            }

            if (_tempoStream.IsDecodeStream != _bassManager.UsesSinglePlaybackMixer)
            {
                if (!IsPaused)
                {
                    Pause();
                }
                _tempoStream.RemoveFromMixer();
                YargLogger.LogWarning("BASS playback mixer mode changed for active stem mixer. Playback stopped; reload mixer to use new topology.");
                return;
            }

            bool wasPlaying = !IsPaused;
            double position = GetPosition_Internal();
            _tempoStream.RemoveFromMixer();

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

            _tempoStream.SetDevice(bassDevice.DeviceId);

            _tempoStream.AddToMixer(_outputChannel, paused: true);
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
            _tempoStream.SetBufferLength(length);
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

            _normalizer.OnGainAdjusted -= OnGainAdjusted;
            _normalizer.Dispose();

            foreach (var channel in Channels)
            {
                channel.Dispose();
            }
        }

        protected override void DisposeUnmanagedResources()
        {
            bool mixerFreedByTempo = false;
            if (_tempoStream != null)
            {
                _tempoStream.Dispose();
                mixerFreedByTempo = true;
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
