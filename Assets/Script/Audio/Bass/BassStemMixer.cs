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

        private struct ScheduledSpeedChange
        {
            public readonly double MasterTime;
            public readonly float  Speed;

            public ScheduledSpeedChange(double masterTime, float speed)
            {
                MasterTime = masterTime;
                Speed = speed;
            }
        }
        #nullable disable

        private const    float WHAMMY_SYNC_INTERVAL_SECONDS = 1f;
        private const    double MASTER_CLOCK_COMPARISON_LOG_INTERVAL_SECONDS = 5.0;
        private const    double MAX_REASONABLE_OUTPUT_LATENCY_SECONDS = 10.0;

        private static bool IsWhammyEnabled => SettingsManager.Settings.UseWhammyFx.Value;
        private        bool IsPlaying       => _isPlaying;

        private readonly BassAudioManager _bassManager;
        private readonly bool           _usesSinglePlaybackMixer;
        private readonly int            _mixerHandle;
        private readonly List<int>      _sourceHandles = new();
        private readonly int            _tempoStreamHandle;
        private          double         _positionOffset;
        private          int            _songEndHandle;
        private          double         _logicalVolume = 1.0;
        private          bool           _isPlaying;
        private          bool           _isSeeking;
        private          OutputChannel  _outputChannel;
        private          float          _speed = 1.0f;
        private          float          _audibleSpeed = 1.0f;
        private          int            _positionFallbackCount;
        private          double         _renderClockAnchorMasterTime;
        private          double         _renderClockAnchorSongPosition;
        private          bool           _renderClockAnchored;
        private          double         _audibleClockAnchorMasterTime;
        private          double         _audibleClockAnchorSongPosition;
        private          bool           _audibleClockAnchored;
        private          double         _lastMasterClockComparisonLogTime = double.NegativeInfinity;
        private          Timer          _whammySyncTimer;
        private readonly List<StemData> _stemDatas = new();
        private readonly List<ScheduledSpeedChange> _pendingAudibleSpeedChanges = new();
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

        private void AddTempoStream(bool paused)
        {
            if (!_usesSinglePlaybackMixer)
            {
                return;
            }

            var pausedFlag = paused ? BassFlags.MixerChanPause : BassFlags.Default;
            var flags = BassFlags.MixerChanBuffer | pausedFlag;
            if (_bassManager.AddToGlobalMusicPlaybackMixer(_tempoStreamHandle, _outputChannel, flags))
            {
                ReanchorTransport(_positionOffset, delayAudible: !paused);
            }
        }

        private void RemoveTempoStream()
        {
            if (_usesSinglePlaybackMixer)
            {
                _bassManager.RemoveFromPlaybackMixer(_tempoStreamHandle);
            }
            _renderClockAnchored = false;
            _audibleClockAnchored = false;
        }

        private bool SetTempoStreamPaused(bool paused)
        {
            if (!_usesSinglePlaybackMixer)
            {
                bool success = paused ? Bass.ChannelPause(_tempoStreamHandle) : Bass.ChannelPlay(_tempoStreamHandle, false);
                if (!success)
                {
                    YargLogger.LogFormatError("Failed to {0} tempo stream: {1}", paused ? "pause" : "play", Bass.LastError);
                    return false;
                }
                return true;
            }

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
                _isPlaying = true;

                double delay = UnpauseDelay;
                UnpauseDelay = 0;

                ReanchorTransport(_positionOffset, delayAudible: true, delay);
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
            if (!IsPlaying)
            {
                return 0;
            }

            double pausePosition = GetPosition_Internal();

            if (!SetTempoStreamPaused(true))
            {
                return (int) Bass.LastError;
            }

            _isPlaying = false;

            if (!_isSeeking)
            {
                FlushTempoStreamBuffer();

                // Seek stems to the heard position
                foreach (var channel in _channels)
                {
                    channel.SetPosition(pausePosition);
                }
                _positionOffset = pausePosition;
            }

            ReanchorTransport(pausePosition);

            return 0;
        }

        protected override double GetPosition_Internal()
        {
            if (!_usesSinglePlaybackMixer)
            {
                return Math.Max(0, GetTempoStreamPosition_Internal());
            }

            if (!TryGetPlaybackClockTime(out double masterTime) ||
                !TryGetAudibleClockPosition(masterTime, out double audiblePosition))
            {
                return GetTempoStreamPosition_Internal();
            }

            LogMasterClockComparison(masterTime, audiblePosition);
            return Math.Max(0, audiblePosition);
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
            return EstimateOutputLatency();
        }

        private double EstimateOutputLatency()
        {
            double fallback = GetConfiguredOutputLatency();
            double deviceLatency = GlobalAudioHandler.PlaybackLatency / 1000.0;
            if (!IsPlaying)
            {
                return fallback + deviceLatency;
            }

            if (TryGetRenderClockPosition(out double renderPosition) &&
                TryGetTempoStreamPosition(out double heardPosition))
            {
                double sourceDelay = Math.Max(0, renderPosition - heardPosition);
                double speed = Math.Max(Math.Abs(_speed), 0.001f);
                double measured = Math.Min(sourceDelay / speed, MAX_REASONABLE_OUTPUT_LATENCY_SECONDS);
                return Math.Max(measured, fallback) + deviceLatency;
            }

            return fallback + deviceLatency;
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

        private bool TryGetTempoStreamPosition(out double position)
        {
            position = 0;
            long playedBytes = _usesSinglePlaybackMixer
                ? BassMix.ChannelGetPosition(_tempoStreamHandle, PositionFlags.Bytes)
                : Bass.ChannelGetPosition(_tempoStreamHandle, PositionFlags.Bytes);
            if (playedBytes < 0)
            {
                return false;
            }

            double seconds = Bass.ChannelBytes2Seconds(_tempoStreamHandle, playedBytes);
            if (seconds < 0)
            {
                return false;
            }

            position = seconds + _positionOffset;
            return true;
        }

        private bool TryGetPlaybackClockTime(out double masterTime)
        {
            if (_usesSinglePlaybackMixer)
            {
                return _bassManager.TryGetGlobalMusicPlaybackMixerTime(out masterTime);
            }

            return TryGetTempoStreamPosition(out masterTime);
        }

        private bool TryGetRenderClockPosition(out double position)
        {
            position = 0;
            if (!_renderClockAnchored)
            {
                return false;
            }

            if (!TryGetPlaybackClockTime(out double masterTime))
            {
                return false;
            }

            double elapsed = masterTime - _renderClockAnchorMasterTime;
            if (elapsed < 0)
            {
                YargLogger.LogFormatWarning(
                    "Master mixer clock moved backwards. Render anchor: {0:0.000000}, now: {1:0.000000}",
                    _renderClockAnchorMasterTime, masterTime);
                return false;
            }

            position = _renderClockAnchorSongPosition + elapsed * _speed;
            return true;
        }

        private bool TryGetAudibleClockPosition(double masterTime, out double position)
        {
            position = 0;
            if (!_audibleClockAnchored)
            {
                return false;
            }

            ProcessPendingAudibleSpeedChanges(masterTime);

            if (!IsPlaying)
            {
                position = _audibleClockAnchorSongPosition;
                return true;
            }

            double elapsed = masterTime - _audibleClockAnchorMasterTime;
            position = _audibleClockAnchorSongPosition + elapsed * _audibleSpeed;
            return true;
        }

        private void ProcessPendingAudibleSpeedChanges(double masterTime)
        {
            while (_pendingAudibleSpeedChanges.Count > 0 &&
                   _pendingAudibleSpeedChanges[0].MasterTime <= masterTime)
            {
                var speedChange = _pendingAudibleSpeedChanges[0];
                _pendingAudibleSpeedChanges.RemoveAt(0);

                double elapsed = Math.Max(0, speedChange.MasterTime - _audibleClockAnchorMasterTime);
                _audibleClockAnchorSongPosition += elapsed * _audibleSpeed;
                _audibleClockAnchorMasterTime = speedChange.MasterTime;
                _audibleSpeed = speedChange.Speed;
            }
        }

        private void ScheduleAudibleSpeedChange(double masterTime, float speed)
        {
            var speedChange = new ScheduledSpeedChange(masterTime, speed);
            int index = _pendingAudibleSpeedChanges.FindIndex(change => masterTime < change.MasterTime);
            if (index < 0)
            {
                _pendingAudibleSpeedChanges.Add(speedChange);
            }
            else
            {
                _pendingAudibleSpeedChanges.Insert(index, speedChange);
            }
        }

        private void ReanchorTransport(double songPosition, bool delayAudible = false, double unpauseDelay = 0)
        {
            if (!TryGetPlaybackClockTime(out double masterTime))
            {
                _renderClockAnchored = false;
                _audibleClockAnchored = false;
                return;
            }

            _pendingAudibleSpeedChanges.Clear();
            _audibleSpeed = _speed;

            _renderClockAnchorMasterTime = masterTime - unpauseDelay;
            _renderClockAnchorSongPosition = songPosition;
            _renderClockAnchored = true;

            double audibleDelay = delayAudible ? EstimateOutputLatency() : 0;
            _audibleClockAnchorMasterTime = masterTime + audibleDelay - unpauseDelay;
            _audibleClockAnchorSongPosition = songPosition;
            _audibleClockAnchored = true;
        }

        private void RollRenderClockAnchorForward()
        {
            if (TryGetRenderClockPosition(out double position) &&
                TryGetPlaybackClockTime(out double masterTime))
            {
                _renderClockAnchorMasterTime = masterTime;
                _renderClockAnchorSongPosition = position;
                _renderClockAnchored = true;
            }
        }

        private void FlushTempoStreamBuffer()
        {
            if (!BassMix.ChannelSetPosition(_tempoStreamHandle, 0, PositionFlags.Bytes))
            {
                Bass.ChannelSetPosition(_tempoStreamHandle, 0);
            }
        }

        private void LogMasterClockComparison(double masterTime, double audiblePosition)
        {
            if (masterTime - _lastMasterClockComparisonLogTime < MASTER_CLOCK_COMPARISON_LOG_INTERVAL_SECONDS)
            {
                return;
            }

            _lastMasterClockComparisonLogTime = masterTime;
            double tempoStreamPosition = GetTempoStreamPosition_Internal();
            double renderPosition = TryGetRenderClockPosition(out double render) ? render : GetDecodingPosition_Internal();
            double estimatedLatency = EstimateOutputLatency();
            YargLogger.LogFormatTrace(
                "Master-clock position comparison. Audible: {0:0.000000}, render: {1:0.000000}, " +
                "tempo-heard: {2:0.000000}, render-audible delta: {3:0.000000}, estimated latency: {4:0.000000}",
                audiblePosition, renderPosition, tempoStreamPosition,
                renderPosition - audiblePosition, estimatedLatency);
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
            _isSeeking = true;
            try
            {
                var wasPlaying = IsPlaying;
                Pause_Internal();

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
                _positionOffset = position;
                ReanchorTransport(position);

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

            bool haveMasterTime = TryGetPlaybackClockTime(out double masterTime);
            double audiblePosition = _positionOffset;
            bool haveAudiblePosition = haveMasterTime &&
                TryGetAudibleClockPosition(masterTime, out audiblePosition);

            RollRenderClockAnchorForward();

            if (IsPlaying && haveMasterTime && haveAudiblePosition)
            {
                double latency = EstimateOutputLatency();
                if (latency <= 0.001)
                {
                    _pendingAudibleSpeedChanges.Clear();
                    _audibleClockAnchorMasterTime = masterTime;
                    _audibleClockAnchorSongPosition = audiblePosition;
                    _audibleSpeed = speed;
                }
                else
                {
                    ScheduleAudibleSpeedChange(masterTime + latency, speed);
                }
            }
            else
            {
                _pendingAudibleSpeedChanges.Clear();
                _audibleSpeed = speed;
                if (haveMasterTime)
                {
                    _audibleClockAnchorMasterTime = masterTime;
                    _audibleClockAnchorSongPosition = haveAudiblePosition ? audiblePosition : _positionOffset;
                    _audibleClockAnchored = true;
                }
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

            bool wasPlaying = IsPlaying;
            double position = _audibleClockAnchored || IsPlaying ? GetPosition_Internal() : _positionOffset;
            RemoveTempoStream();
            AddTempoStream(!wasPlaying);
            ReanchorTransport(position, delayAudible: wasPlaying);
        }

        protected override void SetOutputDevice_Internal(OutputDevice device)
        {
            if (device is not BassOutputDevice bassDevice)
            {
                return;
            }

            if (_usesSinglePlaybackMixer != _bassManager.UsesSinglePlaybackMixer)
            {
                if (IsPlaying)
                {
                    Pause_Internal();
                }
                RemoveTempoStream();
                _isPlaying = false;
                YargLogger.LogWarning("BASS playback mixer mode changed for active stem mixer. Playback stopped; reload mixer to use new topology.");
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

            // Playback buffering belongs to the global music playback mixer. Reinitialize this source's
            // history buffer so large buffer changes are reflected in source-position lookups.
            double position = _audibleClockAnchored || IsPlaying ? GetPosition_Internal() : _positionOffset;

            bool wasPlaying = IsPlaying;
            if (wasPlaying)
            {
                Pause_Internal();
            }

            RemoveTempoStream();
            SetPosition_Internal(position);
            AddTempoStream(true);

            if (wasPlaying)
            {
                Play_Internal();
            }
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
