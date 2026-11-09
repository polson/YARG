using System;

namespace YARG.Audio.BASS
{
    internal static class BassSustainLoop
    {
        private const double ATTACK_SECONDS = 0.03;
        private const double BLOCK_SECONDS = 0.02;
        private const double MIN_LOOP_SECONDS = 0.06;
        private const double MAX_LOOP_SECONDS = 0.4;
        private const double SEARCH_SECONDS = 0.6;
        private const double MIN_POWER = 0.000001;
        private const double MIN_BRIGHTNESS = 0.000001;
        private const double MAX_POWER_RATIO = 2;
        private const double MAX_BRIGHTNESS_RATIO = 1.8;
        private const double MAX_MATCH_ERROR = 0.12;
        private const double MIN_PERIOD_SECONDS = 1 / 1500.0;
        private const double MAX_PERIOD_SECONDS = 1 / 40.0;
        private const double MIN_PERIODICITY = 0.8;
        private const double PERIOD_PEAK_RATIO = 0.9;

        internal static bool TryFind(float[] samples, BassStemFormat format, int firstFrame,
            int fadeFrames, out int startFrame, out int endFrame)
        {
            startFrame = 0;
            endFrame = 0;
            int frameCount = samples.Length / format.Channels;
            int blockFrames = format.FramesFor(BLOCK_SECONDS);
            int attackFrames = format.FramesFor(ATTACK_SECONDS);
            int minFrames = format.FramesFor(MIN_LOOP_SECONDS) + fadeFrames;
            int maxFrames = format.FramesFor(MAX_LOOP_SECONDS) + fadeFrames;
            int searchFrames = format.FramesFor(SEARCH_SECONDS);
            int searchStep = Math.Max(1, format.Frequency / 1000);
            int sampleStep = Math.Max(1, format.Frequency / 8000);
            int analysisStart = Math.Max(firstFrame, frameCount - searchFrames);
            int stableStart = analysisStart + attackFrames;
            double previousPower = 0;
            double previousBrightness = 0;
            double bestScore = MAX_MATCH_ERROR;
            int bestFirstStart = 0;

            for (int end = analysisStart + blockFrames; end <= frameCount; end += blockFrames)
            {
                double energy = 0;
                double changes = 0;
                for (int frame = end - blockFrames + 1; frame < end; frame++)
                {
                    for (int channel = 0; channel < format.Channels; channel++)
                    {
                        int index = frame * format.Channels + channel;
                        double sample = samples[index];
                        double difference = sample - samples[index - format.Channels];
                        energy += sample * sample;
                        changes += difference * difference;
                    }
                }

                double power = energy / ((blockFrames - 1) * format.Channels);
                if (power < MIN_POWER)
                {
                    stableStart = end + attackFrames;
                    previousPower = 0;
                    previousBrightness = 0;
                    continue;
                }

                double brightness = changes / energy;
                if (brightness < MIN_BRIGHTNESS)
                {
                    stableStart = end + attackFrames;
                    previousPower = 0;
                    previousBrightness = 0;
                    continue;
                }

                bool powerJumped = power > previousPower * MAX_POWER_RATIO || previousPower > power * MAX_POWER_RATIO;
                bool toneJumped = brightness > previousBrightness * MAX_BRIGHTNESS_RATIO ||
                    previousBrightness > brightness * MAX_BRIGHTNESS_RATIO;
                bool powerChanged = previousPower > 0 && powerJumped;
                bool toneChanged = previousBrightness > 0 && toneJumped;
                if (powerChanged || toneChanged)
                {
                    stableStart = end + attackFrames;
                }

                previousPower = power;
                previousBrightness = brightness;
                int firstStart = Math.Max(stableStart, end - maxFrames);
                for (int start = firstStart; start <= end - minFrames; start += searchStep)
                {
                    double error = MatchError(samples, format.Channels, headFrame: start, tailFrame: end - fadeFrames,
                        frames: fadeFrames, frameStep: sampleStep);
                    double trimPenalty = 0.02 * (frameCount - end) / searchFrames;
                    double lengthPenalty = 0.02 * (maxFrames - (end - start)) / maxFrames;
                    double score = error + trimPenalty + lengthPenalty;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        startFrame = start;
                        endFrame = end;
                        bestFirstStart = firstStart;
                    }
                }
            }

