#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemClip
    {
        private const double LOOP_FADE_SECONDS    = 0.02;
        private const double SUSTAIN_LOOP_SECONDS = 0.4;
        private const double BLEED_GUARD_SECONDS  = 0.03;
        private const double MIN_CLEAN_SECONDS     = 0.025;

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
            int onset = BassPickAttack.FindOnset(Samples, format, ChartFrame);
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
            int attackFrames = BassSustainStretch.FindAttackEnd(Samples, format);
            int onsetFrame = BassSustainStretch.FindNextOnset(Samples, format, attackFrames);
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
            int loopStart = BassSustainStretch.FindLoopStart(Samples, format, firstFrame: firstFrame,
                tailFrame: FrameCount - fadeFrames, fadeFrames: fadeFrames);
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
