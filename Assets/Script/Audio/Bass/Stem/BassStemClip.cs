#nullable enable
using System;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemClip
    {
        private const double MAX_CLIP_SECONDS      = 3.0;
        private const double START_FADE_SECONDS    = 0.004;
        private const double LOOP_FADE_SECONDS     = 0.02;
        private const double SUSTAIN_LOOP_SECONDS  = 0.4;
        private const double FALLBACK_LOOP_SECONDS = 0.15;

        public float[] Samples = Array.Empty<float>();
        public int     FrameCount;
        public bool    IsSustain;
        public int     LoopStartFrame;
        public int     LoopEndFrame;
        public int     LoopFadeFrames;
        public float[] LoopTail = Array.Empty<float>();

        public bool HasLoop => LoopEndFrame > LoopStartFrame;

        public static int MaxFrames(BassStemFormat format)
        {
            return format.FramesFor(MAX_CLIP_SECONDS);
        }

        public static int FramesFor(BassStemFormat format, double seconds)
        {
            return (int) Math.Min(Math.Round(seconds * format.Frequency), MaxFrames(format));
        }

        public void ApplyStartFade(BassStemFormat format)
        {
            int fadeFrames = Math.Min(format.FramesFor(START_FADE_SECONDS), FrameCount);
            for (int frame = 0; frame < fadeFrames; frame++)
            {
                float gain = (float) frame / fadeFrames;
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    Samples[frame * format.Channels + channel] *= gain;
                }
            }
        }

        public bool PrepareLoop(BassStemFormat format)
        {
            int fadeFrames = Math.Min(format.FramesFor(LOOP_FADE_SECONDS), FrameCount / 4);
            bool cleanLoop = BassSustainLoop.TryFind(Samples, format, firstFrame: 0,
                fadeFrames: fadeFrames, out int bodyStart, out int loopEnd);
            if (!cleanLoop)
            {
                YargLogger.LogFormatInfo("[LoopDebug] fallback: clip {0} ms", FrameCount * 1000.0 / format.Frequency);
                int tailFrames = format.FramesFor(FALLBACK_LOOP_SECONDS);
                bodyStart = Math.Max(FrameCount / 2, FrameCount - tailFrames);
                bodyStart = Math.Min(bodyStart, FrameCount - fadeFrames * 2);
                loopEnd = FrameCount;
            }

            SetLoop(bodyStart: bodyStart, loopEnd: loopEnd, fadeFrames: fadeFrames, format: format);
            return cleanLoop;
        }

        public void LoopStretchedBody(int bodyStartFrame, BassStemFormat format)
        {
            int fadeFrames = Math.Min(format.FramesFor(LOOP_FADE_SECONDS), (FrameCount - bodyStartFrame) / 4);
            int firstFrame = Math.Max(bodyStartFrame, FrameCount - format.FramesFor(SUSTAIN_LOOP_SECONDS));
            int loopStart = BassSustainStretch.FindLoopStart(Samples, format, firstFrame: firstFrame,
                tailFrame: FrameCount - fadeFrames, fadeFrames: fadeFrames);
            SetLoop(bodyStart: loopStart, loopEnd: FrameCount, fadeFrames: fadeFrames, format: format);
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
