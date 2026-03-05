using System;
using System.Collections.Concurrent;
using UnityEngine;
using YARG.Audio.PitchDetection;
using YARG.Core.Audio;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    internal class MicDataProcessor
    {
        private const float MIC_HIT_INPUT_THRESHOLD = 25f;

        private readonly PitchTracker _pitchDetector;
        private readonly ConcurrentQueue<MicOutputFrame> _frameQueue = new();

        private float? _lastPitch;
        private float? _lastAmplitude;

        public MicDataProcessor(float sampleRate = 44100f)
        {
            _pitchDetector = new PitchTracker(sampleRate: sampleRate);
        }

        public void Process(ReadOnlySpan<float> floatBuffer, double timestamp)
        {
            // Calculate the root mean square
            float sum = 0f;
            int count = 0;
            const int RMS_STRIDE = 4;
            for (int i = 0; i < floatBuffer.Length; i += RMS_STRIDE, count++)
            {
                sum += floatBuffer[i] * floatBuffer[i];
            }

            sum = Mathf.Sqrt(sum / count);

            // Convert to decibels to get the amplitude
            const float LOG_FACTOR = 20f;
            const float SCALE_FACTOR = 180f;
            const float MIN_AMPLITUDE = -160f;
            float amplitude = LOG_FACTOR * Mathf.Log10(sum * SCALE_FACTOR);
            if (amplitude < MIN_AMPLITUDE)
            {
                amplitude = MIN_AMPLITUDE;
            }

            // Detect peaks for hit inputs
            if (amplitude > _lastAmplitude && Mathf.Abs(amplitude - _lastAmplitude.Value) >= MIC_HIT_INPUT_THRESHOLD)
            {
                var hitFrame = new MicOutputFrame(timestamp, true, -1f, -1f);
                _frameQueue.Enqueue(hitFrame);
            }

            _lastAmplitude = amplitude;

            // Skip pitch detection if not speaking
            var micSensitivity = SettingsManager.Settings.MicrophoneSensitivity.Value;
            if (amplitude < micSensitivity)
            {
                _lastPitch = null;
                return;
            }

            // Process the pitch buffer
            var pitchOutput = _pitchDetector.ProcessBuffer(floatBuffer);
            if (pitchOutput != null)
            {
                _lastPitch = pitchOutput;
            }

            // We cannot push a frame if there was no pitch
            if (_lastPitch == null)
            {
                return;
            }

            // Queue a MicOutput frame
            var frame = new MicOutputFrame(timestamp, false, _lastPitch.Value, amplitude);
            _frameQueue.Enqueue(frame);
        }

        public bool DequeueFrame(out MicOutputFrame frame)
        {
            return _frameQueue.TryDequeue(out frame);
        }

        public void Clear()
        {
            _frameQueue.Clear();
            _pitchDetector.Reset();
            _lastPitch = null;
            _lastAmplitude = null;
        }
    }
}
