#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemClip
    {
        internal const double ONSET_LEAD_SECONDS = 0.05;

        private const double LOOP_FADE_SECONDS            = 0.02;
        private const double SUSTAIN_LOOP_SECONDS         = 0.4;
        private const double BLEED_GUARD_SECONDS          = 0.03;
        private const double MIN_CLEAN_SECONDS            = 0.025;

        private const double ONSET_SEARCH_SECONDS         = 0.03;
        private const double ONSET_DISTANCE_SECONDS       = 0.01;
        private const double ONSET_BLOCK_SECONDS          = 0.002;
        private const int    ONSET_HISTORY_BLOCKS         = 4;
        private const double ONSET_MIN_RATIO              = 3;
        private const double ONSET_PRE_SECONDS            = 0.003;

        private const double DECAY_SETTLE_SECONDS         = 0.03;
        private const double DECAY_WINDOW_SECONDS         = 0.02;
        private const double DECAY_MIN_SPAN_SECONDS       = 0.25;

        private const double ATTACK_MIN_SECONDS           = 0.015;
        private const double ATTACK_MAX_SECONDS           = 0.12;
        private const double ATTACK_BLOCK_SECONDS         = 0.005;
        private const double ATTACK_STABLE_POWER_RATIO    = 1.3;
        private const double LOOP_SEARCH_SECONDS          = 0.015;
        private const double NEXT_ONSET_BLOCK_SECONDS     = 0.003;
        private const double NEXT_ONSET_RATIO             = 3;
        private const double NEXT_ONSET_FLOOR_RATIO       = 0.01;
        private const double NEXT_ONSET_SMOOTHING         = 0.2;

        public double  SongSeconds;
        public long    StartFrame;
        public float[] Samples = Array.Empty<float>();
        public int     FrameCount;
        public bool    IsSustain;
        public int     AttackEndFrame;
        public int     Note = -1;
        public int     LoopStartFrame;
        public int     LoopEndFrame;
        public int     LoopFadeFrames;
        public float[] LoopTail = Array.Empty<float>();
        public int     ChartFrame;
        public double  DecayDbPerSecond;
        public BassStemClip[] Takes = Array.Empty<BassStemClip>();
        public BassStemClip[] Layers = Array.Empty<BassStemClip>();
        public BassStemClip[] Releases = Array.Empty<BassStemClip>();
        public double  PlaybackRate = 1;

        public bool HasLoop => LoopEndFrame > LoopStartFrame;

        public void AlignToOnset(BassStemFormat format)
        {
            int onset = FindOnset(format, ChartFrame);
            int start = Math.Min(onset >= 0 ? onset : ChartFrame, FrameCount - 1);
            if (start > 0)
            {
                int channels = format.Channels;
                var samples = new float[(FrameCount - start) * channels];
                Array.Copy(Samples, start * channels, samples, 0, samples.Length);
                Samples = samples;
                FrameCount -= start;
                StartFrame += start;
            }

            ChartFrame = 0;
        }

        public void TrimToCleanEnd(BassStemFormat format)
        {
            int attackFrames = MeasureAttackEnd(format);
            int onsetFrame = FindNextOnset(format, attackFrames);
            int cleanEnd = Math.Min(onsetFrame, FrameCount - format.FramesFor(BLEED_GUARD_SECONDS));
            cleanEnd = Math.Max(cleanEnd, Math.Min(format.FramesFor(MIN_CLEAN_SECONDS), FrameCount));
            if (cleanEnd >= FrameCount)
            {
                return;
            }

            Array.Resize(ref Samples, cleanEnd * format.Channels);
            FrameCount = cleanEnd;
        }

        public void LoopStretchedBody(int bodyStartFrame, BassStemFormat format)
        {
            int fadeFrames = Math.Min(format.FramesFor(LOOP_FADE_SECONDS), (FrameCount - bodyStartFrame) / 4);
            int firstFrame = Math.Max(bodyStartFrame, FrameCount - format.FramesFor(SUSTAIN_LOOP_SECONDS));
            int loopStart = FindLoopStart(format, firstFrame: firstFrame, tailFrame: FrameCount - fadeFrames,
                fadeFrames: fadeFrames);
            SetLoop(bodyStart: loopStart, loopEnd: FrameCount, fadeFrames: fadeFrames, format: format);
        }

        public BassStemClip PlayedAt(int note, double playbackRate)
        {
            return new BassStemClip
            {
                SongSeconds = SongSeconds,
                StartFrame = StartFrame,
                Samples = Samples,
                FrameCount = FrameCount,
                IsSustain = IsSustain,
                AttackEndFrame = AttackEndFrame,
                Note = note,
                LoopStartFrame = LoopStartFrame,
                LoopEndFrame = LoopEndFrame,
                LoopFadeFrames = LoopFadeFrames,
                LoopTail = LoopTail,
                PlaybackRate = playbackRate,
            };
        }

        public int NextLoopSegment(int pushedFrames, int remaining, out bool fromTail, out int sourceFrame)
        {
            int fadeStart = LoopEndFrame - LoopFadeFrames;
            if (pushedFrames < fadeStart)
            {
                fromTail = false;
                sourceFrame = pushedFrames;
                return Math.Min(remaining, fadeStart - pushedFrames);
            }

            fromTail = true;
            sourceFrame = pushedFrames - fadeStart;
            return Math.Min(remaining, LoopEndFrame - pushedFrames);
        }

        internal int MeasureAttackEnd(BassStemFormat format)
        {
            int channels = format.Channels;
            int frameCount = Samples.Length / channels;
            int blockFrames = format.FramesFor(ATTACK_BLOCK_SECONDS);
            int minAttackFrames = format.FramesFor(ATTACK_MIN_SECONDS);
            int maxAttackFrames = Math.Min(format.FramesFor(ATTACK_MAX_SECONDS), frameCount * 2 / 3);
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
                        double sample = Samples[frame * channels + channel];
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
                bool stable = powers[block] <= powers[block - 1] * ATTACK_STABLE_POWER_RATIO &&
                    powers[block - 1] <= powers[block] * ATTACK_STABLE_POWER_RATIO;
                stableBlocks = stable ? stableBlocks + 1 : 0;
                int endFrame = (block + 1) * blockFrames;
                if (stableBlocks >= 2 && endFrame >= minAttackFrames)
                {
                    return endFrame;
                }
            }

            return maxAttackFrames;
        }

        internal double MeasureDecay(BassStemFormat format)
        {
            int channels = format.Channels;
            int frameCount = Samples.Length / channels;
            int windowFrames = format.FramesFor(DECAY_WINDOW_SECONDS);
            int firstFrame = AttackEndFrame + format.FramesFor(DECAY_SETTLE_SECONDS);
            int windows = (frameCount - firstFrame) / windowFrames;
            if (windowFrames <= 0 || windows * windowFrames < format.FramesFor(DECAY_MIN_SPAN_SECONDS))
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
                    energy += (double) Samples[sample] * Samples[sample];
                }

                if (energy <= 0)
                {
                    return double.NaN;
                }

                double time = window * DECAY_WINDOW_SECONDS;
                double level = 10 * Math.Log10(energy / (windowFrames * channels));
                sumTime += time;
                sumLevel += level;
                sumTimeLevel += time * level;
                sumTimeSquared += time * time;
            }

            double spread = windows * sumTimeSquared - sumTime * sumTime;
            return spread > 0 ? -(windows * sumTimeLevel - sumTime * sumLevel) / spread : double.NaN;
        }

        private int FindOnset(BassStemFormat format, int chartFrame)
        {
            int channels = format.Channels;
            int frameCount = Samples.Length / channels;
            int blockFrames = Math.Max(1, format.FramesFor(ONSET_BLOCK_SECONDS));
            int searchFrames = format.FramesFor(ONSET_SEARCH_SECONDS);
            int firstBlock = Math.Max(ONSET_HISTORY_BLOCKS, (chartFrame - searchFrames) / blockFrames);
            int lastBlock = Math.Min((chartFrame + searchFrames) / blockFrames, frameCount / blockFrames - 1);
            if (lastBlock < firstBlock)
            {
                return -1;
            }

            var flux = new double[lastBlock + 1];
            for (int block = Math.Max(0, firstBlock - ONSET_HISTORY_BLOCKS); block <= lastBlock; block++)
            {
                flux[block] = Flux(channels, Math.Max(1, block * blockFrames), blockFrames);
            }

            int onsetBlock = -1;
            double bestScore = 0;
            double distanceBlocks = format.FramesFor(ONSET_DISTANCE_SECONDS) / (double) blockFrames;
            double chartBlock = chartFrame / (double) blockFrames;
            for (int block = firstBlock; block <= lastBlock; block++)
            {
                double history = 0;
                for (int previous = block - ONSET_HISTORY_BLOCKS; previous < block; previous++)
                {
                    history += flux[previous];
                }

                history /= ONSET_HISTORY_BLOCKS;
                double ratio = flux[block] / (history + 1e-12);
                double score = ratio / (1 + Math.Abs(block - chartBlock) / distanceBlocks);
                if (ratio >= ONSET_MIN_RATIO && score > bestScore)
                {
                    bestScore = score;
                    onsetBlock = block;
                }
            }

            return onsetBlock < 0 ? -1 : Math.Max(0, onsetBlock * blockFrames - format.FramesFor(ONSET_PRE_SECONDS));
        }

        private double Flux(int channels, int firstFrame, int frames)
        {
            int end = Math.Min(firstFrame + frames, Samples.Length / channels);
            double flux = 0;
            for (int sample = firstFrame * channels; sample < end * channels; sample++)
            {
                double difference = Samples[sample] - Samples[sample - channels];
                flux += difference * difference;
            }

            return flux;
        }

        private int FindNextOnset(BassStemFormat format, int firstFrame)
        {
            int channels = format.Channels;
            int frameCount = Samples.Length / channels;
            int blockFrames = format.FramesFor(NEXT_ONSET_BLOCK_SECONDS);
            double reference = 0;
            double peak = 0;
            for (int start = 1; start + blockFrames <= frameCount; start += blockFrames)
            {
                double flux = 0;
                for (int sample = start * channels; sample < (start + blockFrames) * channels; sample++)
                {
                    double difference = Samples[sample] - Samples[sample - channels];
                    flux += difference * difference;
                }

                bool rose = flux > reference * NEXT_ONSET_RATIO && flux > peak * NEXT_ONSET_FLOOR_RATIO;
                if (start >= firstFrame && reference > 0 && rose)
                {
                    return Math.Max(firstFrame, start - blockFrames);
                }

                reference = reference == 0 ? flux : reference + (flux - reference) * NEXT_ONSET_SMOOTHING;
                peak = Math.Max(peak, flux);
            }

            return frameCount;
        }

        private int FindLoopStart(BassStemFormat format, int firstFrame, int tailFrame, int fadeFrames)
        {
            int searchFrames = format.FramesFor(LOOP_SEARCH_SECONDS);
            int lastFrame = Math.Min(firstFrame + searchFrames, tailFrame - fadeFrames);
            int channel = FindLoudestChannel(format.Channels, firstFrame);
            return FindMatch(format.Channels, channel, outputFrame: tailFrame, firstFrame: firstFrame,
                lastFrame: lastFrame, targetFrame: firstFrame, frames: fadeFrames,
                searchStep: Math.Max(1, format.Frequency / 2000), sampleStep: Math.Max(1, format.Frequency / 8000));
        }

        private int FindLoudestChannel(int channels, int firstFrame)
        {
            int loudestChannel = 0;
            double highestEnergy = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                double energy = 0;
                for (int sample = firstFrame * channels + channel; sample < Samples.Length; sample += channels)
                {
                    energy += (double) Samples[sample] * Samples[sample];
                }
                if (energy > highestEnergy)
                {
                    highestEnergy = energy;
                    loudestChannel = channel;
                }
            }
            return loudestChannel;
        }

        private int FindMatch(int channels, int channel, int outputFrame, int firstFrame, int lastFrame,
            int targetFrame, int frames, int searchStep, int sampleStep)
        {
            int bestFrame = targetFrame;
            int searchRange = Math.Max(1, lastFrame - firstFrame);
            double bestScore = double.MaxValue;
            for (int candidate = firstFrame; candidate <= lastFrame; candidate += searchStep)
            {
                double score = MatchError(channels, channel, sourceFrame: candidate, outputFrame: outputFrame,
                    frames: frames, sampleStep: sampleStep);
                score += 0.01 * Math.Abs(candidate - targetFrame) / searchRange;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFrame = candidate;
                }
            }

            int coarseFrame = bestFrame;
            bestScore = double.MaxValue;
            int firstCandidate = Math.Max(firstFrame, coarseFrame - searchStep);
            int lastCandidate = Math.Min(lastFrame, coarseFrame + searchStep);
            for (int candidate = firstCandidate; candidate <= lastCandidate; candidate++)
            {
                double score = MatchError(channels, channel, sourceFrame: candidate, outputFrame: outputFrame,
                    frames: frames, sampleStep: 1);
                score += 0.01 * Math.Abs(candidate - targetFrame) / searchRange;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFrame = candidate;
                }
            }
            return bestFrame;
        }

        private double MatchError(int channels, int channel, int sourceFrame, int outputFrame, int frames,
            int sampleStep)
        {
            double error = 0;
            double energy = 0;
            for (int frame = 0; frame < frames; frame += sampleStep)
            {
                double source = Samples[(sourceFrame + frame) * channels + channel];
                double previous = Samples[(outputFrame + frame) * channels + channel];
                double difference = source - previous;
                error += difference * difference;
                energy += source * source + previous * previous;
            }
            return energy > 0 ? error / energy : 0;
        }

        private void SetLoop(int bodyStart, int loopEnd, int fadeFrames, BassStemFormat format)
        {
            int channels = format.Channels;
            var tail = new float[fadeFrames * channels];
            for (int frame = 0; frame < fadeFrames; frame++)
            {
                float blend = (float) (frame + 1) / fadeFrames;
                for (int channel = 0; channel < channels; channel++)
                {
                    float tailSample = Samples[(loopEnd - fadeFrames + frame) * channels + channel];
                    float headSample = Samples[(bodyStart + frame) * channels + channel];
                    tail[frame * channels + channel] = tailSample * (1 - blend) + headSample * blend;
                }
            }

            LoopStartFrame = bodyStart + fadeFrames;
            LoopEndFrame = loopEnd;
            LoopFadeFrames = fadeFrames;
            LoopTail = tail;
        }
    }
}
