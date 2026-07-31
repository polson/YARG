#nullable enable
using System;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Owns callback-free non-ASIO capture and its native monitor/analysis graph.
    ///
    /// The monitor splitter is pulled by the output backend and drives capture. The analysis
    /// splitter only reads buffered data, so managed analysis reads can never drive capture or
    /// monitor playback.
    /// </summary>
    internal sealed class BassRecordingGraph : IBassMicSampleSource, IDisposable
    {
        private static readonly int[] SampleRates = { 48000, 44100, 96000, 16000 };

        private static readonly ManagedBass.Fx.PeakEQParameters LowAnalysisEq = new()
        {
            fBandwidth = 2.5f, fCenter = 20f, fGain = -10f,
        };

        private static readonly ManagedBass.Fx.PeakEQParameters HighAnalysisEq = new()
        {
            fBandwidth = 2.5f, fCenter = 10_000f, fGain = -10f,
        };

        private readonly object _lock = new();
        private readonly int _recordHandle;
        private readonly int _rootHandle;
        private readonly bool _ownsRoot;
        private readonly int _monitorHandle;
        private readonly int _analysisHandle;
        private readonly BassMonitorSource _monitorSource;
        private readonly BassMonitorRoute _monitorRoute;
        private readonly YARG.Audio.BASS.Effects.BassFreeverbDsp _reverb;

        private bool _started;
        private bool _captureRequested;
        private bool _disposed;
        private Action? _analysisReset;

        public int SampleRate { get; }
        public bool IsValid
        {
            get
            {
                lock (_lock)
                {
                    return !_disposed && _recordHandle != 0 && _analysisHandle != 0;
                }
            }
        }

        private BassRecordingGraph(int recordHandle, int rootHandle, int monitorHandle, int analysisHandle,
            BassMonitorSource monitorSource, BassMonitorRoute monitorRoute,
            YARG.Audio.BASS.Effects.BassFreeverbDsp reverb, int sampleRate)
        {
            _recordHandle = recordHandle;
            _rootHandle = rootHandle;
            _ownsRoot = rootHandle != recordHandle;
            _monitorHandle = monitorHandle;
            _analysisHandle = analysisHandle;
            _monitorSource = monitorSource;
            _monitorRoute = monitorRoute;
            _reverb = reverb;
            SampleRate = sampleRate;
        }

        public static BassRecordingGraph? Create(BassAudioOutput audioOutput)
        {
            if (audioOutput == null)
            {
                throw new ArgumentNullException(nameof(audioOutput));
            }

            int devicePeriod = Bass.GetConfig(Configuration.DevicePeriod);
            foreach (int sampleRate in SampleRates)
            {
                var graph = TryCreateForFormat(audioOutput, sampleRate, devicePeriod, BassFlags.Float);
                if (graph != null)
                {
                    return graph;
                }

                // Some capture drivers reject float recording. The normal capture format is
                // decoded by a float mixer below; monitor and analysis still see one topology.
                graph = TryCreateForFormat(audioOutput, sampleRate, devicePeriod, BassFlags.Default);
                if (graph != null)
                {
                    return graph;
                }
            }

            YargLogger.LogError("Failed to create callback-free recording graph at any supported sample rate");
            return null;
        }

        private static BassRecordingGraph? TryCreateForFormat(BassAudioOutput audioOutput, int sampleRate,
            int devicePeriod, BassFlags captureFlags)
        {
            int recordHandle = Bass.RecordStart(sampleRate, 1,
                captureFlags | BassFlags.RecordPause, devicePeriod, null, IntPtr.Zero);
            if (recordHandle == 0)
            {
                YargLogger.LogFormatTrace("Failed to create callback-free recording at {0} Hz ({1}): {2}",
                    sampleRate, captureFlags.HasFlag(BassFlags.Float) ? "float" : "native format", Bass.LastError);
                return null;
            }

            BassRecordingGraph? graph = null;
            try
            {
                graph = Build(audioOutput, recordHandle, sampleRate);
                if (graph != null)
                {
                    return graph;
                }
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to build callback-free recording graph");
            }

            return null;
        }

        private static BassRecordingGraph? Build(BassAudioOutput audioOutput, int recordHandle, int sampleRate)
        {
            int rootHandle = recordHandle;
            int monitorHandle = 0;
            int analysisHandle = 0;
            BassMonitorSource? monitorSource = null;
            BassMonitorRoute? monitorRoute = null;
            YARG.Audio.BASS.Effects.BassFreeverbDsp? reverb = null;
            BassRecordingGraph? graph = null;

            try
            {
                var recordInfo = Bass.ChannelGetInfo(recordHandle);
                bool captureIsFloat = (recordInfo.Flags & BassFlags.Float) != 0;
                bool captureIsDecoding = (recordInfo.Flags & BassFlags.Decode) != 0;
                if (!captureIsFloat || !captureIsDecoding)
                {
                    rootHandle = BassMix.CreateMixerStream(sampleRate, 1,
                        BassFlags.Float | BassFlags.Decode);
                    if (rootHandle == 0)
                    {
                        YargLogger.LogFormatError("Failed to create float recording normalization root at {0} Hz: {1}",
                            sampleRate, Bass.LastError);
                        return null;
                    }

                    if (!BassMix.MixerAddChannel(rootHandle, recordHandle,
                            BassFlags.MixerChanNoRampin))
                    {
                        YargLogger.LogFormatError("Failed to add recording source to normalization root: {0}",
                            Bass.LastError);
                        return null;
                    }
                }

                if (!HasFloatDecodeFlags(rootHandle))
                {
                    YargLogger.LogFormatError("Recording graph root {0} is not float/decode (flags: {1})",
                        rootHandle, Bass.ChannelGetInfo(rootHandle).Flags);
                    return null;
                }

                int rootSampleRate = Bass.ChannelGetInfo(rootHandle).Frequency;
                if (rootSampleRate <= 0)
                {
                    rootSampleRate = sampleRate;
                }

                monitorHandle = BassMix.CreateSplitStream(rootHandle,
                    BassFlags.Decode | BassFlags.SplitPosition, null);
                if (monitorHandle == 0)
                {
                    YargLogger.LogFormatError("Failed to create recording monitor split: {0}", Bass.LastError);
                    return null;
                }

                // Monitor split drives root. Analysis only consumes already-buffered source data.
                analysisHandle = BassMix.CreateSplitStream(rootHandle,
                    BassFlags.Decode | BassFlags.SplitPosition | BassFlags.SplitSlave, null);
                if (analysisHandle == 0)
                {
                    YargLogger.LogFormatError("Failed to create recording analysis split: {0}", Bass.LastError);
                    return null;
                }

                // Preserve existing non-ASIO analysis EQ. It is deliberately not on monitor.
                if (BassHelpers.AddEqToChannel(analysisHandle, LowAnalysisEq) == 0 ||
                    BassHelpers.AddEqToChannel(analysisHandle, HighAnalysisEq) == 0)
                {
                    YargLogger.LogFormatError("Failed to add EQ to recording analysis split: {0}", Bass.LastError);
                    return null;
                }

                reverb = BassMicMonitoringEffects.CreateReverb(monitorHandle);
                if (reverb == null)
                {
                    YargLogger.LogError("Failed to add reverb to recording monitor split");
                    return null;
                }

                monitorSource = BassMonitorSource.CreateSplit(monitorHandle, reverb.RequestReset);
                if (monitorSource == null)
                {
                    return null;
                }

                monitorRoute = audioOutput.RegisterMonitor(monitorSource, 1.3);
                if (monitorRoute == null)
                {
                    YargLogger.LogError("Failed to register recording monitor split with active audio output");
                    return null;
                }

                graph = new BassRecordingGraph(recordHandle, rootHandle, monitorHandle, analysisHandle,
                    monitorSource, monitorRoute, reverb, rootSampleRate);
                monitorRoute.SetLifecycleCallbacks(graph.OnMonitorAttached, graph.OnMonitorDetached);
                recordHandle = 0;
                rootHandle = 0;
                monitorHandle = 0;
                analysisHandle = 0;
                reverb = null;
                monitorRoute = null;
                return graph;
            }
            finally
            {
                monitorRoute?.Dispose();
                reverb?.Dispose();
                FreeChannel(analysisHandle, "recording analysis split");
                FreeChannel(monitorHandle, "recording monitor split");
                if (rootHandle != 0 && rootHandle != recordHandle)
                {
                    FreeChannel(rootHandle, "recording normalization root");
                }
                FreeChannel(recordHandle, "recording source");
            }
        }

        public unsafe int Read(Span<float> destination)
        {
            lock (_lock)
            {
                if (_disposed || _analysisHandle == 0)
                {
                    return -1;
                }

                if (destination.IsEmpty)
                {
                    return 0;
                }

                fixed (float* pointer = destination)
                {
                    int bytesRead = Bass.ChannelGetData(_analysisHandle, (IntPtr) pointer,
                        checked(destination.Length * sizeof(float)));
                    return bytesRead < 0 ? -1 : bytesRead / sizeof(float);
                }
            }
        }

        public int ReadAnalysis(Span<float> destination) => Read(destination);

        public int ReadAnalysis(float[] destination) => Read(destination.AsSpan());

        public int GetBacklogBytes()
        {
            lock (_lock)
            {
                return _disposed || _analysisHandle == 0
                    ? -1
                    : BassMix.SplitStreamGetAvailable(_analysisHandle);
            }
        }

        public int GetAnalysisBacklogBytes() => GetBacklogBytes();

        public bool ResetToLive()
        {
            lock (_lock)
            {
                if (_disposed || _analysisHandle == 0)
                {
                    return false;
                }

                if (BassMix.SplitStreamReset(_analysisHandle, 0))
                {
                    return true;
                }

                YargLogger.LogFormatError("Failed to reset recording analysis split: {0}", Bass.LastError);
                return false;
            }
        }

        public bool ResetAnalysisToLive() => ResetToLive();

        public bool ResetMonitorToLive()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                return _monitorSource.ResetToLive();
            }
        }

        public bool DiscardCaptureBuffer()
        {
            lock (_lock)
            {
                if (_disposed || _recordHandle == 0)
                {
                    return false;
                }

                int available = Bass.ChannelGetData(_recordHandle, IntPtr.Zero, (int) DataFlags.Available);
                if (available < 0)
                {
                    YargLogger.LogFormatError("Failed to query recording buffer: {0}", Bass.LastError);
                    return false;
                }

                if (available > 0 && Bass.ChannelGetData(_recordHandle, IntPtr.Zero, available) < 0)
                {
                    YargLogger.LogFormatError("Failed to discard recording buffer: {0}", Bass.LastError);
                    return false;
                }

                return true;
            }
        }

        public bool Start()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }
                _captureRequested = true;
                return StartNativeIfAttached();
            }
        }

        public bool Pause(bool clearCaptureRequest = true)
        {
            lock (_lock)
            {
                if (_disposed || !_started)
                {
                    if (clearCaptureRequest)
                    {
                        _captureRequested = false;
                    }
                    return !_disposed;
                }

                if (clearCaptureRequest)
                {
                    _captureRequested = false;
                }
                return PauseNative();
            }
        }

        private bool StartNativeIfAttached()
        {
            if (_started || !_monitorRoute.IsAttached)
            {
                return true;
            }

            if (!Bass.ChannelPlay(_recordHandle))
            {
                YargLogger.LogFormatError("Failed to start callback-free recording: {0}", Bass.LastError);
                return false;
            }

            _started = true;
            return true;
        }

        private bool PauseNative()
        {
            if (!_started)
            {
                return true;
            }

            if (!Bass.ChannelPause(_recordHandle))
            {
                YargLogger.LogFormatError("Failed to pause callback-free recording: {0}", Bass.LastError);
                return false;
            }

            _started = false;
            return true;
        }

        private void OnMonitorAttached()
        {
            lock (_lock)
            {
                if (!_disposed && _captureRequested && !StartNativeIfAttached())
                {
                    YargLogger.LogFormatError("Failed to resume recording after monitor attach: {0}", Bass.LastError);
                }
            }
        }

        private void OnMonitorDetached()
        {
            Action? resetAnalysis;
            lock (_lock)
            {
                if (!_disposed)
                {
                    PauseNative();
                    DiscardCaptureBuffer();
                }
                resetAnalysis = _disposed ? null : _analysisReset;
            }

            // Invoke outside graph lock. The worker takes its processing lock before taking the
            // graph lock, so invoking while holding this lock would invert lock order.
            try
            {
                resetAnalysis?.Invoke();
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to reset microphone analysis after monitor detach");
            }
        }

        public bool IsStarted
        {
            get
            {
                lock (_lock)
                {
                    return _started;
                }
            }
        }

        public void SetMonitoringLevel(double volume)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _monitorRoute.SetVolume(volume);
                }
            }
        }

        internal void SetAnalysisResetCallback(Action resetAnalysis)
        {
            _analysisReset = resetAnalysis ?? throw new ArgumentNullException(nameof(resetAnalysis));
        }

        public void DetachRoute()
        {
            BassMonitorRoute route;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
                route = _monitorRoute;
            }

            route.Dispose();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _captureRequested = false;
                _monitorRoute.Dispose();
                Bass.ChannelStop(_recordHandle);

                _reverb.Dispose();
                FreeChannel(_analysisHandle, "recording analysis split");
                FreeChannel(_monitorHandle, "recording monitor split");
                if (_ownsRoot)
                {
                    FreeChannel(_rootHandle, "recording normalization root");
                }
                FreeChannel(_recordHandle, "recording source");
            }
        }

        private static bool HasFloatDecodeFlags(int handle)
        {
            var flags = Bass.ChannelGetInfo(handle).Flags;
            return (flags & (BassFlags.Float | BassFlags.Decode)) ==
                (BassFlags.Float | BassFlags.Decode);
        }

        private static void FreeChannel(int handle, string description)
        {
            if (handle == 0)
            {
                return;
            }

            if (!Bass.StreamFree(handle))
            {
                YargLogger.LogFormatError("Failed to free {0} {1}: {2}", description, handle, Bass.LastError);
            }
        }
    }
}
