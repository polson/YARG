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
    /// Minimal microphone adapter for an input owned by the active ASIO output backend.
    /// Monitoring stays entirely in the native ASIO graph; this worker only drains analysis data.
    /// </summary>
    internal sealed class BassAsioMicDevice : MicDevice
    {
        private const float MIC_HIT_INPUT_THRESHOLD = 25f;
        private const float MIN_AMPLITUDE           = -160f;
        private const float AMPLITUDE_CALIBRATION   = 180f;
        private const float UNUSED_FRAME_VALUE      = -1f;

        private const int AMPLITUDE_SAMPLE_STRIDE = 4;
        private const int IDLE_POLL_INTERVAL_MS   = 1;

        private readonly BassAsioInputLease              _lease;
        private readonly object                          _processingLock = new();
        private readonly ConcurrentQueue<MicOutputFrame> _frameQueue     = new();
        private readonly Thread                          _readThread;
        private readonly float[]                         _readBuffer;
        private readonly float[]                         _frameBuffer;
        private readonly PitchTracker                    _pitchDetector;

        private volatile bool   _stopRequested;
        private          float? _lastPitch;
        private          float? _lastAmplitude;
        private          int    _frameSampleCount;

        internal static BassAsioMicDevice? Create(BassAudioManager manager, AsioInputDescriptor descriptor,
            string displayName)
        {
            var result = manager.TryAcquireAsioInput(descriptor.DriverId, descriptor.ChannelIndex, out var lease);
            if (result != AsioInputAcquireResult.Success || lease == null)
            {
                YargLogger.LogWarning($"Failed to acquire ASIO microphone '{displayName}': {result}");
                return null;
            }

            try
            {
                var device = new BassAsioMicDevice(lease, displayName);
                device.SetMonitoringLevel(SettingsManager.Settings.VocalMonitoring.Value);
                return device;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private BassAsioMicDevice(BassAsioInputLease lease, string displayName) : base(displayName)
        {
            _lease = lease;
            int frameSamples = Math.Max(1, lease.Descriptor.SampleRate * RECORD_PERIOD_MS / 1000);
            _readBuffer = new float[frameSamples];
            _frameBuffer = new float[frameSamples];
            _pitchDetector = new PitchTracker(lease.Descriptor.SampleRate);
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"ASIO mic {lease.Descriptor.ChannelIndex}",
            };
            _readThread.Start();
        }

        private void ReadLoop()
        {
            while (!_stopRequested && _lease.IsValid)
            {
                int bytesRead;
                lock (_processingLock)
                {
                    bytesRead = _lease.Read(_readBuffer);
                    if (bytesRead > 0 && IsRecordingOutput)
                    {
                        int sampleCount = Math.Min(bytesRead / sizeof(float), _readBuffer.Length);
                        ProcessSamples(_readBuffer.AsSpan(0, sampleCount));
                    }
                }

                // A full read may mean more buffered input is waiting. Drain it immediately so
                // analysis does not fall behind; only yield when the split stream is caught up.
                bool inputCaughtUp = bytesRead < _readBuffer.Length * sizeof(float);
                if (inputCaughtUp)
                {
                    Thread.Sleep(IDLE_POLL_INTERVAL_MS);
                }
            }
        }

        private void ProcessSamples(ReadOnlySpan<float> samples)
        {
            float? pitch = _pitchDetector.ProcessBuffer(samples);
            if (pitch.HasValue)
            {
                _lastPitch = pitch.Value;
            }

            AppendSamplesToFrame(samples);
        }

        private void AppendSamplesToFrame(ReadOnlySpan<float> samples)
        {
            while (!samples.IsEmpty)
            {
                int frameSpace = _frameBuffer.Length - _frameSampleCount;
                int samplesToCopy = Math.Min(samples.Length, frameSpace);

                var destination = _frameBuffer.AsSpan(_frameSampleCount, samplesToCopy);
                samples[..samplesToCopy].CopyTo(destination);

                _frameSampleCount += samplesToCopy;
                samples = samples[samplesToCopy..];

                if (_frameSampleCount == _frameBuffer.Length)
                {
                    AnalyzeFrame(_frameBuffer);
                    _frameSampleCount = 0;
                }
            }
        }

        private void AnalyzeFrame(ReadOnlySpan<float> samples)
        {
            float amplitude = CalculateAmplitude(samples);
            QueueHitIfDetected(amplitude);
            _lastAmplitude = amplitude;
            QueuePitchIfDetected(amplitude);
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

        private void QueueHitIfDetected(float amplitude)
        {
            if (!_lastAmplitude.HasValue)
            {
                return;
            }

            float amplitudeIncrease = amplitude - _lastAmplitude.Value;
            if (amplitudeIncrease < MIC_HIT_INPUT_THRESHOLD)
            {
                return;
            }

            // Current input time marks the end of the frame; hits belong at its midpoint.
            double frameMidpoint = InputManager.CurrentInputTime - RECORD_PERIOD_MS / 2000.0;
            var frame = new MicOutputFrame(frameMidpoint, true, UNUSED_FRAME_VALUE, UNUSED_FRAME_VALUE);
            _frameQueue.Enqueue(frame);
        }

        private void QueuePitchIfDetected(float amplitude)
        {
            if (amplitude < SettingsManager.Settings.MicrophoneSensitivity.Value)
            {
                _lastPitch = null;
                return;
            }

            if (_lastPitch.HasValue)
            {
                var frame = new MicOutputFrame(InputManager.CurrentInputTime, false, _lastPitch.Value, amplitude);
                _frameQueue.Enqueue(frame);
            }
        }

        public override int Reset()
        {
            lock (_processingLock)
            {
                bool resetSucceeded = _lease.Reset();
                ResetProcessingState();
                return resetSucceeded ? 0 : -1;
            }
        }

        private void ResetProcessingState()
        {
            _lastPitch = null;
            _lastAmplitude = null;
            _frameSampleCount = 0;
            Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
            _pitchDetector.Reset();
            _frameQueue.Clear();
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame) => _frameQueue.TryDequeue(out frame);

        public override void ClearOutputQueue() => _frameQueue.Clear();

        public override void SetMonitoringLevel(float volume)
        {
            if (!_lease.EnableMonitoring(volume))
            {
                YargLogger.LogWarning($"Failed to enable monitoring for ASIO microphone '{DisplayName}'");
            }
        }

        public override SerializedMic Serialize() => new(DisplayName);

        protected override void DisposeUnmanagedResources()
        {
            _stopRequested = true;
            if (Thread.CurrentThread != _readThread)
            {
                _readThread.Join(250);
            }

            _lease.Dispose();
        }
    }
}