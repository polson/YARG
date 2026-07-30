#nullable enable
using System;
using System.Collections.Generic;
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
        private const int ASIO_PROCESSING_THREADS = 1;
        private const int RENDER_AHEAD_MILLISECONDS = 30;
        private const int PREFILL_TIMEOUT_MILLISECONDS = 2000;

        private readonly int _bufferLength;
        private readonly Action _asioReinitializeRequested;
        private readonly HashSet<int> _songs = new();
        private readonly HashSet<int> _startedSongs = new();
        private readonly HashSet<int> _samples = new();
        private readonly HashSet<int> _monitors = new();
        private readonly Dictionary<int, BassAsioInput> _inputs = new();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private readonly AsioNotifyProcedure _notifyCallback;
#endif

        private AsioInputDescriptor[] _inputDescriptors = Array.Empty<AsioInputDescriptor>();
        private BassAsioMixerRouter? _mixerRouter;
        private int _songMixerHandle;
        private int _liveMixerHandle;
        private int _asioDeviceId = -1;
        private int _bassDeviceId;
        private int _sampleRate;
        private int _latencyFrames;
        private string _asioDriverId = string.Empty;
        private string _asioDriverName = string.Empty;
        private bool _ownsAsio;
        private bool _asioStarted;
        private bool _notifyRegistered;
        private bool _disposed;
        private int _notificationQueued;
        private double _volume = 1;
        private int _callbackFrames;
        private bool _songNeedsPrefill;

        public int HeardLatencyMilliseconds => (int) Math.Round(
            FramesToMilliseconds(_latencyFrames + QueuedFrames));

        public bool SongMixerRunsContinuously => true;
        public double PlaybackStartDelay => GetCommandDelay();

        private int QueuedFrames => checked((int) (_mixerRouter?.GetStats().QueuedFrames ?? 0));

        public BassAsioOutputBackend(int bufferLength, Action asioReinitializeRequested)
        {
            _bufferLength = bufferLength;
            _asioReinitializeRequested = asioReinitializeRequested;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            _notifyCallback = OnAsioNotification;
#endif
        }

        public bool Initialize(BassOutputDevice device)
        {
            _bassDeviceId = device.DeviceId;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                return InitializeAsioDriver(device.AsioDeviceId) &&
                    CreateOutputMixers(_sampleRate) && StartAsio();
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
            if (!_songs.Remove(tempoStreamHandle))
            {
                return;
            }
            _startedSongs.Remove(tempoStreamHandle);

            if (!BassMix.MixerRemoveChannel(tempoStreamHandle) && Bass.LastError != Errors.Handle)
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
            if (BassMix.ChannelFlags(tempoStreamHandle, BassFlags.Default,
                    BassFlags.MixerChanPause) < 0)
            {
                return (int) Bass.LastError;
            }

            if (_songNeedsPrefill)
            {
                if (!_mixerRouter!.Prefill(_songMixerHandle, PREFILL_TIMEOUT_MILLISECONDS))
                {
                    YargLogger.LogError("Failed to prefill native ASIO song buffer");
                    return -1;
                }
                _songNeedsPrefill = false;
            }
            _startedSongs.Add(tempoStreamHandle);
            return 0;
        }

        public int PauseSong(int tempoStreamHandle)
        {
            return BassMix.ChannelFlags(tempoStreamHandle, BassFlags.MixerChanPause,
                BassFlags.MixerChanPause) < 0 ? (int) Bass.LastError : 0;
        }

        public void PrepareSongForSeek(int tempoStreamHandle)
        {
            // A newly loaded preview seeks before its first Play. It has contributed no audio to
            // the shared ring, so flushing here would punch silence into the song fading out.
            if (_startedSongs.Contains(tempoStreamHandle))
            {
                _songNeedsPrefill = FlushSongBuffer();
            }
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
            return _mixerRouter?.GetSourcePosition(tempoStreamHandle, _latencyFrames) ?? -1;
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
            if (!BassMix.MixerAddChannel(_liveMixerHandle, sourceHandle, flags))
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

            if (!BassMix.MixerAddChannel(_liveMixerHandle, sourceHandle, flags) &&
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
            if (_mixerRouter != null)
            {
                if (!_mixerRouter.SetVolume(volume))
                {
                    YargLogger.LogError("Failed to set native ASIO output volume");
                }
            }
        }

        private bool CreateOutputMixers(int frequency)
        {
            // Native worker buffers song mixer. ASIO callback pulls live mixer directly.
            _songMixerHandle = BassMix.CreateMixerStream(frequency, OUTPUT_CHANNELS,
                BassFlags.Float | BassFlags.MixerNonStop | BassFlags.Decode |
                BassFlags.MixerPositionEx);
            if (_songMixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create ASIO song mixer: {0}", Bass.LastError);
                return false;
            }

            _liveMixerHandle = BassMix.CreateMixerStream(frequency, OUTPUT_CHANNELS,
                BassFlags.Float | BassFlags.MixerNonStop | BassFlags.Decode);
            if (_liveMixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create ASIO output mixer: {0}", Bass.LastError);
                return false;
            }

            _sampleRate = Bass.ChannelGetInfo(_songMixerHandle).Frequency;
            return true;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private bool InitializeAsioDriver(int deviceId)
        {
            if (!BassAsio.Init(deviceId, AsioInitFlags.Thread))
            {
                YargLogger.LogFormatError("Failed to initialize ASIO device: {0}",
                    BassAsio.LastError);
                return false;
            }
            _ownsAsio = true;
            _asioDeviceId = deviceId;
            BassAsio.CurrentDevice = deviceId;

            var asioInfo = BassAsio.Info;
            if (asioInfo.Outputs < OUTPUT_CHANNELS)
            {
                YargLogger.LogError("ASIO device does not provide stereo output");
                return false;
            }

            if (!TryGetActiveSampleRate(out _sampleRate))
            {
                return false;
            }

            var deviceInfo = BassAsio.GetDeviceInfo(deviceId);
            _asioDriverName = string.IsNullOrWhiteSpace(deviceInfo.Name)
                ? $"ASIO {deviceId}"
                : deviceInfo.Name;
            _asioDriverId = string.IsNullOrWhiteSpace(deviceInfo.Driver)
                ? _asioDriverName
                : deviceInfo.Driver;
            _callbackFrames = _bufferLength > 0
                ? _bufferLength
                : Math.Max(1, asioInfo.PreferredBufferLength);
            return true;
        }

        private static bool TryGetActiveSampleRate(out int sampleRate)
        {
            sampleRate = 0;
            double activeRate = BassAsio.Rate;
            double roundedRate = Math.Round(activeRate);
            bool isValid = !double.IsNaN(activeRate) && !double.IsInfinity(activeRate) &&
                activeRate > 0 && roundedRate > 0 && roundedRate <= int.MaxValue &&
                Math.Abs(activeRate - roundedRate) <= 0.01;
            if (!isValid)
            {
                YargLogger.LogFormatError("ASIO device reported invalid sample rate: {0}",
                    activeRate);
                return false;
            }

            sampleRate = (int) roundedRate;
            return true;
        }

        private bool StartAsio()
        {
            if (!CreateInputPool() || !ConfigureOutputTransport() ||
                !StartAsioProcessing())
            {
                return false;
            }

            CacheInputDescriptors();
            RegisterForDriverNotifications();
            return ConfigureOutputLatency();
        }

        private bool ConfigureOutputTransport()
        {
            _mixerRouter = BassAsioMixerRouter.Create(_bassDeviceId, _sampleRate,
                OUTPUT_CHANNELS, _callbackFrames);
            if (_mixerRouter != null &&
                _mixerRouter.AttachMixer(_songMixerHandle, RENDER_AHEAD_MILLISECONDS) &&
                _mixerRouter.AttachMixer(_liveMixerHandle, 0) &&
                _mixerRouter.SetVolume(_volume) &&
                _mixerRouter.Prefill(_songMixerHandle, PREFILL_TIMEOUT_MILLISECONDS) &&
                _mixerRouter.EnableOutput(0))
            {
                YargLogger.LogInfo("ASIO output transport: native YargAudio mixer router");
                return true;
            }

            _mixerRouter?.Dispose();
            _mixerRouter = null;
            YargLogger.LogError("Failed to configure native ASIO mixer router");
            return false;
        }

        private bool StartAsioProcessing()
        {
            if (!BassAsio.Start(_bufferLength, ASIO_PROCESSING_THREADS))
            {
                YargLogger.LogFormatError("Failed to start ASIO output: {0}", BassAsio.LastError);
                return false;
            }
            _asioStarted = true;
            YargLogger.LogFormatInfo("ASIO processing threads: {0}",
                ASIO_PROCESSING_THREADS);
            return true;
        }

        private void CacheInputDescriptors()
        {
            var descriptors = new AsioInputDescriptor[_inputs.Count];
            int descriptorIndex = 0;
            foreach (var input in _inputs.Values)
            {
                descriptors[descriptorIndex++] = input.Descriptor;
            }
            Array.Sort(descriptors, (left, right) =>
                left.ChannelIndex.CompareTo(right.ChannelIndex));
            _inputDescriptors = descriptors;
        }

        private void RegisterForDriverNotifications()
        {
            if (BassAsio.SetNotify(_notifyCallback, IntPtr.Zero))
            {
                _notifyRegistered = true;
            }
            else
            {
                YargLogger.LogFormatWarning(
                    "Failed to register for ASIO driver notifications: {0}", BassAsio.LastError);
            }
        }

        private bool ConfigureOutputLatency()
        {
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

        private bool CreateInputPool()
        {
            int inputCount = Math.Max(0, BassAsio.Info.Inputs);
            for (int channel = 0; channel < inputCount; channel++)
            {
                var channelInfo = BassAsio.ChannelGetInfo(true, channel);
                string name = string.IsNullOrWhiteSpace(channelInfo.Name)
                    ? $"Input {channel}"
                    : channelInfo.Name;
                BassAsioInput? input = BassAsioInput.Create(_asioDriverId, _asioDriverName,
                    channel, name, channelInfo.Group, _sampleRate);
                if (input == null)
                {
                    YargLogger.LogFormatError("Failed to create ASIO input {0}: {1}",
                        channel, Bass.LastError);
                    return false;
                }

                _inputs.Add(channel, input);
            }
            return true;
        }

        private bool ActivateInput(BassAsioInput input)
        {
            if (input.IsAttached)
            {
                return true;
            }

            BassAsio.CurrentDevice = _asioDeviceId;
            if (!BassAsio.Stop())
            {
                YargLogger.LogFormatError("Failed to stop ASIO while activating input {0}: {1}",
                    input.ChannelIndex, BassAsio.LastError);
                return false;
            }
            _asioStarted = false;

            bool configured = BassAsio.ChannelEnableBass(
                    true, input.ChannelIndex, input.RootHandle, Join: false) &&
                BassAsio.ChannelSetFormat(
                    true, input.ChannelIndex, AsioSampleFormat.Float) &&
                BassAsio.ChannelSetRate(true, input.ChannelIndex, _sampleRate) &&
                input.AttachToOutputMixer(_liveMixerHandle);
            if (!configured)
            {
                YargLogger.LogFormatError("Failed to activate ASIO input {0}: {1}",
                    input.ChannelIndex, BassAsio.LastError);
                BassAsio.ChannelReset(true, input.ChannelIndex,
                    AsioChannelResetFlags.Enable | AsioChannelResetFlags.Format |
                    AsioChannelResetFlags.Rate);
            }

            if (!BassAsio.Start(_bufferLength, ASIO_PROCESSING_THREADS))
            {
                YargLogger.LogFormatError("Failed to restart ASIO after activating input {0}: {1}",
                    input.ChannelIndex, BassAsio.LastError);
                return false;
            }
            _asioStarted = true;

            if (!configured)
            {
                return false;
            }

            YargLogger.LogFormatInfo("Activated selected ASIO input {0}", input.ChannelIndex);
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

#endif

        internal IReadOnlyList<AsioInputDescriptor> GetInputDescriptors() =>
            (AsioInputDescriptor[]) _inputDescriptors.Clone();

        internal AsioInputAcquireResult TryAcquireInput(string driverId, int channelIndex,
            out BassAsioInputLease? lease)
        {
            lease = null;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_disposed || !_asioStarted ||
                !_inputs.TryGetValue(channelIndex, out var input))
            {
                return AsioInputAcquireResult.UnavailableChannel;
            }
            if (!string.Equals(driverId, _asioDriverId, StringComparison.OrdinalIgnoreCase))
            {
                return AsioInputAcquireResult.DriverMismatch;
            }
            if (!ActivateInput(input))
            {
                return AsioInputAcquireResult.UnavailableChannel;
            }
            return input.TryAcquire(out lease);
#else
            return AsioInputAcquireResult.UnavailableChannel;
#endif
        }

        internal bool TryGetInputLevel(int channelIndex, out double level)
        {
            level = 0;
            if (_disposed || !_asioStarted ||
                !_inputs.TryGetValue(channelIndex, out var input) || !input.IsAttached)
            {
                return false;
            }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            BassAsio.CurrentDevice = _asioDeviceId;
            level = BassAsio.ChannelGetLevel(true, channelIndex);
            return level >= 0;
#else
            return false;
#endif
        }

        private double GetCommandDelay()
        {
            if (_sampleRate <= 0)
            {
                return 0;
            }

            int queuedFrames = checked((int) (_mixerRouter?.GetStats().QueuedFrames ?? 0));
            return (_latencyFrames + queuedFrames) / (double) _sampleRate;
        }

        private bool FlushSongBuffer()
        {
            if (_mixerRouter?.FlushMixer(_songMixerHandle) == true)
            {
                return true;
            }

            YargLogger.LogError("Failed to flush native ASIO song buffer");
            return false;
        }

        private double FramesToMilliseconds(long frames) => _sampleRate > 0
            ? Math.Max(0, frames) * 1000.0 / _sampleRate
            : 0;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            StopAsio();
#endif

            InvalidateInputs();
            LogRouterSummary();
            _mixerRouter?.Dispose();
            _mixerRouter = null;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_ownsAsio)
            {
                BassAsio.Free();
                _ownsAsio = false;
            }
#endif

            FreeInputs();
            DetachTrackedChannels();
            FreeMixers();
        }

        private void LogRouterSummary()
        {
            if (_mixerRouter == null)
            {
                return;
            }

            AsioMixerRouterStats stats = _mixerRouter.GetStats();
            YargLogger.LogFormatInfo(
                "ASIO router stopped: state={0}, queued={1}, produced={2}, consumed={3}, " +
                "requested={4}, underruns={5} ({6} frames), max render={7} ns, error={8}",
                stats.State, stats.QueuedFrames, stats.ProducedFrames, stats.ConsumedSongFrames,
                stats.RequestedOutputFrames, stats.UnderrunEvents, stats.UnderrunFrames,
                stats.MaximumRenderNanoseconds, stats.LastError);
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private void StopAsio()
        {
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
        }
#endif

        private void InvalidateInputs()
        {
            foreach (var input in _inputs.Values)
            {
                input.Invalidate();
            }
        }

        private void FreeInputs()
        {
            foreach (var input in _inputs.Values)
            {
                input.FreeNativeStreams();
            }

            _inputs.Clear();
            _inputDescriptors = Array.Empty<AsioInputDescriptor>();
        }

        private void DetachTrackedChannels()
        {
            _songs.Clear();
            _samples.Clear();
            foreach (int monitorHandle in new List<int>(_monitors))
            {
                DetachMonitor(monitorHandle);
            }
        }

        private void FreeMixers()
        {
            if (_liveMixerHandle != 0)
            {
                Bass.StreamFree(_liveMixerHandle);
                _liveMixerHandle = 0;
            }

            if (_songMixerHandle != 0)
            {
                Bass.StreamFree(_songMixerHandle);
                _songMixerHandle = 0;
            }
        }
    }
}
