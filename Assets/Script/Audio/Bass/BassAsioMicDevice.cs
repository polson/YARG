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
        private const int IDLE_POLL_INTERVAL_MS = 1;

        private readonly BassAsioInputLease _lease;
        private readonly object _processingLock = new();
        private readonly ConcurrentQueue<MicOutputFrame> _frameQueue = new();
        private readonly Thread _worker;
        private readonly float[] _analysisBuffer;
        private readonly float[] _outputFrameBuffer;
        private readonly PitchTracker _pitchDetector;

        private volatile bool _stopping;
        private float? _lastPitch;
        private float? _lastAmplitude;
        private int _outputFrameSamples;

        internal static BassAsioMicDevice? Create(BassAudioManager manager,
            AsioInputDescriptor descriptor, string displayName)
        {
            var result = manager.TryAcquireAsioInput(descriptor.DriverId,
                descriptor.ChannelIndex, out var lease);
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

        private BassAsioMicDevice(BassAsioInputLease lease, string displayName)
            : base(displayName)
        {
            _lease = lease;
            int frameSamples = Math.Max(1,
                lease.Descriptor.SampleRate * RECORD_PERIOD_MS / 1000);
            _analysisBuffer = new float[frameSamples];
            _outputFrameBuffer = new float[frameSamples];
            _pitchDetector = new PitchTracker(lease.Descriptor.SampleRate);
            _worker = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"ASIO mic {lease.Descriptor.ChannelIndex}",
            };
            _worker.Start();
        }

        private void ReadLoop()
        {
            while (!_stopping && _lease.IsValid)
            {
                int bytesRead = _lease.Read(_analysisBuffer);
                if (bytesRead <= 0)
                {
                    Thread.Sleep(IDLE_POLL_INTERVAL_MS);
                    continue;
                }

                int samplesRead = Math.Min(bytesRead / sizeof(float), _analysisBuffer.Length);
                lock (_processingLock)
                {
                    if (IsRecordingOutput && samplesRead > 0)
                    {
                        ProcessSamples(new ReadOnlySpan<float>(_analysisBuffer, 0, samplesRead));
                    }
                }

                // A full read may mean more buffered input is waiting. Drain it immediately so
                // analysis does not fall behind; only yield when the split stream is caught up.
                if (bytesRead < _analysisBuffer.Length * sizeof(float))
                {
                    Thread.Sleep(IDLE_POLL_INTERVAL_MS);
                }
            }
        }

        private void ProcessSamples(ReadOnlySpan<float> samples)
        {
            var pitch = _pitchDetector.ProcessBuffer(samples);
            if (pitch.HasValue)
            {
                _lastPitch = pitch.Value;
            }

            int samplesProcessed = 0;
            while (samplesProcessed < samples.Length)
            {
                int samplesToCopy = Math.Min(
                    samples.Length - samplesProcessed,
                    _outputFrameBuffer.Length - _outputFrameSamples);
                samples.Slice(samplesProcessed, samplesToCopy).CopyTo(
                    _outputFrameBuffer.AsSpan(_outputFrameSamples));
                samplesProcessed += samplesToCopy;
                _outputFrameSamples += samplesToCopy;

                if (_outputFrameSamples == _outputFrameBuffer.Length)
                {
                    ProcessOutputFrame(_outputFrameBuffer);
                    _outputFrameSamples = 0;
                }
            }
        }

        private void ProcessOutputFrame(ReadOnlySpan<float> samples)
        {
            float sum = 0;
            int count = 0;
            for (int i = 0; i < samples.Length; i += 4, count++)
            {
                sum += samples[i] * samples[i];
            }

            float amplitude = count > 0 ? 20f * Mathf.Log10(Mathf.Sqrt(sum / count) * 180f) : -160f;
            if (amplitude < -160f || float.IsNaN(amplitude))
            {
                amplitude = -160f;
            }

            if (_lastAmplitude.HasValue && amplitude > _lastAmplitude.Value &&
                Mathf.Abs(amplitude - _lastAmplitude.Value) >= MIC_HIT_INPUT_THRESHOLD)
            {
                double windowMidpoint = InputManager.CurrentInputTime -
                    RECORD_PERIOD_MS / 2000.0;
                _frameQueue.Enqueue(new MicOutputFrame(windowMidpoint,
                    true, -1f, -1f));
            }
            _lastAmplitude = amplitude;

            if (amplitude < SettingsManager.Settings.MicrophoneSensitivity.Value)
            {
                _lastPitch = null;
                return;
            }

            if (_lastPitch.HasValue)
            {
                _frameQueue.Enqueue(new MicOutputFrame(InputManager.CurrentInputTime,
                    false, _lastPitch.Value, amplitude));
            }
        }

        public override int Reset()
        {
            lock (_processingLock)
            {
                _lastPitch = null;
                _lastAmplitude = null;
                _outputFrameSamples = 0;
                Array.Clear(_outputFrameBuffer, 0, _outputFrameBuffer.Length);
                _pitchDetector.Reset();
                _frameQueue.Clear();
            }
            return _lease.Reset() ? 0 : -1;
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame) =>
            _frameQueue.TryDequeue(out frame);

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
            _stopping = true;
            if (Thread.CurrentThread != _worker)
            {
                _worker.Join(250);
            }
            _lease.Dispose();
        }
    }
}
