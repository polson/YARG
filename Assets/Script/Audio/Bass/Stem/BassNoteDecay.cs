#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal static class BassNoteDecay
    {
        private const double SETTLE_SECONDS      = 0.03;
        private const double WINDOW_SECONDS      = 0.02;
        private const double MIN_SPAN_SECONDS    = 0.25;
        private const double DEFAULT_DB_PER_SEC  = 3;
        private const double MIN_DB_PER_SEC      = 1;
        private const double MAX_DB_PER_SEC      = 10;

        internal static double Measure(float[] samples, BassStemFormat format, int attackEndFrame)
        {
            int channels = format.Channels;
            int frameCount = samples.Length / channels;
            int windowFrames = format.FramesFor(WINDOW_SECONDS);
            int firstFrame = attackEndFrame + format.FramesFor(SETTLE_SECONDS);
            int windows = (frameCount - firstFrame) / windowFrames;
            if (windowFrames <= 0 || windows * windowFrames < format.FramesFor(MIN_SPAN_SECONDS))
            {
                return double.NaN;
            }

            double sumTime = 0;
            double sumLevel = 0;
            double sumTimeLevel = 0;
            double sumTimeSquared = 0;
            for (int window = 0; window < windows; window++)
            {
                int start = (firstFrame + window * windowFrames) * channels;
                double energy = 0;
                for (int sample = start; sample < start + windowFrames * channels; sample++)
                {
                    energy += (double) samples[sample] * samples[sample];
                }

                if (energy <= 0)
                {
                    return double.NaN;
                }

                double time = window * WINDOW_SECONDS;
                double level = 10 * Math.Log10(energy / (windowFrames * channels));
                sumTime += time;
                sumLevel += level;
                sumTimeLevel += time * level;
                sumTimeSquared += time * time;
            }

            double spread = windows * sumTimeSquared - sumTime * sumTime;
            return spread > 0 ? -(windows * sumTimeLevel - sumTime * sumLevel) / spread : double.NaN;
        }

        internal static double Typical(IEnumerable<double> measured)
        {
            var rates = new List<double>();
            foreach (double rate in measured)
            {
                if (!double.IsNaN(rate))
                {
                    rates.Add(rate);
                }
            }

            if (rates.Count == 0)
            {
                return DEFAULT_DB_PER_SEC;
            }

            rates.Sort();
            return Math.Clamp(rates[rates.Count / 2], MIN_DB_PER_SEC, MAX_DB_PER_SEC);
        }
    }
}
