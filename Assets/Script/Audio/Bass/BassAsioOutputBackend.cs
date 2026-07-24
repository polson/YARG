#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassAsioOutputBackend : IBassOutputBackend
    {
        private const int POSITION_HISTORY_CAPACITY = 1024;
        private const double CLOCK_SMOOTHING_SECONDS = 1.0;

        private readonly int _bufferLength;
        private readonly object _positionLock = new();
        private readonly PositionDiagnostics _positionDiagnostics = new();
        private readonly AsioProcedure _outputCallback;
        private readonly HashSet<int> _songs = new();
        private readonly Dictionary<int, AsioSongPosition> _songPositions = new();
        private readonly HashSet<int> _samples = new();
        private int _masterMixerHandle;
        private int _bytesPerFrame;
        private int _sampleRate;
        private int _latencyFrames;
        private int _callbackFrames;
        private bool _ownsAsio;
        private double _volume = 1;
        private long _submittedFrames;
        private long _lastClockTimestamp;
        private double _clockOffset;
        private double _lastReportedFrame;
        private bool _clockInitialized;

        public int HeardLatencyMilliseconds { get; private set; }
        public bool SongMixerRunsContinuously => true;
        public double PlaybackStartDelay => GetCommandDelay();

        public BassAsioOutputBackend(int bufferLength)
        {
            _bufferLength = bufferLength;
            _outputCallback = FillOutputBuffer;
        }

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
            long position = BassMix.ChannelGetPosition(tempoStreamHandle, PositionFlags.Bytes);
            lock (_positionLock)
            {
                _songPositions.Add(tempoStreamHandle,
                    new AsioSongPosition(position >= 0 ? position : 0));
            }
            return true;
        }

        public void DetachSong(int tempoStreamHandle)
        {
            lock (_positionLock)
            {
                _songPositions.Remove(tempoStreamHandle);
            }
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

        public long GetSongPosition(int tempoStreamHandle)
        {
            long waitStarted = Stopwatch.GetTimestamp();
            bool contended = !Monitor.TryEnter(_positionLock);
            if (contended)
            {
                Monitor.Enter(_positionLock);
            }

            PositionDiagnosticReport? report = null;
            try
            {
                long lockAcquired = Stopwatch.GetTimestamp();
                _positionDiagnostics.RecordLockWait(waitStarted, lockAcquired, contended);

                if (!_songPositions.TryGetValue(tempoStreamHandle, out var position))
                {
                    return -1;
                }

                double heardFrame = GetHeardFrame();
                report = _positionDiagnostics.GetReport(lockAcquired);
                return position.GetPosition(heardFrame);
            }
            finally
            {
                Monitor.Exit(_positionLock);
                if (report.HasValue)
                {
                    report.Value.Log();
                }
            }
        }

        public double GetTempoCommandDelay(int tempoStreamHandle) => GetCommandDelay();

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
            var mixerInfo = Bass.ChannelGetInfo(_masterMixerHandle);
            _sampleRate = mixerInfo.Frequency;
            _bytesPerFrame = mixerInfo.Channels * sizeof(float);
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
            _callbackFrames = _bufferLength > 0
                ? _bufferLength
                : Math.Max(0, BassAsio.Info.PreferredBufferLength);
            if (!BassAsio.ChannelEnable(false, 0, _outputCallback, IntPtr.Zero) ||
                !BassAsio.ChannelJoin(false, 1, 0) ||
                !BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float) ||
                !BassAsio.ChannelSetRate(false, 0, mixerInfo.Frequency) ||
                !BassAsio.Start(_bufferLength, 0))
            {
                YargLogger.LogFormatError("Failed to start ASIO output: {0}", BassAsio.LastError);
                return false;
            }

            int latency = BassAsio.GetLatency(false);
            _latencyFrames = Math.Max(0, latency);
            HeardLatencyMilliseconds = (int) Math.Round(_latencyFrames * 1000.0 / mixerInfo.Frequency);
            return true;
        }

