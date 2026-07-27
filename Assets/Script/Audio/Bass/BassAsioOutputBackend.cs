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
    /// <summary>
    /// Routes worker-rendered song audio and direct live audio into ASIO output.
    /// </summary>
    internal sealed class BassAsioOutputBackend : IBassOutputBackend
    {
        private const int OUTPUT_CHANNELS = 2;
        private const int BYTES_PER_FRAME = OUTPUT_CHANNELS * sizeof(float);
        // Native pull keeps Unity GC and managed thread suspension out of the hardware callback.
        // Set false only to collect managed-callback diagnostics for an A/B test.
        private const bool USE_NATIVE_ASIO_OUTPUT = true;

        private readonly int _bufferLength;
        private readonly Action _asioReinitializeRequested;
        private readonly HashSet<int> _songs = new();
        private readonly HashSet<int> _samples = new();
        private readonly HashSet<int> _monitors = new();
        private readonly AsioProcedure _outputCallback;
        private readonly AsioNotifyProcedure _notifyCallback;

        private BassRenderAheadStream? _renderAheadStream;
        private int _songMixerHandle;
        private int _outputMixerHandle;
        private int _bassDeviceId;
        private int _sampleRate;
        private int _latencyFrames;
        private bool _ownsAsio;
        private bool _asioStarted;
        private bool _notifyRegistered;
        private bool _disposed;
        private int _notificationQueued;
        private double _volume = 1;
        private bool _usesManagedCallback;
        private int _callbackFrames;
        private int _lastCallbackFrames;
        private long _lastCallbackTimestamp;
        private long _maximumCallbackTicks;
        private long _maximumCallbackGapTicks;
        private long _maximumCallbackLatenessTicks;
        private long _lateCallbacks;
        private long _outputUnderfills;

        public int HeardLatencyMilliseconds => (int) Math.Round(
            FramesToMilliseconds(_latencyFrames + QueuedFrames));

        public AudioOutputMetrics Metrics
        {
            get
            {
                BassRenderAheadStream? stream = _renderAheadStream;
                return new AudioOutputMetrics(
                    usesManagedCallback: _usesManagedCallback,
                    callbackPeriodMilliseconds:
                        FramesToMilliseconds(Volatile.Read(ref _callbackFrames)),
                    maximumCallbackTimeMilliseconds:
                        TicksToMilliseconds(Volatile.Read(ref _maximumCallbackTicks)),
                    maximumCallbackGapMilliseconds:
                        TicksToMilliseconds(Volatile.Read(ref _maximumCallbackGapTicks)),
                    maximumCallbackLatenessMilliseconds:
                        TicksToMilliseconds(Volatile.Read(ref _maximumCallbackLatenessTicks)),
                    lateCallbackCount: Volatile.Read(ref _lateCallbacks),
                    outputUnderfillCount: Volatile.Read(ref _outputUnderfills),
                    renderAheadMilliseconds: FramesToMilliseconds(stream?.QueuedFrames ?? 0),
                    minimumRenderAheadMilliseconds:
                        FramesToMilliseconds(stream?.MinimumQueuedFrames ?? 0),
                    maximumRenderTimeMilliseconds: stream?.MaximumRenderTimeMilliseconds ?? 0,
                    renderUnderrunCount: stream?.UnderrunCount ?? 0);
            }
        }

        public bool SongMixerRunsContinuously => true;
        public double PlaybackStartDelay => GetCommandDelay();

        private int QueuedFrames => _renderAheadStream?.QueuedFrames ?? 0;

        public BassAsioOutputBackend(int bufferLength, Action asioReinitializeRequested)
        {
            _bufferLength = bufferLength;
            _asioReinitializeRequested = asioReinitializeRequested;
            _outputCallback = FillOutputBuffer;
            _notifyCallback = OnAsioNotification;
        }

        public bool Initialize(BassOutputDevice device)
        {
            _bassDeviceId = device.DeviceId;
            if (!CreateOutputMixers())
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
            if (!BassMix.MixerAddChannel(_songMixerHandle, tempoStreamHandle, flags))
            {
                YargLogger.LogFormatError("Failed to add tempo stream to ASIO song mixer: {0}",
                    Bass.LastError);
                return false;
            }

            _songs.Add(tempoStreamHandle);
            return true;
        }

        public void DetachSong(int tempoStreamHandle)
        {
            if (_songs.Remove(tempoStreamHandle) &&
                !BassMix.MixerRemoveChannel(tempoStreamHandle) && Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to remove tempo stream from ASIO song mixer: {0}",
                    Bass.LastError);
            }
        }

        public int SongMixerHandle(int tempoStreamHandle) => _songMixerHandle;

        public bool IsSongPlaying(int tempoStreamHandle)
        {
            bool active = Bass.ChannelIsActive(tempoStreamHandle) is
                PlaybackState.Playing or PlaybackState.Stalled;
            return active && !BassMix.ChannelHasFlag(tempoStreamHandle, BassFlags.MixerChanPause);
        }

        public int PlaySong(int tempoStreamHandle, bool restart)
        {
            return BassMix.ChannelFlags(tempoStreamHandle, BassFlags.Default,
                BassFlags.MixerChanPause) < 0
                ? (int) Bass.LastError
                : 0;
        }

        public int PauseSong(int tempoStreamHandle)
        {
            return BassMix.ChannelFlags(tempoStreamHandle, BassFlags.MixerChanPause,
                BassFlags.MixerChanPause) < 0 ? (int) Bass.LastError : 0;
        }

        public void ResetSongAfterSeek(int tempoStreamHandle) => _renderAheadStream?.Flush();

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
            if (_renderAheadStream != null)
            {
                return _renderAheadStream.GetSourcePosition(tempoStreamHandle, _latencyFrames);
            }

            return BassMix.ChannelGetPosition(tempoStreamHandle, PositionFlags.Bytes,
                FramesToBytes(_latencyFrames));
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

        public bool AttachMonitor(int sourceHandle, double volume)
        {
            if (_monitors.Contains(sourceHandle))
            {
                return SetMonitorVolume(sourceHandle, volume);
            }
            if (!SetMonitorVolume(sourceHandle, volume))
            {
                return false;
            }

            var flags = BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin;
            if (!BassMix.MixerAddChannel(_outputMixerHandle, sourceHandle, flags))
            {
                YargLogger.LogFormatError("Failed to add source to ASIO output mixer: {0}",
                    Bass.LastError);
                return false;
            }

            _monitors.Add(sourceHandle);
            return true;
        }

        public void DetachMonitor(int sourceHandle)
        {
            if (!_monitors.Remove(sourceHandle))
            {
                return;
            }
            if (!BassMix.MixerRemoveChannel(sourceHandle) && Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to remove source from ASIO output mixer: {0}",
                    Bass.LastError);
            }
        }

        public bool SetMonitorVolume(int sourceHandle, double volume)
        {
            if (Bass.ChannelSetAttribute(sourceHandle, ChannelAttribute.Volume, volume))
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to set monitor source volume: {0}", Bass.LastError);
            return false;
        }

        public bool PlaySample(int sourceHandle, OutputChannel? outputChannel)
        {
            var flags = BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin;
            if (outputChannel is BassOutputChannel bassOutputChannel)
            {
                flags |= bassOutputChannel.Flags;
            }
            if (!BassMix.MixerAddChannel(_outputMixerHandle, sourceHandle, flags) &&
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
            if (_outputMixerHandle != 0 &&
                !Bass.ChannelSetAttribute(_outputMixerHandle, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set ASIO output volume: {0}", Bass.LastError);
            }
        }

        private bool CreateOutputMixers()
        {
            var info = Bass.Info;
            int frequency = info.SampleRate > 0 ? info.SampleRate : 44100;

            // Song mixer -> render-ahead push stream -> output mixer -> ASIO.
            // Monitors and samples join output mixer directly, avoiding render-ahead latency.
            _songMixerHandle = BassMix.CreateMixerStream(frequency, OUTPUT_CHANNELS,
                BassFlags.Float | BassFlags.MixerNonStop | BassFlags.Decode |
                BassFlags.MixerPositionEx);
            if (_songMixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create ASIO song mixer: {0}", Bass.LastError);
                return false;
            }

            _outputMixerHandle = BassMix.CreateMixerStream(frequency, OUTPUT_CHANNELS,
                BassFlags.Float | BassFlags.MixerNonStop | BassFlags.Decode);
            if (_outputMixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create ASIO output mixer: {0}", Bass.LastError);
                return false;
            }

            Bass.ChannelSetAttribute(_outputMixerHandle, ChannelAttribute.Volume, _volume);
            _sampleRate = Bass.ChannelGetInfo(_songMixerHandle).Frequency;
            return true;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private bool StartAsio(int deviceId)
        {
            if (!BassAsio.Init(deviceId, AsioInitFlags.Thread))
            {
                YargLogger.LogFormatError("Failed to initialize ASIO device: {0}",
                    BassAsio.LastError);
                return false;
            }
            _ownsAsio = true;

            if (!BassAsio.CheckRate(_sampleRate))
            {
                YargLogger.LogFormatError("ASIO device does not support {0}Hz: {1}",
                    _sampleRate, BassAsio.LastError);
                return false;
            }
            BassAsio.Rate = _sampleRate;
            _callbackFrames = _bufferLength > 0
                ? _bufferLength
                : Math.Max(1, BassAsio.Info.PreferredBufferLength);

            _renderAheadStream = BassRenderAheadStream.Create(
                _songMixerHandle, _bassDeviceId, _sampleRate, OUTPUT_CHANNELS, _callbackFrames,
                outputRequestsReported: !USE_NATIVE_ASIO_OUTPUT);
            if (_renderAheadStream == null)
            {
                return false;
            }
            if (!BassMix.MixerAddChannel(_outputMixerHandle, _renderAheadStream.Handle,
                    BassFlags.MixerChanNoRampin))
            {
                YargLogger.LogFormatError("Failed to attach ASIO render-ahead stream: {0}",
                    Bass.LastError);
                return false;
            }

            if (USE_NATIVE_ASIO_OUTPUT)
            {
                if (!BassAsio.ChannelEnableBass(false, 0, _outputMixerHandle, Join: true))
                {
                    YargLogger.LogFormatError("Failed to configure native ASIO output: {0}",
                        BassAsio.LastError);
                    return false;
                }
                YargLogger.LogInfo("ASIO output transport: native BASS channel");
            }
            else
            {
                if (!BassAsio.ChannelEnable(false, 0, _outputCallback, IntPtr.Zero) ||
                    !BassAsio.ChannelJoin(false, 1, 0) ||
                    !BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float) ||
                    !BassAsio.ChannelSetRate(false, 0, _sampleRate))
                {
                    YargLogger.LogFormatError("Failed to configure managed ASIO output: {0}",
                        BassAsio.LastError);
                    return false;
                }
                _usesManagedCallback = true;
                YargLogger.LogInfo("ASIO output transport: managed callback");
            }

            if (!BassAsio.Start(_bufferLength, 0))
            {
                YargLogger.LogFormatError("Failed to start ASIO output: {0}", BassAsio.LastError);
                return false;
            }
            _asioStarted = true;

            if (BassAsio.SetNotify(_notifyCallback, IntPtr.Zero))
            {
                _notifyRegistered = true;
            }
            else
            {
                YargLogger.LogFormatWarning(
                    "Failed to register for ASIO driver notifications: {0}", BassAsio.LastError);
            }

            _latencyFrames = Math.Max(0, BassAsio.GetLatency(false));
            float latencySeconds = (_latencyFrames + QueuedFrames) / (float) _sampleRate;
            if (!Bass.ChannelSetAttribute(_songMixerHandle, ChannelAttribute.MixerLatency,
                    latencySeconds))
            {
                YargLogger.LogFormatError("Failed to set ASIO mixer latency: {0}", Bass.LastError);
                return false;
            }
            return true;
        }

        private void OnAsioNotification(AsioNotify notification, IntPtr user)
        {
            if ((notification != AsioNotify.Reset && notification != AsioNotify.Rate) ||
                Volatile.Read(ref _disposed) ||
                Interlocked.Exchange(ref _notificationQueued, 1) != 0)
            {
                return;
            }

            // Driver callbacks may not reinitialize ASIO. Defer and coalesce requests.
            UnityMainThreadCallback.QueueEvent(HandleAsioNotification);
        }

        private void HandleAsioNotification()
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
            if (_disposed)
            {
                return;
            }

            YargLogger.LogInfo("ASIO driver settings changed; reinitializing audio output");
            _asioReinitializeRequested();
        }

        private int FillOutputBuffer(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                int callbackFrames = length / BYTES_PER_FRAME;
                RecordCallbackEntry(start, callbackFrames);
                _renderAheadStream?.OnOutputRequested(callbackFrames, start);

                int bytesRead = Bass.ChannelGetData(_outputMixerHandle, buffer, length);
                if (bytesRead != length)
                {
                    Interlocked.Increment(ref _outputUnderfills);
                }
                return bytesRead >= 0 ? bytesRead : 0;
            }
            finally
            {
                UpdateMaximum(ref _maximumCallbackTicks, Stopwatch.GetTimestamp() - start);
            }
        }

        private void RecordCallbackEntry(long timestamp, int callbackFrames)
        {
            int previousFrames = Interlocked.Exchange(ref _lastCallbackFrames, callbackFrames);
            long previousTimestamp = Interlocked.Exchange(ref _lastCallbackTimestamp, timestamp);
            Volatile.Write(ref _callbackFrames, callbackFrames);
            if (previousTimestamp == 0 || previousFrames <= 0 || _sampleRate <= 0)
            {
                return;
            }

            long gapTicks = timestamp - previousTimestamp;
            UpdateMaximum(ref _maximumCallbackGapTicks, gapTicks);

            long expectedTicks = previousFrames * (long) Stopwatch.Frequency / _sampleRate;
            long latenessTicks = gapTicks - expectedTicks;
            if (latenessTicks <= 0)
            {
                return;
            }

            UpdateMaximum(ref _maximumCallbackLatenessTicks, latenessTicks);

            // Ignore normal timer/driver jitter. At least 25% of one buffer period or 0.1ms late
            // is suspicious at low buffer sizes, but remains diagnostic rather than proof of xrun.
            long toleranceTicks = Math.Max(expectedTicks / 4, Stopwatch.Frequency / 10000);
            if (latenessTicks > toleranceTicks)
            {
                Interlocked.Increment(ref _lateCallbacks);
            }
        }
