#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal static class BassPickAttack
    {
        internal const double LEAD_SECONDS = 0.05;
        internal const double STRONG_RATIO = 2;

        private const double SEARCH_SECONDS     = 0.03;
        private const double DISTANCE_SECONDS   = 0.01;
        private const double BLOCK_SECONDS      = 0.002;
        private const int    HISTORY_BLOCKS     = 4;
        private const double MIN_ONSET_RATIO    = 3;
        private const double PRE_ONSET_SECONDS  = 0.003;
        private const double PICK_SECONDS       = 0.01;
        private const double BODY_START_SECONDS = 0.03;
        private const double BODY_END_SECONDS   = 0.08;
        private const double WEAK_RATIO         = 1;
        private const double TARGET_RATIO       = 1.5;
        private const double BORROW_SECONDS     = 0.015;
        private const double BORROW_FADE_RATIO  = 0.4;
        private const double HIGH_PASS_HZ       = 2000;
        private const double MAX_BORROW_GAIN    = 4;

        internal static int FindOnset(float[] samples, BassStemFormat format, int chartFrame)
        {
            int channels = format.Channels;
            int frameCount = samples.Length / channels;
            int blockFrames = Math.Max(1, format.FramesFor(BLOCK_SECONDS));
            int searchFrames = format.FramesFor(SEARCH_SECONDS);
            int firstBlock = Math.Max(HISTORY_BLOCKS, (chartFrame - searchFrames) / blockFrames);
            int lastBlock = Math.Min((chartFrame + searchFrames) / blockFrames, frameCount / blockFrames - 1);
            if (lastBlock < firstBlock)
            {
                return -1;
            }

            var flux = new double[lastBlock + 1];
            for (int block = Math.Max(0, firstBlock - HISTORY_BLOCKS); block <= lastBlock; block++)
            {
                flux[block] = Flux(samples, channels, Math.Max(1, block * blockFrames), blockFrames);
            }

            int onsetBlock = -1;
            double bestScore = 0;
            double distanceBlocks = format.FramesFor(DISTANCE_SECONDS) / (double) blockFrames;
            double chartBlock = chartFrame / (double) blockFrames;
            for (int block = firstBlock; block <= lastBlock; block++)
            {
                double history = 0;
                for (int previous = block - HISTORY_BLOCKS; previous < block; previous++)
                {
                    history += flux[previous];
                }

                history /= HISTORY_BLOCKS;
                double ratio = flux[block] / (history + 1e-12);
                double score = ratio / (1 + Math.Abs(block - chartBlock) / distanceBlocks);
                if (ratio >= MIN_ONSET_RATIO && score > bestScore)
                {
                    bestScore = score;
                    onsetBlock = block;
                }
            }

            return onsetBlock < 0 ? -1 : Math.Max(0, onsetBlock * blockFrames - format.FramesFor(PRE_ONSET_SECONDS));
        }

        internal static double Strength(float[] samples, BassStemFormat format, int onsetFrame)
        {
            int channels = format.Channels;
            int frameCount = samples.Length / channels;
            int pickStart = onsetFrame + format.FramesFor(PRE_ONSET_SECONDS);
            int pickFrames = format.FramesFor(PICK_SECONDS);
            int bodyStart = onsetFrame + format.FramesFor(BODY_START_SECONDS);
            int bodyFrames = format.FramesFor(BODY_END_SECONDS) - format.FramesFor(BODY_START_SECONDS);
            if (bodyStart + bodyFrames > frameCount)
            {
                return 0;
            }

            double pick = Flux(samples, channels, Math.Max(1, pickStart), pickFrames) / pickFrames;
            double body = Flux(samples, channels, bodyStart, bodyFrames) / bodyFrames;
            return body > 0 ? pick / body : 0;
        }

        internal static float[]? ExtractPick(BassStemClip donor, BassStemFormat format)
        {
            int channels = format.Channels;
            int frames = format.FramesFor(PRE_ONSET_SECONDS + BORROW_SECONDS);
            if (donor.PickStrength < TARGET_RATIO || donor.FrameCount < frames)
            {
                return null;
            }

            var pick = new float[frames * channels];
            float smoothing = (float) Math.Exp(-2 * Math.PI * HIGH_PASS_HZ / format.Frequency);
            int fadeFrames = (int) (frames * BORROW_FADE_RATIO);
            for (int channel = 0; channel < channels; channel++)
            {
                float low = donor.Samples[channel];
                for (int frame = 0; frame < frames; frame++)
                {
                    float sample = donor.Samples[frame * channels + channel];
                    low = smoothing * low + (1 - smoothing) * sample;
                    int fromEnd = frames - frame;
                    float fade = fromEnd < fadeFrames ? (float) (0.5 - 0.5 * Math.Cos(Math.PI * fromEnd / fadeFrames)) : 1;
                    pick[frame * channels + channel] = (sample - low) * fade;
                }
            }

            return pick;
        }

        internal static bool Borrow(BassStemClip clip, float[] pick, BassStemFormat format)
        {
            int channels = format.Channels;
            int pickStart = format.FramesFor(PRE_ONSET_SECONDS);
            int pickFrames = format.FramesFor(PICK_SECONDS);
            int bodyStart = format.FramesFor(BODY_START_SECONDS);
            int bodyFrames = format.FramesFor(BODY_END_SECONDS) - bodyStart;
            int borrowFrames = pick.Length / channels;
            if (clip.PickStrength >= WEAK_RATIO || clip.FrameCount < Math.Max(bodyStart + bodyFrames, borrowFrames))
            {
                return false;
            }

            double pickRate = Flux(clip.Samples, channels, pickStart, pickFrames) / pickFrames;
            double bodyRate = Flux(clip.Samples, channels, bodyStart, bodyFrames) / bodyFrames;
            double donorRate = Flux(pick, channels, pickStart, pickFrames) / pickFrames;
            double missing = TARGET_RATIO * bodyRate - pickRate;
            if (missing <= 0 || donorRate <= 0)
            {
                return false;
            }

            float gain = (float) Math.Min(Math.Sqrt(missing / donorRate), MAX_BORROW_GAIN);
            for (int index = 0; index < pick.Length; index++)
            {
                clip.Samples[index] += gain * pick[index];
            }

            return true;
        }

        private static double Flux(float[] samples, int channels, int firstFrame, int frames)
        {
            int end = Math.Min(firstFrame + frames, samples.Length / channels);
            double flux = 0;
            for (int sample = firstFrame * channels; sample < end * channels; sample++)
            {
                double difference = samples[sample] - samples[sample - channels];
                flux += difference * difference;
            }

            return flux;
        }
    }
}