            if (endFrame == 0)
            {
                return false;
            }

            int coarseStart = startFrame;
            bestScore = MAX_MATCH_ERROR;
            startFrame = 0;
            int firstCandidate = Math.Max(bestFirstStart, coarseStart - searchStep);
            int lastCandidate = Math.Min(coarseStart + searchStep, endFrame - minFrames);
            for (int start = firstCandidate; start <= lastCandidate; start++)
            {
                double error = MatchError(samples, format.Channels, headFrame: start, tailFrame: endFrame - fadeFrames,
                    frames: fadeFrames, frameStep: 1);
                double trimPenalty = 0.02 * (frameCount - endFrame) / searchFrames;
                double lengthPenalty = 0.02 * (maxFrames - (endFrame - start)) / maxFrames;
                double score = error + trimPenalty + lengthPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    startFrame = start;
                }
            }

            return startFrame > 0;
        }

        internal static bool TryFindPeriodic(float[] samples, BassStemFormat format, int firstFrame,
            out int startFrame, out int endFrame, out int fadeFrames)
        {
            int channels = format.Channels;
            int frameCount = samples.Length / channels;
            int minLag = Math.Max(1, format.FramesFor(MIN_PERIOD_SECONDS));
            int maxLag = Math.Min(format.FramesFor(MAX_PERIOD_SECONDS), (frameCount - firstFrame) / 2);
            startFrame = 0;
            endFrame = 0;
            fadeFrames = 0;
            if (maxLag <= minLag)
            {
                return false;
            }

            var correlations = new double[maxLag + 1];
            int firstPeakLag = 0;
            double best = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                correlations[lag] = Correlation(samples, channels, firstFrame, frameCount - lag, lag);
                if (firstPeakLag == 0 && correlations[lag] < 0)
                {
                    firstPeakLag = lag;
                }

                if (firstPeakLag != 0)
                {
                    best = Math.Max(best, correlations[lag]);
                }
            }

            if (best < MIN_PERIODICITY)
            {
                return false;
            }

            int period = firstPeakLag;
            while (correlations[period] < best * PERIOD_PEAK_RATIO ||
                (period < maxLag && correlations[period + 1] > correlations[period]))
            {
                period++;
            }

            int periods = (int) ((frameCount - firstFrame) / (period * 1.25));
            if (periods == 0)
            {
                return false;
            }

            fadeFrames = periods * period / 4;
            int guess = frameCount - fadeFrames - periods * period;
            double bestError = double.MaxValue;
            int first = Math.Max(firstFrame, guess - periods);
            int last = guess + periods;
            for (int start = first; start <= last; start++)
            {
                double error = MatchError(samples, channels, headFrame: start, tailFrame: frameCount - fadeFrames,
                    frames: fadeFrames, frameStep: 1);
                if (error < bestError)
                {
                    bestError = error;
                    startFrame = start;
                }
            }

            endFrame = frameCount;
            return bestError < MAX_MATCH_ERROR;
        }

        private static double Correlation(float[] samples, int channels, int firstFrame, int lastFrame, int lag)
        {
            double product = 0;
            double left = 0;
            double right = 0;
            for (int frame = firstFrame; frame < lastFrame; frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    double head = samples[frame * channels + channel];
                    double tail = samples[(frame + lag) * channels + channel];
                    product += head * tail;
                    left += head * head;
                    right += tail * tail;
                }
            }

            return left > 0 && right > 0 ? product / Math.Sqrt(left * right) : 0;
        }

        private static double MatchError(float[] samples, int channels, int headFrame, int tailFrame, int frames,
            int frameStep)
        {
            double differenceEnergy = 0;
            double energy = 0;
            int head = headFrame * channels;
            int tail = tailFrame * channels;
            for (int frame = 0; frame < frames; frame += frameStep)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    int sample = frame * channels + channel;
                    double left = samples[head + sample];
                    double right = samples[tail + sample];
                    double difference = left - right;
                    differenceEnergy += difference * difference;
                    energy += left * left + right * right;
                }
            }

            int sampleCount = ((frames - 1) / frameStep + 1) * channels;
            return energy > MIN_POWER * sampleCount ? differenceEnergy / energy : 1;
        }
    }
}
