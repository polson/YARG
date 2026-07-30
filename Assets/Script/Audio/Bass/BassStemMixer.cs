using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using UnityEngine;
using YARG.Audio.BASS.Effects;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    public sealed class BassStemMixer : StemMixer
    {
        private const    float WHAMMY_SYNC_INTERVAL_SECONDS = 1f;
        private const    float MIN_PLAYBACK_SPEED           = 0.05f;
        private const    float MAX_PLAYBACK_SPEED           = 51f;
        private readonly int   _mixerHandle;

        private readonly BassNormalizer           _normalizer;
        private readonly BassSongPlayback         _playback;
        private readonly BufferedPlaybackTimeline _playbackTimeline;
        private readonly SongPositionTracker      _songPositionTracker;
        private readonly List<int>                _sourceHandles = new();
        private readonly List<StemData>           _stemDatas     = new();
        private readonly int                      _tempoStreamHandle;
        private          bool                     _didSeek;
        private          BassGainDsp              _gainDsp;
        private          int                      _longestHandle;
        private          bool                     _shouldNormalize;
        private          int                      _songEndHandle;
        private          float                    _songSpeed = 1.0f;
        private          float                    _speed     = 1.0f;
        private          Timer                    _whammySyncTimer;

#nullable enable
        private BassStemMixer(string name, BassAudioManager manager, float speed, double volume, int mixerHandle,
            int tempoStreamHandle, BassSongPlayback playback, bool clampStemVolume, bool normalize,
            OutputChannel? outputChannel) : base(name, manager, clampStemVolume)
#nullable disable
        {
            _normalizer = new BassNormalizer(gain => _gainDsp?.SetGain(gain));
            _mixerHandle = mixerHandle;
            _tempoStreamHandle = tempoStreamHandle;
            _playback = playback;

            _songPositionTracker = new SongPositionTracker(_tempoStreamHandle, _playback);
            _playbackTimeline = new BufferedPlaybackTimeline(speed);
            _playback.OutputChanged += OnOutputChanged;
            _shouldNormalize = normalize && SettingsManager.Settings.EnableNormalization.Value;

            if (_shouldNormalize)
            {
                AddGainDSP();
            }

            _whammySyncTimer = new Timer();
            SetOutputChannel_Internal(outputChannel);
            SetVolume_Internal(volume);
            SetPlaybackSpeed_Internal(speed, 0f, true);
            _playback.SetBufferLength(SettingsManager.Settings.PlaybackBufferLength.Value);
        }

        private static bool IsWhammyEnabled => SettingsManager.Settings.UseWhammyFx.Value;

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
            remove => _songEnd -= value;
        }

#nullable enable
        internal static BassStemMixer? Create(string name, BassAudioManager manager, float speed, double volume,
            int mixerHandle, bool clampStemVolume, bool normalize, OutputChannel? outputChannel)
        {
            int tempoStreamHandle;
            try
            {
                tempoStreamHandle = BassX.Fx.CreateTempo(mixerHandle, BassFlags.Decode | BassFlags.FxFreeSource);
            }
            catch (BassX.BassOperationException exception)
            {
                YargLogger.LogError(exception.Message);
                BassX.Stream.Free(mixerHandle, "mixer stream");
                return null;
            }

            var playback = manager.CreateSongPlayback(tempoStreamHandle);
            if (!playback.IsValid)
            {
                playback.Dispose();
                // Tempo stream owns and frees its source mixer via BassFlags.FxFreeSource.
                BassX.Stream.Free(tempoStreamHandle, "tempo stream");
                return null;
            }

            return new BassStemMixer(name, manager, speed, volume, mixerHandle, tempoStreamHandle, playback,
                clampStemVolume, normalize, outputChannel);
        }
