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
        private const    double MASTER_CLOCK_COMPARISON_LOG_INTERVAL_SECONDS = 5.0;

        private static bool IsWhammyEnabled => SettingsManager.Settings.UseWhammyFx.Value;
        private        bool IsPlaying       => _isPlaying;

        private readonly BassAudioManager _bassManager;
        private readonly int            _mixerHandle;
        private readonly List<int>      _sourceHandles = new();
        private readonly int            _tempoStreamHandle;
        private          double         _positionOffset;
        private          int            _songEndHandle;
        private          bool           _isPlaying;
        private          bool           _isSeeking;
        private          OutputChannel  _outputChannel;
        private          float          _speed = 1.0f;
        private          int            _positionFallbackCount;
        private          double         _masterClockAnchorMasterTime;
        private          double         _masterClockAnchorSongPosition;
        private          float          _masterClockAnchorSpeed = 1.0f;
        private          bool           _masterClockAnchored;
        private          double         _lastMasterClockComparisonLogTime = double.NegativeInfinity;
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
            _tempoStreamHandle = BassFx.TempoCreate(handle, BassFlags.SampleOverrideLowestVolume | BassFlags.Decode);
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
            SetVolume_Internal(volume);
            SetOutputChannel_Internal(outputChannel);
            SetSpeed_Internal(speed, true);
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


        private void AddTempoStream(bool paused)
        {
            var pausedFlag = paused ? BassFlags.MixerChanPause : BassFlags.Default;
            var flags = BassFlags.MixerChanBuffer | pausedFlag;
            if (_bassManager.AddToMasterMixer(_tempoStreamHandle, _outputChannel, flags))
            {
                AnchorMasterClock(_positionOffset);
            }
        }

        private void RemoveTempoStream()
        {
            _bassManager.RemoveFromMasterMixer(_tempoStreamHandle);
            _masterClockAnchored = false;
        }

        private bool SetTempoStreamPaused(bool paused)
        {
            var flags = paused ? BassFlags.MixerChanPause : BassFlags.Default;
            if ((int) BassMix.ChannelFlags(_tempoStreamHandle, flags, BassFlags.MixerChanPause) == -1)
            {
                YargLogger.LogFormatError("Failed to {0} tempo stream: {1}", paused ? "pause" : "resume", Bass.LastError);
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

            if (!IsPlaying)
            {
                if (!SetTempoStreamPaused(false))
                {
                    return (int) Bass.LastError;
                }
                AnchorMasterClock(_positionOffset);
                _isPlaying = true;
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

        private void OnGainAdjusted(float adjustedGain)
        {
            _gain = adjustedGain;
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
            if (!IsPlaying)
            {
                return 0;
            }

            // Get current heard position in seconds before we pause
            double pausePosition = GetPosition_Internal();

            if (!SetTempoStreamPaused(true))
            {
                return (int) Bass.LastError;
            }

            _isPlaying = false;

            if (!_isSeeking)
            {
                if (!BassMix.ChannelSetPosition(_tempoStreamHandle, 0, PositionFlags.Bytes))
                {
                    Bass.ChannelSetPosition(_tempoStreamHandle, 0);
                }

                // Seek stems to the heard position
                foreach (var channel in _channels)
                {
                    channel.SetPosition(pausePosition);
                }
                _positionOffset = pausePosition;
            }

            AnchorMasterClock(pausePosition);

            return 0;
        }

        protected override double GetPosition_Internal()
        {
            double fallbackPosition = GetTempoStreamPosition_Internal();
            if (!TryGetMasterClockPosition(out double masterClockPosition))
            {
                return fallbackPosition;
            }

            LogMasterClockComparison(masterClockPosition, fallbackPosition);
            return masterClockPosition;
        }

        private double GetTempoStreamPosition_Internal()
        {
            long playedBytes = BassMix.ChannelGetPosition(_tempoStreamHandle, PositionFlags.Bytes);

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

        private bool TryGetMasterClockPosition(out double position)
        {
            position = 0;
            if (!_masterClockAnchored)
            {
                return false;
            }

            if (!IsPlaying)
            {
                position = _masterClockAnchorSongPosition;
                return true;
            }

            if (!_bassManager.TryGetMasterMixerTime(out double masterTime))
            {
                return false;
            }

            double elapsed = masterTime - _masterClockAnchorMasterTime;
            if (elapsed < 0)
            {
                YargLogger.LogFormatWarning(
                    "Master mixer clock moved backwards. Anchor: {0:0.000000}, now: {1:0.000000}",
                    _masterClockAnchorMasterTime, masterTime);
                return false;
            }

            position = _masterClockAnchorSongPosition + elapsed * _masterClockAnchorSpeed;
            return true;
        }

        private void AnchorMasterClock(double songPosition)
        {
            if (!_bassManager.TryGetMasterMixerTime(out double masterTime))
            {
                _masterClockAnchored = false;
                return;
            }

            _masterClockAnchorMasterTime = masterTime;
            _masterClockAnchorSongPosition = songPosition;
            _masterClockAnchorSpeed = _speed;
            _masterClockAnchored = true;
        }

        private void RollMasterClockAnchorForward()
        {
            if (TryGetMasterClockPosition(out double position))
            {
                AnchorMasterClock(position);
            }
        }

        private void LogMasterClockComparison(double masterClockPosition, double tempoStreamPosition)
        {
            if (!_bassManager.TryGetMasterMixerTime(out double masterTime) ||
                masterTime - _lastMasterClockComparisonLogTime < MASTER_CLOCK_COMPARISON_LOG_INTERVAL_SECONDS)
            {
                return;
            }

            _lastMasterClockComparisonLogTime = masterTime;
            double decodingPosition = GetDecodingPosition_Internal();
            YargLogger.LogFormatTrace(
                "Master-clock position comparison. Master-derived: {0:0.000000}, tempo-played: {1:0.000000}, " +
                "decoding/source: {2:0.000000}, master-tempo delta: {3:0.000000}, master-decoding delta: {4:0.000000}",
                masterClockPosition, tempoStreamPosition, decodingPosition,
                masterClockPosition - tempoStreamPosition, masterClockPosition - decodingPosition);
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
            if (!Bass.ChannelGetAttribute(_tempoStreamHandle, ChannelAttribute.Volume, out float volume))
            {
                YargLogger.LogFormatError("Failed to get volume: {0}", Bass.LastError);
            }
            return BassAudioManager.LogarithmicVolume(volume);
        }

        protected override void SetPosition_Internal(double position)
        {
            _isSeeking = true;
            try
            {
                var wasPlaying = IsPlaying;
                Pause_Internal();

                if (!BassMix.ChannelSetPosition(_tempoStreamHandle, 0, PositionFlags.Bytes))
                {
                    Bass.ChannelSetPosition(_tempoStreamHandle, 0);
                }

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
                _positionOffset = position;
                AnchorMasterClock(position);

                if (wasPlaying)
                {
                    Play_Internal();
                }
            }
            finally
            {
                _isSeeking = false;
            }
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

            if (_bassManager.MasterMixerHandle == 0)
            {
                return -1;
            }

            int data = Bass.ChannelGetData(_bassManager.MasterMixerHandle, buffer, flags);
            if (data < 0)
            {
                return (int) Bass.LastError;
            }
            return data;
        }

        protected override int GetSampleData_Internal(float[] buffer)
        {
            if (_bassManager.MasterMixerHandle == 0)
            {
                return -1;
            }

            int data = Bass.ChannelGetData(_bassManager.MasterMixerHandle, buffer, (buffer.Length * 4) | (int) (DataFlags.Float));
            if (data < 0)
            {
                return (int) Bass.LastError;
            }
            return data;
        }

        protected override int GetLevel_Internal(float[] level)
        {
            if (_bassManager.MasterMixerHandle == 0)
            {
                return -1;
            }

            bool status = Bass.ChannelGetLevel(_bassManager.MasterMixerHandle, level, 0.2f, LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS);
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
            RollMasterClockAnchorForward();
            _speed = speed;
            AnchorMasterClock(_masterClockAnchored ? _masterClockAnchorSongPosition : _positionOffset);

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
            bool wasPlaying = IsPlaying;
            double position = _masterClockAnchored || IsPlaying ? GetPosition_Internal() : _positionOffset;
            RemoveTempoStream();
            AddTempoStream(!wasPlaying);
            AnchorMasterClock(position);
        }

        protected override void SetOutputDevice_Internal(OutputDevice device)
        {
            if (device is not BassOutputDevice bassDevice)
            {
                return;
            }

            bool wasPlaying = IsPlaying;
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
            // Playback buffer belongs to the non-decoding master mixer. This mixer is a decoding source,
            // so BASS_ATTRIB_BUFFER is not available here.
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
