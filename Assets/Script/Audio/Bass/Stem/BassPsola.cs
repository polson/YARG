#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal static class BassPsola
    {
        internal static bool Enabled = true;

        private const double MAX_SHIFT_SEMITONES = 7;
        private const double MARK_SEARCH_RATIO   = 0.15;
        private const double MIN_MARK_MATCH      = 0.5;
        private const float  MIN_WINDOW_SUM      = 0.5f;
        private const int    MIN_MARKS           = 3;
        private const double MIN_LEVEL_GAIN      = 0.5;
        private const double MAX_LEVEL_GAIN      = 2;

        internal static bool CanShift(double factor)
        {
            return factor > 0 && Math.Abs(12 * Math.Log(factor, 2)) <= MAX_SHIFT_SEMITONES;
        }

        internal static float[]? Shift(float[] source, int channels, int frameCount, double periodFrames,
            int attackEndFrame, Func<double, double> factorAt)
        {
            int sourceFrames = source.Length / channels;
            if (frameCount <= 0 || periodFrames < 2 || sourceFrames < periodFrames * MIN_MARKS)
            {
                return null;
            }

            var marks = FindMarks(Mono(source, channels), periodFrames);
            if (marks == null)
            {
                return null;
            }

            var output = new float[frameCount * channels];
            var weights = new float[frameCount];
            int nearest = 0;
            int lastUsed = -1;
            double time = marks[0];
            while (time < frameCount)
            {
                while (nearest + 1 < marks.Count && Math.Abs(marks[nearest + 1] - time) <= Math.Abs(marks[nearest] - time))
                {
                    nearest++;
                }

                int chosen = Math.Max(nearest, lastUsed);
                if (time < attackEndFrame && chosen == lastUsed)
                {
                    chosen = Math.Min(lastUsed + 1, marks.Count - 1);
                }

                lastUsed = chosen;
                double analysisPeriod = LocalPeriod(marks, chosen);
                double synthesisPeriod = analysisPeriod / factorAt(time);
                int halfWidth = (int) Math.Round(Math.Max(analysisPeriod, synthesisPeriod));
                AddGrain(source, channels, marks[chosen], (int) Math.Round(time), halfWidth, output, weights);
                time += synthesisPeriod;
            }

            for (int frame = 0; frame < frameCount; frame++)
            {
                float weight = weights[frame];
                for (int channel = 0; channel < channels; channel++)
                {
                    int index = frame * channels + channel;
                    if (frame < marks[0])
                    {
                        float dry = frame < sourceFrames ? source[index] : 0;
                        output[index] = output[index] / Math.Max(weight, 1) + dry * Math.Max(0, 1 - weight);
                    }
                    else
                    {
                        output[index] /= Math.Max(weight, MIN_WINDOW_SUM);
                    }
                }
            }

            MatchLevel(source, output, Math.Min(frameCount, sourceFrames) * channels);
            return output;
        }

        private static void MatchLevel(float[] source, float[] output, int sampleCount)
        {
            double sourcePower = 0;
            double outputPower = 0;
            for (int index = 0; index < sampleCount; index++)
            {
                sourcePower += source[index] * source[index];
                outputPower += output[index] * output[index];
            }

            if (sourcePower <= 0 || outputPower <= 0)
            {
                return;
            }

            float gain = (float) Math.Clamp(Math.Sqrt(sourcePower / outputPower), MIN_LEVEL_GAIN, MAX_LEVEL_GAIN);
            for (int index = 0; index < output.Length; index++)
            {
                output[index] *= gain;
            }
        }

        private static float[] Mono(float[] source, int channels)
        {
            var mono = new float[source.Length / channels];
            for (int frame = 0; frame < mono.Length; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += source[frame * channels + channel];
                }

                mono[frame] = sum / channels;
            }

            return mono;
        }

        private static List<int>? FindMarks(float[] mono, double periodFrames)
        {
            int period = (int) Math.Round(periodFrames);
            int search = Math.Max(1, (int) (periodFrames * MARK_SEARCH_RATIO));
            int first = 0;
            float peak = -1;
            for (int frame = 0; frame < period; frame++)
            {
                float magnitude = Math.Abs(mono[frame]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                    first = frame;
                }
            }

            var marks = new List<int> { first };
            double matchTotal = 0;
            while (true)
            {
                int previous = marks[^1];
                int expected = previous + period;
                if (expected + search + period > mono.Length)
                {
                    break;
                }

                int best = expected;
                double bestMatch = double.MinValue;
                for (int candidate = expected - search; candidate <= expected + search; candidate++)
                {
                    double match = Match(mono, previous, candidate, period);
                    if (match > bestMatch)
                    {
                        bestMatch = match;
                        best = candidate;
                    }
                }

                matchTotal += bestMatch;
                marks.Add(best);
            }

            if (marks.Count < MIN_MARKS || matchTotal / (marks.Count - 1) < MIN_MARK_MATCH)
            {
                return null;
            }

            return marks;
        }

        private static double Match(float[] mono, int left, int right, int length)
        {
            double product = 0;
            double leftPower = 0;
            double rightPower = 0;
            for (int offset = 0; offset < length; offset++)
            {
                double a = mono[left + offset];
                double b = mono[right + offset];
                product += a * b;
                leftPower += a * a;
                rightPower += b * b;
            }

            double power = Math.Sqrt(leftPower * rightPower);
            return power > 0 ? product / power : 0;
        }

        private static double LocalPeriod(List<int> marks, int index)
        {
            return index + 1 < marks.Count
                ? marks[index + 1] - marks[index]
                : marks[index] - marks[index - 1];
        }

        private static void AddGrain(float[] source, int channels, int sourceCenter, int outputCenter, int halfWidth,
            float[] output, float[] weights)
        {
            int sourceFrames = source.Length / channels;
            int outputFrames = weights.Length;
            for (int offset = -halfWidth + 1; offset < halfWidth; offset++)
            {
                int outputFrame = outputCenter + offset;
                if (outputFrame < 0 || outputFrame >= outputFrames)
                {
                    continue;
                }

                float window = (float) (0.5 + 0.5 * Math.Cos(Math.PI * offset / halfWidth));
                weights[outputFrame] += window;
                int sourceFrame = sourceCenter + offset;
                if (sourceFrame < 0 || sourceFrame >= sourceFrames)
                {
                    continue;
                }

                for (int channel = 0; channel < channels; channel++)
                {
                    output[outputFrame * channels + channel] += window * source[sourceFrame * channels + channel];
                }
            }
        }
    }
}