#nullable disable

        private void AddGainDSP()
        {
            _gainDsp = BassGainDsp.Create(_mixerHandle, 1f);
        }

        protected override int Play_Internal()
        {
            if (_shouldNormalize)
            {
                _gainDsp?.SetGain(_normalizer.Gain);
            }

            if (!_playback.IsPlaying)
            {
                int result = _playback.Play(_didSeek);
                if (result != 0)
                {
                    return result;
                }

                // Start control-rate tracking after ChannelPlay returns so mixer startup work is not
                // counted as song progress.
                _playbackTimeline.Play(_songPositionTracker.GetSongPosition(), _playback.GetPlaybackStartDelay());
                _didSeek = false;
            }

            if (IsWhammyEnabled)
            {
                _whammySyncTimer.Start(WHAMMY_SYNC_INTERVAL_SECONDS, SyncWhammyDrift);
            }

            return 0;
        }

        /// <summary>
        ///     .
        ///     The BASS PitchShift effect causes the stem playback to drift over time.
        ///     It was discovered that we can correct the drift by setting the whammy pitch
        ///     to 0% when no pitch shift is applied.
        /// </summary>
        private void SyncWhammyDrift()
        {
            foreach (var channel in Channels)
            {
                if (Mathf.Approximately(channel.GetWhammyPitch(), 1.0f))
                {
                    channel.SetWhammyPitch(0.0f);
                }
            }
        }

        protected override void FadeIn_Internal(double maxVolume, double duration)
        {
            _playback.FadeIn(maxVolume, duration);
        }

        protected override void FadeOut_Internal(double duration)
        {
            _playback.FadeOut(duration);
        }

        protected override int Pause_Internal()
        {
            if (!_playback.IsPlaying)
            {
                _playbackTimeline.Pause();
                return 0;
            }

            int result = _playback.Pause();
            if (result != 0)
            {
                return result;
            }

            _playbackTimeline.Pause();

            return 0;
        }

        protected override double GetPosition_Internal() => _songPositionTracker.GetSongPosition();

        /// <summary>
        ///     Samples BASS playback once, then derives heard and predictive control positions from that
        ///     same sample. BASSmix already compensates this position for mixer playback buffering.
        /// </summary>
        protected override SyncPosition GetSyncPosition_Internal()
        {
            double bassPosition = _songPositionTracker.GetSongPosition();
            return _playbackTimeline.GetSyncPosition(bassPosition);
        }

        protected override double GetControlPosition_Internal() => GetSyncPosition_Internal().Control;

        protected override double GetTempoStreamLatency_Internal() => _playback.GetLatency();

        // The total delay between playback command and when audio is heard
        public double GetPlaybackStartOffset() => _playbackTimeline.OutputLatency + _songPositionTracker.AlignmentDelay;

        protected override double GetVolume_Internal() => _playback.GetVolume();

        protected override void SetPosition_Internal(double position)
        {
            bool wasPlaying = _playback.IsPlaying;
            Pause_Internal();

            double playbackOffset = GetPlaybackStartOffset() * _songSpeed;
            double preparedPosition = position + playbackOffset;
            double seekPosition = Math.Clamp(preparedPosition, 0, _length);
            double playbackDelay = Math.Max(0, -preparedPosition);

            RemoveChannelsFromMixer();
            bool channelsAdded = AddChannelsToMixer(_stemDatas, playbackDelay, out double alignmentDelay);

            if (channelsAdded)
            {
                foreach (var channel in _channels)
                {
                    channel.SetPosition(seekPosition);
                }

                _didSeek = true;
                _songPositionTracker.Reset(seekPosition, alignmentDelay, playbackDelay);
                BassX.Mix.SetPosition(_tempoStreamHandle, 0, PositionFlags.Bytes);

                _playback.ResetAfterSeek();
                _playbackTimeline.ResetAfterSeek(_songPositionTracker.GetSongPosition(), position);
            }

            if (wasPlaying)
            {
                Play_Internal();
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            _playback.SetVolume(volume);
        }

        protected override int GetFFTData_Internal(float[] buffer, int fftSize, bool complex) =>
            _playback.GetFFTData(buffer, fftSize, complex);

        protected override int GetSampleData_Internal(float[] buffer) => _playback.GetSampleData(buffer);

        protected override int GetLevel_Internal(float[] level) => _playback.GetLevel(level);

        protected override void SetPlaybackSpeed_Internal(float songSpeed, float syncAdjustment, bool shiftPitch)
        {
            // SongRunner clamps requested song speed, but the temporary synchronization adjustment can
            // push the effective speed outside BASS_FX's supported 5%-5100% tempo range.
            float effectiveSpeed = Math.Clamp(songSpeed + syncAdjustment, MIN_PLAYBACK_SPEED, MAX_PLAYBACK_SPEED);

            // Model the speed BASS actually receives. This can differ from the requested adjustment
            // when the effective speed reaches one of the limits above.
            float appliedAdjustment = effectiveSpeed - songSpeed;
            _songSpeed = songSpeed;

            // Exact comparison is intentional. If BASS receives a new float value, the playback model
            // must record the same value; an approximate comparison could let the two drift apart.
            bool speedChanged = _speed != effectiveSpeed;
            if (speedChanged)
            {
                _speed = effectiveSpeed;
                BassAudioManager.SetSpeed(effectiveSpeed, _tempoStreamHandle, shiftPitch);
            }

            double tempoLatency = _playback.GetLatency();
            _playbackTimeline.SetSpeed(songSpeed, appliedAdjustment, tempoLatency);
            if (!speedChanged)
            {
                return;
            }

            _playback.ResetAfterSpeedChange();
        }

        protected override void SetOutputLatency_Internal(double latency)
        {
            _playbackTimeline.SetOutputLatency(latency);
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

            if (!BuildStemData(sourceStream, stemInfos, out var stemDatas))
            {
                return false;
            }

            _stemDatas.AddRange(stemDatas);

            // Every stem is padded to match the largest pitch-effect delay in the mixer. A new stem can
            // increase that delay, so rebuild all mixer channels to keep every stem aligned. Rebuilding
            // also prevents the existing streams from being added a second time below.
            RemoveChannelsFromMixer();
            if (!AddChannelsToMixer(_stemDatas, 0, out double delay))
            {
                _stemDatas.RemoveAll(stemDatas.Contains);
                return false;
            }

            _songPositionTracker.SetAlignmentDelay(delay);

            foreach (var stemStreamData in stemDatas)
            {
                CreateChannel(stemStreamData.Stem, sourceStream, stemStreamData.StreamHandles,
                    stemStreamData.ReverbHandles);
            }

            return true;
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _playback.SetOutputChannel(channel);
        }

        protected override void SetOutputDevice_Internal(OutputDevice device)
        {
            if (device is not BassOutputDevice bassDevice)
            {
                return;
            }

            // Normalization analysis streams belong to the current BASS device and are read by a
            // background worker. Stop and free them before the old device can be released. The
            // current gain remains applied; a new mixer will create a new normalizer.
            StopNormalization();

            foreach (var stemData in _stemDatas)
            {
                BassX.Channel.SetDevice(stemData.ReverbHandles.Stream, bassDevice.DeviceId);
                BassX.Channel.SetDevice(stemData.StreamHandles.Stream, bassDevice.DeviceId);
            }

            foreach (int handle in _sourceHandles)
            {
                BassX.Channel.SetDevice(handle, bassDevice.DeviceId);
            }

            if (_mixerHandle != 0)
            {
                BassX.Channel.SetDevice(_mixerHandle, bassDevice.DeviceId);
            }

            if (_tempoStreamHandle != 0)
            {
                BassX.Channel.SetDevice(_tempoStreamHandle, bassDevice.DeviceId);
            }
        }

        private void StopNormalization()
        {
            _shouldNormalize = false;
            _normalizer.Dispose();
        }

        private void OnOutputChanged()
        {
            _playbackTimeline.ResetAfterOutputChange(_songPositionTracker.GetSongPosition(),
                _playback.GetPlaybackStartDelay());
        }

        private void RemoveChannelsFromMixer()
        {
            foreach (int channel in BassMix.MixerGetChannels(_mixerHandle))
            {
                BassX.Mix.RemoveChannel(channel);
            }

            _playback.PrepareForSeek();
        }

        private static bool BuildStemData(int sourceStream, IEnumerable<StemInfo> stemInfos,
            out List<StemData> stemDatas)
        {
            stemDatas = new List<StemData>();

            foreach (var group in stemInfos.GroupBy(info => info.Stem))
            {
                var stem = group.Key;
                int[] allIndices = group.Where(info => info.Indices != null).SelectMany(info => info.Indices).ToArray();

                var handles = BassAudioManager.CreateSplitStreams(sourceStream, allIndices);
                if (handles == null)
                {
                    YargLogger.LogFormatError("Failed to load stem {0}: {1}!", stem, Bass.LastError);
                    continue;
                }

                var (streamHandle, reverbHandle) = handles.Value;
                double pitchFxDelay = 0;
                if (GlobalAudioHandler.UseWhammyFx && AudioHelpers.PitchBendAllowedStems.Contains(stem))
                {
                    if (!BassX.Channel.GetAttribute(streamHandle.Stream, ChannelAttribute.Frequency,
                        out float frequency))
                    {
                        return false;
                    }

                    // BASS_FX pitch shift buffers one full FFT frame. Use source stream frequency:
                    // low-rate stems otherwise receive only half required compensation and drift
                    // ahead of stems without pitch FX.
                    pitchFxDelay = GlobalAudioHandler.WHAMMY_FFT_DEFAULT / frequency;
                }

                float[,] volumeMatrix = BuildVolumeMatrix(group, allIndices.Length);
                stemDatas.Add(new StemData(stem, volumeMatrix, streamHandle, reverbHandle, pitchFxDelay));
            }

            if (stemDatas.Count > 0)
            {
                return true;
            }

            YargLogger.LogError("Failed to load any stems!");
            return false;
        }

        private bool AddChannelsToMixer(IEnumerable<StemData> stemStreamDataList, double playbackDelay,
            out double alignmentDelay)
        {
            var stemData = stemStreamDataList.ToArray();
            var addedChannels = new List<int>(stemData.Length * 2);

            // Align every stem with the largest pitch fx latency.  Latencies per stem can differ due to sample rate
            double requiredAlignmentDelay = stemData.Max(data => data.PitchFxDelay);
            alignmentDelay = requiredAlignmentDelay;

            try
            {
                foreach (var data in stemData)
                {
                    var streamHandles = data.StreamHandles;
                    var reverbHandles = data.ReverbHandles;
                    float[,] volumeMatrix = data.VolumeMatrix;

                    // Each stem already incurs its own processing delay. Add the difference from the maximum so every
                    // stem has the same total delay.
                    double addedDelay = playbackDelay + requiredAlignmentDelay - data.PitchFxDelay;
                    long delayBytes = Bass.ChannelSeconds2Bytes(_mixerHandle, addedDelay);

                    var flags = volumeMatrix != null ? BassFlags.MixerChanMatrix : BassFlags.Default;
                    BassX.Mix.AddChannel(_mixerHandle, streamHandles.Stream, flags, delayBytes, 0);
                    addedChannels.Add(streamHandles.Stream);
                    BassX.Mix.AddChannel(_mixerHandle, reverbHandles.Stream, flags, delayBytes, 0);
                    addedChannels.Add(reverbHandles.Stream);

                    if (volumeMatrix == null)
                    {
                        continue;
                    }

                    BassX.Mix.SetMatrix(streamHandles.Stream, volumeMatrix);
                    BassX.Mix.SetMatrix(reverbHandles.Stream, volumeMatrix);
                }

                return true;
            }
            catch (BassX.BassOperationException exception)
            {
                YargLogger.LogError(exception.Message);
                foreach (int channel in addedChannels)
                {
                    BassX.Mix.RemoveChannel(channel);
                }

                return false;
            }
        }

        internal static float[,] BuildVolumeMatrix(StemInfo info)
        {
            if (info.Indices == null || info.Panning == null)
            {
                return null;
            }

            return BuildVolumeMatrix(new[]
            {
                info,
            }, info.Indices.Length);
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
                float[] panning = info.Panning;
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
            _playback.SetBufferLength(length);
        }

        protected override void DisposeManagedResources()
        {
            _whammySyncTimer.Stop();
            _whammySyncTimer = null;
            _stemDatas.Clear();

            StopNormalization();

            _gainDsp?.Dispose();
            _gainDsp = null;

            foreach (var channel in Channels)
            {
                channel.Dispose();
            }

            foreach (int sourceHandle in _sourceHandles)
            {
                BassX.Stream.Free(sourceHandle, "source stream (THIS WILL LEAK MEMORY!)");
            }

            _sourceHandles.Clear();
        }

        protected override void DisposeUnmanagedResources()
        {
            if (_playback != null)
            {
                _playback.OutputChanged -= OnOutputChanged;
            }

            _playback?.Dispose();

            // Tempo stream owns and frees its source mixer via BassFlags.FxFreeSource.
            if (_tempoStreamHandle != 0)
            {
                BassX.Stream.Free(_tempoStreamHandle, "tempo stream");
            }
        }

        private void CreateChannel(SongStem stem, int sourceHandle, StreamHandle streamHandles,
            StreamHandle reverbHandles)
        {
            var pitchparams = BassAudioManager.SetPitchParams(stem, _speed, streamHandles, reverbHandles);
            var stemchannel = new BassStemChannel(_manager, stem, _clampStemVolume, sourceHandle, pitchparams,
                streamHandles, reverbHandles);
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
                BassX.Mix.SetProcessingThreads(_mixerHandle, _channels.Count);
            }
        }

        public override OneShotChannel CreateOneShotChannel(int sampleStream, IReadOnlyList<double> scheduledPlays,
            double outputLeadTime = 0)
        {
            return _playback.CreateOneShotChannel(sampleStream, scheduledPlays, _songPositionTracker.GetSongPosition,
                () => _speed, outputLeadTime);
        }
