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

        internal static bool TryFind(float[] samples, int frequency, int channels, int firstFrame,
            int fadeFrames, out int startFrame, out int endFrame)
        {
            startFrame = 0;
            endFrame = 0;
            int frameCount = samples.Length / channels;
            int blockFrames = (int) Math.Round(BLOCK_SECONDS * frequency);
            int attackFrames = (int) Math.Round(ATTACK_SECONDS * frequency);
            int minFrames = (int) Math.Round(MIN_LOOP_SECONDS * frequency) + fadeFrames;
            int maxFrames = (int) Math.Round(MAX_LOOP_SECONDS * frequency) + fadeFrames;
            int searchFrames = (int) Math.Round(SEARCH_SECONDS * frequency);
            int searchStep = Math.Max(1, frequency / 1000);
            int sampleStep = Math.Max(1, frequency / 8000);
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
                    for (int channel = 0; channel < channels; channel++)
                    {
                        int index = frame * channels + channel;
                        double sample = samples[index];
                        double difference = sample - samples[index - channels];
                        energy += sample * sample;
                        changes += difference * difference;
                    }
                }

                double power = energy / ((blockFrames - 1) * channels);
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

                bool powerChanged = previousPower > 0 &&
                    (power > previousPower * MAX_POWER_RATIO || previousPower > power * MAX_POWER_RATIO);
                bool toneChanged = previousBrightness > 0 &&
                    (brightness > previousBrightness * MAX_BRIGHTNESS_RATIO ||
                        previousBrightness > brightness * MAX_BRIGHTNESS_RATIO);
                if (powerChanged || toneChanged)
                {
                    stableStart = end + attackFrames;
                }

                previousPower = power;
                previousBrightness = brightness;
                int firstStart = Math.Max(stableStart, end - maxFrames);
                for (int start = firstStart; start <= end - minFrames; start += searchStep)
                {
                    double error = MatchError(samples, channels, headFrame: start, tailFrame: end - fadeFrames,
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
            for (int start = Math.Max(bestFirstStart, coarseStart - searchStep);
                start <= Math.Min(coarseStart + searchStep, endFrame - minFrames); start++)
            {
                double error = MatchError(samples, channels, headFrame: start, tailFrame: endFrame - fadeFrames,
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