#endif

        private int FillOutputBuffer(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (_bytesPerFrame <= 0)
            {
                return 0;
            }

            long timestamp = Stopwatch.GetTimestamp();
            long frameCount = length / _bytesPerFrame;
            long blockStart;
            lock (_positionLock)
            {
                _callbackFrames = (int) frameCount;
                blockStart = _submittedFrames;
                UpdateOutputClock(blockStart, timestamp);
            }

            int bytesRead = Bass.ChannelGetData(_masterMixerHandle, buffer, length);
            if (bytesRead < 0)
            {
                bytesRead = 0;
            }

            lock (_positionLock)
            {
                _submittedFrames += frameCount;

                foreach (var pair in _songPositions)
                {
                    long position = BassMix.ChannelGetPosition(pair.Key, PositionFlags.Bytes);
                    pair.Value.AddBlock(blockStart, _submittedFrames,
                        position >= 0 ? position : pair.Value.LastGeneratedPosition);
                }
            }

            return bytesRead;
        }

        private void UpdateOutputClock(long blockStartFrame, long timestamp)
        {
            double callbackTime = (double) timestamp / Stopwatch.Frequency;
            double observedOffset = (blockStartFrame / (double) _sampleRate) - callbackTime;

            if (!_clockInitialized)
            {
                _clockOffset = observedOffset;
                _clockInitialized = true;
            }
            else
            {
                // Callback entry time contains scheduling jitter. Smooth its frame-clock offset so
                // position stays stable while still following slow hardware clock drift.
                double elapsed = (double) (timestamp - _lastClockTimestamp) /
                    Stopwatch.Frequency;
                double blend = 1 - Math.Exp(-elapsed / CLOCK_SMOOTHING_SECONDS);
                _clockOffset += blend * (observedOffset - _clockOffset);
            }

            _lastClockTimestamp = timestamp;
        }

        private double GetHeardFrame()
        {
            if (!_clockInitialized || _sampleRate <= 0)
            {
                return 0;
            }

            double currentTime = (double) Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            double heardFrame = ((currentTime + _clockOffset) * _sampleRate) - _latencyFrames;
            _positionDiagnostics.RecordUpperClamp(heardFrame, _submittedFrames, _sampleRate);
            heardFrame = Math.Clamp(heardFrame, _lastReportedFrame, _submittedFrames);
            _lastReportedFrame = heardFrame;
            return heardFrame;
        }

        private double GetCommandDelay()
        {
            lock (_positionLock)
            {
                if (_sampleRate <= 0)
                {
                    return 0;
                }

                // A command waits 0-1 callback periods before BASS generates data with the new
                // state. Use the midpoint, then include frames already queued in the ASIO driver.
                return (_latencyFrames + (_callbackFrames * 0.5)) / _sampleRate;
            }
        }

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
            lock (_positionLock)
            {
                _songPositions.Clear();
            }
            _samples.Clear();
            if (_masterMixerHandle != 0)
            {
                Bass.StreamFree(_masterMixerHandle);
                _masterMixerHandle = 0;
            }
        }

        private sealed class AsioSongPosition
        {
            private readonly PositionBlock[] _history = new PositionBlock[POSITION_HISTORY_CAPACITY];
            private int _historyStart;
            private int _historyCount;

            public long LastGeneratedPosition { get; private set; }

            public AsioSongPosition(long initialPosition)
            {
                LastGeneratedPosition = initialPosition;
            }

            public void AddBlock(long outputStart, long outputEnd, long positionEnd)
            {
                // Song seeks reset the tempo stream to zero. Do not interpolate backward from the
                // previous route across that discontinuity; this output block belongs to the reset stream.
                long positionStart = positionEnd < LastGeneratedPosition ? 0 : LastGeneratedPosition;
                var block = new PositionBlock(outputStart, outputEnd, positionStart, positionEnd);
                LastGeneratedPosition = positionEnd;

                int index = (_historyStart + _historyCount) % _history.Length;
                _history[index] = block;
                if (_historyCount < _history.Length)
                {
                    _historyCount++;
                }
                else
                {
                    _historyStart = (_historyStart + 1) % _history.Length;
                }
            }

            public long GetPosition(double outputFrame)
            {
                if (_historyCount == 0)
                {
                    return LastGeneratedPosition;
                }

                PositionBlock first = _history[_historyStart];
                if (outputFrame <= first.OutputStart)
                {
                    return first.PositionStart;
                }

                int lastIndex = (_historyStart + _historyCount - 1) % _history.Length;
                PositionBlock last = _history[lastIndex];
                if (outputFrame >= last.OutputEnd)
                {
                    return last.PositionEnd;
                }

                // Heard output is normally only a few blocks behind submitted output. Search from
                // newest to oldest so position reads hold the callback lock for minimal time.
                for (int i = _historyCount - 1; i >= 0; i--)
                {
                    PositionBlock block = _history[(_historyStart + i) % _history.Length];
                    if (outputFrame < block.OutputStart)
                    {
                        continue;
                    }

                    double blockLength = block.OutputEnd - block.OutputStart;
                    if (blockLength <= 0)
                    {
                        return block.PositionEnd;
                    }

                    double progress = Math.Clamp(
                        (outputFrame - block.OutputStart) / blockLength, 0, 1);
                    return (long) Math.Round(block.PositionStart +
                        ((block.PositionEnd - block.PositionStart) * progress));
                }

                return first.PositionStart;
            }
        }

        private readonly struct PositionBlock
        {
            public readonly long OutputStart;
            public readonly long OutputEnd;
            public readonly long PositionStart;
            public readonly long PositionEnd;

            public PositionBlock(long outputStart, long outputEnd,
                long positionStart, long positionEnd)
            {
                OutputStart = outputStart;
                OutputEnd = outputEnd;
                PositionStart = positionStart;
                PositionEnd = positionEnd;
            }
        }

        private sealed class PositionDiagnostics
        {
            private const double REPORT_INTERVAL_SECONDS = 1.0;

            private long _reportStarted;
            private long _positionReads;
            private long _contendedReads;
            private long _totalLockWaitTicks;
            private long _maximumLockWaitTicks;
            private long _upperClampReads;
            private long _upperClampPeriods;
            private double _maximumUpperClampMilliseconds;
            private bool _upperClampActive;

            public void RecordLockWait(long waitStarted, long lockAcquired, bool contended)
            {
                _positionReads++;
                if (!contended)
                {
                    return;
                }

                long waitTicks = lockAcquired - waitStarted;
                _contendedReads++;
                _totalLockWaitTicks += waitTicks;
                _maximumLockWaitTicks = Math.Max(_maximumLockWaitTicks, waitTicks);
            }

            public void RecordUpperClamp(double heardFrame, long submittedFrames, int sampleRate)
            {
                if (heardFrame <= submittedFrames)
                {
                    _upperClampActive = false;
                    return;
                }

                _upperClampReads++;
                if (!_upperClampActive)
                {
                    _upperClampPeriods++;
                    _upperClampActive = true;
                }

                double overrunMilliseconds = (heardFrame - submittedFrames) * 1000 / sampleRate;
                _maximumUpperClampMilliseconds = Math.Max(
                    _maximumUpperClampMilliseconds, overrunMilliseconds);
            }

            public PositionDiagnosticReport? GetReport(long timestamp)
            {
                if (_reportStarted == 0)
                {
                    _reportStarted = timestamp;
                    return null;
                }

                double elapsed = (double) (timestamp - _reportStarted) / Stopwatch.Frequency;
                if (elapsed < REPORT_INTERVAL_SECONDS)
                {
                    return null;
                }

                var report = new PositionDiagnosticReport(
                    elapsed,
                    _positionReads,
                    _contendedReads,
                    TicksToMilliseconds(_totalLockWaitTicks),
                    TicksToMilliseconds(_maximumLockWaitTicks),
                    _upperClampReads,
                    _upperClampPeriods,
                    _maximumUpperClampMilliseconds);

                _reportStarted = timestamp;
                _positionReads = 0;
                _contendedReads = 0;
                _totalLockWaitTicks = 0;
                _maximumLockWaitTicks = 0;
                _upperClampReads = 0;
                _upperClampPeriods = 0;
                _maximumUpperClampMilliseconds = 0;
                return report;
            }

            private static double TicksToMilliseconds(long ticks) =>
                ticks * 1000.0 / Stopwatch.Frequency;
        }

        private readonly struct PositionDiagnosticReport
        {
            private readonly double _elapsedSeconds;
            private readonly long _positionReads;
            private readonly long _contendedReads;
            private readonly double _totalLockWaitMilliseconds;
            private readonly double _maximumLockWaitMilliseconds;
            private readonly long _upperClampReads;
            private readonly long _upperClampPeriods;
            private readonly double _maximumUpperClampMilliseconds;

            public PositionDiagnosticReport(double elapsedSeconds, long positionReads,
                long contendedReads, double totalLockWaitMilliseconds,
                double maximumLockWaitMilliseconds, long upperClampReads,
                long upperClampPeriods, double maximumUpperClampMilliseconds)
            {
                _elapsedSeconds = elapsedSeconds;
                _positionReads = positionReads;
                _contendedReads = contendedReads;
                _totalLockWaitMilliseconds = totalLockWaitMilliseconds;
                _maximumLockWaitMilliseconds = maximumLockWaitMilliseconds;
                _upperClampReads = upperClampReads;
                _upperClampPeriods = upperClampPeriods;
                _maximumUpperClampMilliseconds = maximumUpperClampMilliseconds;
            }

            public void Log()
            {
                double averageContendedWait = _contendedReads > 0
                    ? _totalLockWaitMilliseconds / _contendedReads
                    : 0;
                YargLogger.LogFormatDebug(
                    "ASIO position diagnostics ({0:F2}s): reads={1}, lock contention={2}, " +
                    "contended wait avg/max={3:F3}/{4:F3}ms, upper clamp reads/periods={5}/{6}, " +
                    "maximum clock lead={7:F3}ms",
                    _elapsedSeconds, _positionReads, _contendedReads,
                    averageContendedWait, _maximumLockWaitMilliseconds,
                    _upperClampReads, _upperClampPeriods, _maximumUpperClampMilliseconds);
            }
        }
    }
}
