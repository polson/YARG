using System;

namespace YARG.Audio.BASS
{
    internal static class BassSustainStretch
    {
        private const double MIN_CAPTURE_SECONDS = 0.025;
        private const double MAX_CAPTURE_SECONDS = 0.4;
        private const double MIN_ATTACK_SECONDS = 0.015;
        private const double MAX_ATTACK_SECONDS = 0.12;
        private const double ANALYSIS_BLOCK_SECONDS = 0.005;
        private const double STABLE_POWER_RATIO = 1.3;
        private const double GRAIN_SECONDS = 0.04;
        private const double SEARCH_SECONDS = 0.015;
        private const double SHORT_BODY_SECONDS = 0.075;
        private const float SHORT_BODY_SPEED = 0.15f;
        private const float BODY_SPEED = 0.25f;

        internal static bool TryRender(float[] samples, int frequency, int channels,
            out float[] rendered, out int bodyStartFrame)
        {
            rendered = Array.Empty<float>();
            bodyStartFrame = 0;
            int frameCount = samples.Length / channels;
            if (frameCount < MIN_CAPTURE_SECONDS * frequency || frameCount > MAX_CAPTURE_SECONDS * frequency)
            {
                return false;
            }

            int attackFrames = FindAttackEnd(samples, frequency, channels);
            int sustainFrames = frameCount - attackFrames;
            float speed = sustainFrames < SHORT_BODY_SECONDS * frequency ? SHORT_BODY_SPEED : BODY_SPEED;
            int bodyFrames = (int) Math.Round(sustainFrames / (double) speed);
            int grainFrames = (int) Math.Round(GRAIN_SECONDS * frequency);
            grainFrames = Math.Min(grainFrames, sustainFrames / 2);
            int overlapFrames = grainFrames / 2;
            int hopFrames = grainFrames - overlapFrames;
            int searchFrames = (int) Math.Round(SEARCH_SECONDS * frequency);
            int searchStep = Math.Max(1, frequency / 2000);
            int sampleStep = Math.Max(1, frequency / 8000);
            int channel = FindLoudestChannel(samples, channels, firstFrame: attackFrames);
            int lastSource = frameCount - grainFrames;
            var fade = new float[overlapFrames];
            for (int frame = 0; frame < overlapFrames; frame++)
            {
                fade[frame] = (float) (0.5 - 0.5 * Math.Cos(Math.PI * (frame + 1) / (overlapFrames + 1)));
            }

            rendered = new float[(attackFrames + bodyFrames) * channels];
            Array.Copy(samples, rendered, (attackFrames + grainFrames) * channels);
            for (int position = attackFrames + hopFrames; position < attackFrames + bodyFrames; position += hopFrames)
            {
                int target = attackFrames + (int) Math.Round((position - attackFrames) * speed);
                target = Math.Min(target, lastSource);
                int first = Math.Max(attackFrames, target - searchFrames);
                int last = Math.Min(lastSource, target + searchFrames);
                int overlap = Math.Min(overlapFrames, attackFrames + bodyFrames - position);
                int source = FindMatch(samples, rendered, channels, channel, outputFrame: position,
                    firstFrame: first, lastFrame: last, targetFrame: target, frames: overlap,
                    searchStep: searchStep, sampleStep: sampleStep);
                int frames = Math.Min(grainFrames, attackFrames + bodyFrames - position);
                for (int frame = 0; frame < frames; frame++)
                {
                    float blend = frame < overlapFrames ? fade[frame] : 1;
                    for (int outputChannel = 0; outputChannel < channels; outputChannel++)
                    {
                        int output = (position + frame) * channels + outputChannel;
                        float sample = samples[(source + frame) * channels + outputChannel];
                        rendered[output] = rendered[output] * (1 - blend) + sample * blend;
                    }
                }
            }

            bodyStartFrame = attackFrames + grainFrames;
            return true;
        }

        internal static int FindLoopStart(float[] samples, int frequency, int channels, int firstFrame,
            int tailFrame, int fadeFrames)
        {
            int searchFrames = (int) Math.Round(SEARCH_SECONDS * frequency);
            int lastFrame = Math.Min(firstFrame + searchFrames, tailFrame - fadeFrames);
            int channel = FindLoudestChannel(samples, channels, firstFrame);
            return FindMatch(samples, samples, channels, channel, outputFrame: tailFrame,
                firstFrame: firstFrame, lastFrame: lastFrame, targetFrame: firstFrame, frames: fadeFrames,
                searchStep: Math.Max(1, frequency / 2000), sampleStep: Math.Max(1, frequency / 8000));
        }