#nullable enable
        private struct StemData
        {
            public readonly SongStem     Stem;
            public readonly float[,]?    VolumeMatrix;
            public readonly StreamHandle StreamHandles;
            public readonly StreamHandle ReverbHandles;
            public readonly double       PitchFxDelay;

            public StemData(SongStem stem, float[,]? volumeMatrix, StreamHandle streamHandles,
                StreamHandle reverbHandles, double pitchFxDelay)
            {
                Stem = stem;
                VolumeMatrix = volumeMatrix;
                StreamHandles = streamHandles;
                ReverbHandles = reverbHandles;
                PitchFxDelay = pitchFxDelay;
            }
        }
#nullable disable

        /// <summary>
        ///     Gets actual song position from tempo stream.
        ///     <para>
        ///         Calculated as: tempo stream position + last seek position - alignment delay.
        ///     </para>
        ///     <para>
        ///         Tempo stream position advances continuously during playback and resets to zero after each seek.
        ///         Last seek position is the position of the most recent seek in the song. Alignment delay is applied
        ///         to all stems to keep them synchronized when using whammy FX and varies based on sample rate.
        ///     </para>
        /// </summary>
        private sealed class SongPositionTracker
        {
            private readonly BassSongPlayback _playback;
            private readonly int              _tempoStreamHandle;
            private          double           _playbackDelay;
            private          long             _positionBeforeSeek;
            private          bool             _seekPending;
            private          double           _songStart;

            public SongPositionTracker(int tempoStreamHandle, BassSongPlayback playback)
            {
                _tempoStreamHandle = tempoStreamHandle;
                _playback = playback;
            }

            public double AlignmentDelay { get; private set; }

            private double TotalDelay => AlignmentDelay + _playbackDelay;

            /// <summary>
            ///     Gets the current position in the song, in seconds.
            /// </summary>
            public double GetSongPosition()
            {
                double position = GetTempoStreamPosition();
                if (position < 0)
                {
                    return 0;
                }

                return position - TotalDelay + _songStart;
            }

            public double GetSongPosition(long tempoStreamPosition)
            {
                // Explicit positions come from the decode timeline. They already belong to the
                // newly prepared route and must not consume the pending heard-position boundary.
                double position = GetPositionSeconds(tempoStreamPosition);
                return position - TotalDelay + _songStart;
            }

            /// <summary>
            ///     Starts tracking from the requested song position after a seek
            /// </summary>
            public void Reset(double songStart, double alignmentDelay, double playbackDelay)
            {
                _positionBeforeSeek = _playback.GetPosition();
                if (_positionBeforeSeek < 0)
                {
                    YargLogger.LogFormatError("Failed to capture position before seek: {0}!", Bass.LastError);
                }

                _seekPending = _positionBeforeSeek > 0;

                _songStart = songStart;
                AlignmentDelay = alignmentDelay;
                _playbackDelay = playbackDelay;
            }

            public void SetAlignmentDelay(double delay)
            {
                AlignmentDelay = delay;
            }

            private double GetTempoStreamPosition()
            {
                long positionBytes = _playback.GetPosition();
                if (positionBytes < 0)
                {
                    YargLogger.LogFormatError("Failed to get byte position: {0}!", Bass.LastError);
                    return -1;
                }

                return GetTempoStreamPosition(positionBytes);
            }

            private double GetTempoStreamPosition(long positionBytes)
            {
                if (_seekPending)
                {
                    if (positionBytes >= _positionBeforeSeek)
                    {
                        // BASSmix reports position currently heard. Immediately after resetting a source
                        // inside a playing mixer, this can still be its pre-seek position for one output
                        // buffer. Hold prepared position until reported position crosses seek boundary.
                        return 0;
                    }

                    _seekPending = false;
                }

                return GetPositionSeconds(positionBytes);
            }

            private double GetPositionSeconds(long positionBytes)
            {
                double position = Bass.ChannelBytes2Seconds(_tempoStreamHandle, positionBytes);
                if (position < 0)
                {
                    YargLogger.LogFormatError("Failed to convert bytes to seconds: {0}!", Bass.LastError);
                    return -1;
                }

                return position;
            }
        }
    }
}