#endif

        public void ResetMetrics()
        {
            Interlocked.Exchange(ref _maximumCallbackTicks, 0);
            Interlocked.Exchange(ref _maximumCallbackGapTicks, 0);
            Interlocked.Exchange(ref _maximumCallbackLatenessTicks, 0);
            Interlocked.Exchange(ref _lateCallbacks, 0);
            Interlocked.Exchange(ref _outputUnderfills, 0);
            _renderAheadStream?.ResetMetrics();
        }

        private double GetCommandDelay()
        {
            if (_sampleRate <= 0)
            {
                return 0;
            }

            int queuedFrames = _renderAheadStream?.SnapshotQueuedFrames() ?? 0;
            return (_latencyFrames + queuedFrames) / (double) _sampleRate;
        }

        private int FramesToBytes(int frames)
        {
            long bytes = Math.Max(0, frames) * (long) BYTES_PER_FRAME;
            return (int) Math.Min(bytes, int.MaxValue);
        }

        private double FramesToMilliseconds(long frames) => _sampleRate > 0
            ? Math.Max(0, frames) * 1000.0 / _sampleRate
            : 0;

        private static double TicksToMilliseconds(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;

        private static void UpdateMaximum(ref long target, long value)
        {
            long previous;
            do
            {
                previous = Volatile.Read(ref target);
                if (value <= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_notifyRegistered)
            {
                BassAsio.SetNotify(null, IntPtr.Zero);
                _notifyRegistered = false;
            }
            if (_asioStarted)
            {
                BassAsio.Stop();
                _asioStarted = false;
            }
#endif

            _renderAheadStream?.Dispose();
            _renderAheadStream = null;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_ownsAsio)
            {
                BassAsio.Free();
                _ownsAsio = false;
            }
#endif

            _songs.Clear();
            _samples.Clear();
            foreach (int monitorHandle in new List<int>(_monitors))
            {
                DetachMonitor(monitorHandle);
            }

            if (_outputMixerHandle != 0)
            {
                Bass.StreamFree(_outputMixerHandle);
                _outputMixerHandle = 0;
            }
            if (_songMixerHandle != 0)
            {
                Bass.StreamFree(_songMixerHandle);
                _songMixerHandle = 0;
            }
        }
    }
}