        private static int FindLoudestChannel(float[] samples, int channels, int firstFrame)
        {
            int loudestChannel = 0;
            double highestEnergy = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                double energy = 0;
                for (int sample = firstFrame * channels + channel; sample < samples.Length; sample += channels)
                {
                    energy += (double) samples[sample] * samples[sample];
                }
                if (energy > highestEnergy)
                {
                    highestEnergy = energy;
                    loudestChannel = channel;
                }
            }
            return loudestChannel;
        }

        private static int FindMatch(float[] samples, float[] output, int channels, int channel,
            int outputFrame, int firstFrame, int lastFrame, int targetFrame, int frames, int searchStep, int sampleStep)
        {
            int bestFrame = targetFrame;
            int searchRange = Math.Max(1, lastFrame - firstFrame);
            double bestScore = double.MaxValue;
            for (int candidate = firstFrame; candidate <= lastFrame; candidate += searchStep)
            {
                double score = MatchError(samples, output, channels, channel, sourceFrame: candidate,
                    outputFrame: outputFrame, frames: frames, sampleStep: sampleStep);
                score += 0.01 * Math.Abs(candidate - targetFrame) / searchRange;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFrame = candidate;
                }
            }

            int coarseFrame = bestFrame;
            bestScore = double.MaxValue;
            for (int candidate = Math.Max(firstFrame, coarseFrame - searchStep);
                candidate <= Math.Min(lastFrame, coarseFrame + searchStep); candidate++)
            {
                double score = MatchError(samples, output, channels, channel, sourceFrame: candidate,
                    outputFrame: outputFrame, frames: frames, sampleStep: 1);
                score += 0.01 * Math.Abs(candidate - targetFrame) / searchRange;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFrame = candidate;
                }
            }
            return bestFrame;
        }

        private static double MatchError(float[] samples, float[] output, int channels, int channel,
            int sourceFrame, int outputFrame, int frames, int sampleStep)
        {
            double error = 0;
            double energy = 0;
            for (int frame = 0; frame < frames; frame += sampleStep)
            {
                double source = samples[(sourceFrame + frame) * channels + channel];
                double previous = output[(outputFrame + frame) * channels + channel];
                double difference = source - previous;
                error += difference * difference;
                energy += source * source + previous * previous;
            }
            return energy > 0 ? error / energy : 0;
        }

        private static int FindAttackEnd(float[] samples, int frequency, int channels)
        {
            int frameCount = samples.Length / channels;
            int blockFrames = (int) Math.Round(ANALYSIS_BLOCK_SECONDS * frequency);
            int minAttackFrames = (int) Math.Round(MIN_ATTACK_SECONDS * frequency);
            int maxAttackFrames = (int) Math.Round(MAX_ATTACK_SECONDS * frequency);
            maxAttackFrames = Math.Min(maxAttackFrames, frameCount * 2 / 3);
            int blockCount = maxAttackFrames / blockFrames;
            var powers = new double[blockCount];
            int peakBlock = 0;
            for (int block = 0; block < blockCount; block++)
            {
                double energy = 0;
                for (int frame = block * blockFrames; frame < (block + 1) * blockFrames; frame++)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        double sample = samples[frame * channels + channel];
                        energy += sample * sample;
                    }
                }
                powers[block] = energy;
                if (energy > powers[peakBlock])
                {
                    peakBlock = block;
                }
            }

            int stableBlocks = 0;
            for (int block = peakBlock + 1; block < blockCount; block++)
            {
                bool stable = powers[block] <= powers[block - 1] * STABLE_POWER_RATIO &&
                    powers[block - 1] <= powers[block] * STABLE_POWER_RATIO;
                stableBlocks = stable ? stableBlocks + 1 : 0;
                int endFrame = (block + 1) * blockFrames;
                if (stableBlocks >= 2 && endFrame >= minAttackFrames)
                {
                    return endFrame;
                }
            }

            return maxAttackFrames;
        }
    }
}
