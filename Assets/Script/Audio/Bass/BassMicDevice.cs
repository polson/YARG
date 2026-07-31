#nullable enable
using System;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Audio.BASS.Effects;
using YARG.Core.Audio;
using YARG.Core.IO;
using YARG.Core.Logging;
using YARG.Input;

namespace YARG.Audio.BASS
{
    internal static class BassMicMonitoringEffects
    {
        public static BassFreeverbDsp? CreateReverb(int streamHandle) =>
            BassFreeverbDsp.Create(streamHandle,
                dryMix: 0.3f,
                wetMix: 1f,
                roomSize: 0.4f,
                damp: 0.7f,
                width: 0f,
                priority: 1);
    }

    /// <summary>
    /// Non-ASIO microphone backed by a callback-free native BASS graph.
    /// </summary>
    public sealed class BassMicDevice : MicDevice
    {
        private readonly object _lifecycleLock = new();
        private readonly int _deviceId;
        private readonly BassAudioOutput _audioOutput;

        private BassRecordingGraph? _recordingGraph;
        private BassMicAnalysisPipeline? _analysisPipeline;

        internal static BassMicDevice? Create(int deviceId, string name, BassAudioOutput audioOutput)
        {
            // Must initialise device before recording.
            if (!Bass.RecordInit(deviceId))
            {
                YargLogger.LogError(
                    $"Failed to initialize BASS recording device [{deviceId}] '{name}': {Bass.LastError}!");
                return null;
            }

            var device = new BassMicDevice(deviceId, name, audioOutput);
            if (!device.CreateGraphAndStart())
            {
                FreeDevice(deviceId);
                return null;
            }

            return device;
        }

        private static void FreeDevice(int deviceId)
        {
            Bass.CurrentRecordingDevice = deviceId;
            if (!Bass.RecordFree())
            {
                YargLogger.LogFormatWarning("Failed to free recording device after initialization failure: {0}!",
                    Bass.LastError);
            }
        }

        private BassMicDevice(int deviceId, string name, BassAudioOutput audioOutput) : base(name)
        {
            _deviceId = deviceId;
            _audioOutput = audioOutput;
        }

        public override int Reset()
        {
            lock (_lifecycleLock)
            {
                var graph = _recordingGraph;
                var pipeline = _analysisPipeline;
                if (graph == null || pipeline == null)
                {
                    return 0;
                }

                bool wasStarted = graph.IsStarted;
                if (!graph.Pause(clearCaptureRequest: false))
                {
                    return (int) Bass.LastError;
                }

                bool captureReset = graph.DiscardCaptureBuffer();
                bool analysisReset = pipeline.Reset();
                bool monitorReset = graph.ResetMonitorToLive();
                bool restartSucceeded = !wasStarted || graph.Start();

                if (!captureReset || !analysisReset || !monitorReset || !restartSucceeded)
                {
                    return (int) Bass.LastError;
                }

                return 0;
            }
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame)
        {
            if (_analysisPipeline != null)
            {
                return _analysisPipeline.DequeueOutputFrame(out frame);
            }

            frame = default;
            return false;
        }

        public override void ClearOutputQueue() => _analysisPipeline?.ClearOutputQueue();

        public override void SetMonitoringLevel(float volume) =>
            _recordingGraph?.SetMonitoringLevel(volume);

        public override SerializedMic Serialize() => new(DisplayName);

        private bool CreateGraphAndStart()
        {
            lock (_lifecycleLock)
            {
                if (_recordingGraph != null)
                {
                    return _recordingGraph.Start();
                }

                // BASS recording-device selection is thread-local.
                Bass.CurrentRecordingDevice = _deviceId;
                var graph = BassRecordingGraph.Create(_audioOutput);
                if (graph == null)
                {
                    return false;
                }

                BassMicAnalysisPipeline? pipeline = null;
                try
                {
                    pipeline = new BassMicAnalysisPipeline(graph,
                        () => IsRecordingOutput,
                        () => InputManager.CurrentInputTime);
                    graph.SetAnalysisResetCallback(() => pipeline.Reset());
                    if (!graph.Start())
                    {
                        pipeline.StopAndJoin();
                        graph.Dispose();
                        return false;
                    }

                    _recordingGraph = graph;
                    _analysisPipeline = pipeline;
                    return true;
                }
                catch (Exception exception)
                {
                    pipeline?.StopAndJoin();
                    graph.Dispose();
                    YargLogger.LogException(exception, $"Failed to initialize microphone '{DisplayName}'");
                    return false;
                }
            }
        }

        public void StopRecording()
        {
            lock (_lifecycleLock)
            {
                var graph = _recordingGraph;
                var pipeline = _analysisPipeline;
                if (graph == null || pipeline == null)
                {
                    return;
                }

                graph.Pause();
                graph.DiscardCaptureBuffer();
                pipeline.Reset();
                graph.ResetMonitorToLive();
            }
        }

        public void StartRecording()
        {
            lock (_lifecycleLock)
            {
                if (_recordingGraph == null)
                {
                    CreateGraphAndStart();
                    return;
                }

                _recordingGraph.Start();
            }
        }

        public void RestartRecording()
        {
            lock (_lifecycleLock)
            {
                var graph = _recordingGraph;
                var pipeline = _analysisPipeline;
                if (graph == null || pipeline == null)
                {
                    CreateGraphAndStart();
                    return;
                }

                // Keep HRECORD and native splitters alive across scene/song transitions. Pausing
                // before reset prevents the analysis worker from reading while positions move.
                if (!graph.Pause(clearCaptureRequest: false))
                {
                    YargLogger.LogFormatError("Failed to pause recording for mic '{0}': {1}",
                        DisplayName, Bass.LastError);
                    return;
                }

                graph.DiscardCaptureBuffer();
                pipeline.Reset();
                if (!graph.ResetMonitorToLive())
                {
                    YargLogger.LogFormatError("Failed to reset monitor for mic '{0}'", DisplayName);
                }

                if (!graph.Start())
                {
                    YargLogger.LogFormatError("Failed to resume recording for mic '{0}': {1}",
                        DisplayName, Bass.LastError);
                }
            }
        }

        protected override void DisposeUnmanagedResources()
        {
            BassMicAnalysisPipeline? pipeline;
            BassRecordingGraph? graph;
            lock (_lifecycleLock)
            {
                pipeline = _analysisPipeline;
                graph = _recordingGraph;
            }

            if (pipeline != null && !pipeline.StopAndJoin())
            {
                // Never free the source beneath an active ChannelGetData call.
                YargLogger.LogError($"Keeping microphone '{DisplayName}' native graph alive after worker shutdown failure");
                return;
            }

            graph?.DetachRoute();
            graph?.Dispose();

            Bass.CurrentRecordingDevice = _deviceId;
            if (!Bass.RecordFree())
            {
                YargLogger.LogWarning(
                    $"Failed to free BASS recording device [{_deviceId}] '{DisplayName}': {Bass.LastError}");
            }
        }
    }
}
