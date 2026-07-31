#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;
using YARG.Audio.PitchDetection;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Float samples exposed by one native microphone graph.
    ///
    /// The source owns its native handles. Read, backlog, and reset operations must remain safe
    /// while the analysis worker is running; the owner stops and joins this pipeline before it
    /// disposes the source.
    /// </summary>
    internal interface IBassMicSampleSource
    {
        int SampleRate { get; }
        bool IsValid { get; }

        /// <summary>
        /// Reads up to <paramref name="destination"/> samples. Returns zero when caught up and
        /// a negative value on failure.
        /// </summary>
        int Read(Span<float> destination);

        /// <summary>
        /// Returns source-format bytes ahead of this source's analysis reader.
        /// </summary>
        int GetBacklogBytes();

        /// <summary>
        /// Drops buffered history so the next read starts at the source's live position.
        /// </summary>
        bool ResetToLive();
    }

    /// <summary>
    /// Shared managed microphone framing and analysis for native ASIO and non-ASIO graphs.
    ///
    /// Monitor audio never passes through this worker. A pause can therefore drop analysis data,
    /// but cannot make the native monitor replay old capture blocks and grow latency.
    /// </summary>
    internal sealed class BassMicAnalysisPipeline : IDisposable
    {
        private const float MIC_HIT_INPUT_THRESHOLD = 25f;
        private const float MIN_AMPLITUDE           = -160f;
        private const float AMPLITUDE_CALIBRATION   = 180f;
        private const float UNUSED_FRAME_VALUE      = -1f;

        private const int AMPLITUDE_SAMPLE_STRIDE = 4;
        private const int IDLE_POLL_INTERVAL_MS   = 1;
        private const int MAX_ANALYSIS_BACKLOG_FRAMES = 2;

        private readonly IBassMicSampleSource _source;
        private readonly Func<bool> _isRecordingOutput;
        private readonly Func<double> _getInputTime;
        private readonly object _processingLock = new();
        private readonly ConcurrentQueue<MicOutputFrame> _frameQueue = new();
        private readonly Thread _readThread;
        private readonly float[] _readBuffer;
        private readonly float[] _frameBuffer;
        private readonly PitchTracker _pitchDetector;
        private readonly int _frameSampleCount;
        private readonly int _maximumLiveBacklogBytes;

        private volatile bool _stopRequested;
        private bool _analysisDisabled;
        private float? _lastPitch;
        private float? _lastAmplitude;
        private int _frameSamples;
        private double _frameEndTime;
        private bool _hasFrameEndTime;
        private bool _readFailureLogged;
        private int _backlogResetCount;

        internal int BacklogResetCount => Volatile.Read(ref _backlogResetCount);

        public BassMicAnalysisPipeline(IBassMicSampleSource source, Func<bool> isRecordingOutput,
            Func<double> getInputTime)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _isRecordingOutput = isRecordingOutput ?? throw new ArgumentNullException(nameof(isRecordingOutput));
            _getInputTime = getInputTime ?? throw new ArgumentNullException(nameof(getInputTime));

            _frameSampleCount = Math.Max(1, checked(source.SampleRate * MicDevice.RECORD_PERIOD_MS / 1000));
            _readBuffer = new float[_frameSampleCount];
            _frameBuffer = new float[_frameSampleCount];
            _pitchDetector = new PitchTracker(source.SampleRate);
            _maximumLiveBacklogBytes = checked(_frameSampleCount * sizeof(float) *
                MAX_ANALYSIS_BACKLOG_FRAMES);

            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"Mic analysis {source.SampleRate} Hz",
            };
            _readThread.Start();
        }

        public bool Reset()
        {
            lock (_processingLock)
            {
                bool reset = _source.ResetToLive();
                ResetAnalysisState();
                _analysisDisabled = false;
                return reset;
            }
        }

        public bool StopAndJoin()
        {
            _stopRequested = true;
            if (Thread.CurrentThread == _readThread)
            {
                return true;
            }

            // Source reads are non-blocking ChannelGetData calls. Do not free native handles
            // beneath this thread if a platform violates that expectation and fails to exit.
            if (!_readThread.Join(1000))
            {
                YargLogger.LogError("Timed out waiting for microphone analysis worker to stop");
                // Do not allow any owner to free its source beneath an active native read. The
                // second join is intentional: it makes teardown safe instead of falling back to
                // finalizer timing or leaking a live capture graph.
                _readThread.Join();
            }

            return true;
        }

        public bool DequeueOutputFrame(out MicOutputFrame frame) => _frameQueue.TryDequeue(out frame);

        public void ClearOutputQueue() => _frameQueue.Clear();

        public void Dispose()
        {
            StopAndJoin();
        }

        private void ReadLoop()
        {
            try
            {
                while (!_stopRequested && _source.IsValid)
                {
                    if (!_isRecordingOutput())
                    {
                        ResetWhenAnalysisDisabled();
                        Thread.Sleep(IDLE_POLL_INTERVAL_MS);
                        continue;
                    }

                    _analysisDisabled = false;

                    lock (_processingLock)
                    {
                        if (_stopRequested || !_source.IsValid)
                        {
                            break;
                        }

                        int backlogBytes = _source.GetBacklogBytes();
                        if (backlogBytes < 0)
                        {
                            LogReadFailure("Failed to query microphone analysis backlog");
                            break;
                        }

                        // Stale analysis data is discarded instead of being drained after a managed
                        // pause. This bounds analysis lag independently from monitor playback.
                        if (backlogBytes > _maximumLiveBacklogBytes)
                        {
                            int resetCount = Interlocked.Increment(ref _backlogResetCount);
                            if (resetCount == 1 || resetCount % 32 == 0)
                            {
                                YargLogger.LogWarning(
                                    $"Dropped stale microphone analysis backlog: {backlogBytes} bytes " +
                                    $"(limit {_maximumLiveBacklogBytes}), reset {resetCount}");
                            }
                            if (!_source.ResetToLive())
                            {
                                LogReadFailure("Failed to reset microphone analysis backlog");
                                break;
                            }

                            ResetAnalysisState();
                            continue;
                        }

                        double readTime = _getInputTime();
                        int samplesRead = _source.Read(_readBuffer.AsSpan());
                        if (samplesRead < 0)
                        {
                            LogReadFailure("Failed to read microphone analysis samples");
                            break;
                        }

                        if (samplesRead > 0)
                        {
                            samplesRead = Math.Min(samplesRead, _readBuffer.Length);
                            AppendSamples(_readBuffer, samplesRead, readTime, backlogBytes);
                        }
                        else
                        {
                            Thread.Sleep(IDLE_POLL_INTERVAL_MS);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Microphone analysis worker failed");
            }
        }

        private void ResetWhenAnalysisDisabled()
        {
            if (_analysisDisabled)
            {
                return;
            }

            lock (_processingLock)
            {
                if (_analysisDisabled)
                {
                    return;
                }

                if (_source.IsValid && !_source.ResetToLive())
                {
                    LogReadFailure("Failed to reset disabled microphone analysis source");
                    return;
                }
                ResetAnalysisState();
                _analysisDisabled = true;
            }
        }

        private void AppendSamples(float[] samples, int sampleCount, double readTime, int backlogBytes)
        {
            int backlogSamples = Math.Max(0, backlogBytes / sizeof(float));
            int samplesRead = 0;
            while (samplesRead < sampleCount)
            {
                int frameSpace = _frameBuffer.Length - _frameSamples;
                int samplesToCopy = Math.Min(sampleCount - samplesRead, frameSpace);
                Array.Copy(samples, samplesRead, _frameBuffer, _frameSamples, samplesToCopy);

                _frameSamples += samplesToCopy;
                samplesRead += samplesToCopy;

                // The source backlog is measured before this read. The final sample copied into
                // the current frame is therefore this far behind the live input clock.
                int samplesStillAhead = Math.Max(0, backlogSamples - samplesRead);
                _frameEndTime = readTime - samplesStillAhead / (double) _source.SampleRate;
                _hasFrameEndTime = true;

                if (_frameSamples == _frameBuffer.Length)
                {
                    AnalyzeFrame(_frameBuffer, _hasFrameEndTime ? _frameEndTime : readTime);
                    _frameSamples = 0;
                    _hasFrameEndTime = false;
                }
            }
        }

        private void AnalyzeFrame(float[] samples, double frameEndTime)
        {
            float amplitude = CalculateAmplitude(samples);

            if (_lastAmplitude.HasValue && amplitude > _lastAmplitude.Value &&
                amplitude - _lastAmplitude.Value >= MIC_HIT_INPUT_THRESHOLD)
            {
                double frameMidpoint = frameEndTime - MicDevice.RECORD_PERIOD_MS / 2000.0;
                _frameQueue.Enqueue(new MicOutputFrame(frameMidpoint, true,
                    UNUSED_FRAME_VALUE, UNUSED_FRAME_VALUE));
            }
            _lastAmplitude = amplitude;

            if (amplitude < SettingsManager.Settings.MicrophoneSensitivity.Value)
            {
                _lastPitch = null;
                return;
            }

            float? pitch = _pitchDetector.ProcessBuffer(samples);
            if (pitch.HasValue)
            {
                _lastPitch = pitch.Value;
            }

            if (_lastPitch.HasValue)
            {
                _frameQueue.Enqueue(new MicOutputFrame(frameEndTime, false,
                    _lastPitch.Value, amplitude));
            }
        }

        private static float CalculateAmplitude(ReadOnlySpan<float> samples)
        {
            float squareSum = 0f;
            int sampledCount = 0;
            for (int i = 0; i < samples.Length; i += AMPLITUDE_SAMPLE_STRIDE)
            {
                squareSum += samples[i] * samples[i];
                sampledCount++;
            }

            if (sampledCount == 0)
            {
                return MIN_AMPLITUDE;
            }

            float rootMeanSquare = Mathf.Sqrt(squareSum / sampledCount);
            float amplitude = 20f * Mathf.Log10(rootMeanSquare * AMPLITUDE_CALIBRATION);
            if (amplitude < MIN_AMPLITUDE || float.IsNaN(amplitude))
            {
                return MIN_AMPLITUDE;
            }

            return amplitude;
        }

        private void ResetAnalysisState()
        {
            _lastPitch = null;
            _lastAmplitude = null;
            _frameSamples = 0;
            _frameEndTime = 0;
            _hasFrameEndTime = false;
            Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
            _pitchDetector.Reset();
            _frameQueue.Clear();
        }

        private void LogReadFailure(string message)
        {
            if (_readFailureLogged)
            {
                return;
            }

            _readFailureLogged = true;
            YargLogger.LogError($"{message}: {ManagedBass.Bass.LastError}");
        }
    }
}
