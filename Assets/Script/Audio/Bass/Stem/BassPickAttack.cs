#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal static class BassPickAttack
    {
        internal const double LEAD_SECONDS = 0.05;

        private const double SEARCH_SECONDS    = 0.03;
        private const double DISTANCE_SECONDS  = 0.01;
        private const double BLOCK_SECONDS     = 0.002;
        private const int    HISTORY_BLOCKS    = 4;
        private const double MIN_ONSET_RATIO   = 3;
        private const double PRE_ONSET_SECONDS = 0.003;

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
