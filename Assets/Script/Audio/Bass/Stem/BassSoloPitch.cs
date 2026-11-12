using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassSoloPitch
    {
        private const double WINDOW_SECONDS  = 0.023;
        private const double HOP_SECONDS     = 0.01;
        private const double MIN_FREQUENCY   = 80;
        private const double MAX_FREQUENCY   = 2500;
        private const double THRESHOLD       = 0.2;
        private const double MIN_MEAN_SQUARE = 0.000001;

        private readonly int      _sampleRate;
        private readonly int      _window;
        private readonly int      _hop;
        private readonly int      _minLag;
        private readonly int      _maxLag;
        private readonly double[] _difference;
        private          double[] _energy = Array.Empty<double>();

        public BassSoloPitch(int sampleRate)
            : this(sampleRate, MIN_FREQUENCY, MAX_FREQUENCY, WINDOW_SECONDS, HOP_SECONDS)
        {
        }

        public BassSoloPitch(int sampleRate, double minFrequency, double maxFrequency, double windowSeconds,
            double hopSeconds)
        {
            _sampleRate = sampleRate;
            _window = (int) Math.Round(windowSeconds * sampleRate);
            _hop = (int) Math.Round(hopSeconds * sampleRate);
            _minLag = (int) (sampleRate / maxFrequency);
            _maxLag = (int) Math.Ceiling(sampleRate / minFrequency);
            _difference = new double[_maxLag + 2];
        }

        public int HopFrames => _hop;

        public void Detect(float[] mono, List<float> readings)
        {
            readings.Clear();
            if (_energy.Length < mono.Length + 1)
            {
                _energy = new double[mono.Length + 1];
            }

            for (int index = 0; index < mono.Length; index++)
            {
                _energy[index + 1] = _energy[index] + mono[index] * mono[index];
            }

            for (int start = 0; start + _window + _maxLag + 1 <= mono.Length; start += _hop)
            {
                readings.Add(DetectAt(mono, start));
            }
        }

        private float DetectAt(float[] mono, int start)
        {
            double windowEnergy = _energy[start + _window] - _energy[start];
            if (windowEnergy / _window < MIN_MEAN_SQUARE)
            {
                return 0f;
            }

            double running = 0;
            _difference[0] = 1;
            for (int lag = 1; lag <= _maxLag + 1; lag++)
            {
                double laggedEnergy = _energy[start + lag + _window] - _energy[start + lag];
                double difference = windowEnergy + laggedEnergy - 2 * Correlate(mono, start, lag);
                running += difference;
                _difference[lag] = running > 0 ? difference * lag / running : 1;
            }

            for (int lag = _minLag; lag <= _maxLag; lag++)
            {
                if (_difference[lag] >= THRESHOLD)
                {
                    continue;
                }

                while (lag < _maxLag && _difference[lag + 1] < _difference[lag])
                {
                    lag++;
                }

                return (float) (_sampleRate / RefineLag(lag));
            }

            return 0f;
        }

        private double Correlate(float[] mono, int start, int lag)
        {
            float sum0 = 0;
            float sum1 = 0;
            float sum2 = 0;
            float sum3 = 0;
            int end = start + _window;
            int index = start;
            for (; index + 3 < end; index += 4)
            {
                sum0 += mono[index] * mono[index + lag];
                sum1 += mono[index + 1] * mono[index + 1 + lag];
                sum2 += mono[index + 2] * mono[index + 2 + lag];
                sum3 += mono[index + 3] * mono[index + 3 + lag];
            }

            for (; index < end; index++)
            {
                sum0 += mono[index] * mono[index + lag];
            }

            return (double) sum0 + sum1 + sum2 + sum3;
        }

        private double RefineLag(int lag)
        {
            double before = _difference[lag - 1];
            double at = _difference[lag];
            double after = _difference[lag + 1];
            double curvature = before - 2 * at + after;
            if (curvature <= 0)
            {
                return lag;
            }

            return lag + Math.Clamp((before - after) / (2 * curvature), -1, 1);
        }
    }
}
