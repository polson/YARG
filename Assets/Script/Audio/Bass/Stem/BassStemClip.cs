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
        private const double BLEED_GUARD_SECONDS   = 0.03;
        private const double MIN_CLEAN_SECONDS     = 0.025;
        private const double SHORT_BODY_SECONDS    = 0.1;

        public double  SongSeconds;
        public long    StartFrame;
        public float[] Samples = Array.Empty<float>();
        public int     FrameCount;
        public bool    IsSustain;
        public int     AttackEndFrame;
        public double  PitchHz;
        public int     Note = -1;
        public int     LoopStartFrame;
        public int     LoopEndFrame;
        public int     LoopFadeFrames;
        public float[] LoopTail = Array.Empty<float>();
        public BassStemClip? Vibrato;

        public bool HasLoop => LoopEndFrame > LoopStartFrame;

        public static int MaxFrames(BassStemFormat format)
        {
            return format.FramesFor(MAX_CLIP_SECONDS);
        }

        public static int FramesFor(BassStemFormat format, double seconds)
        {
            return (int) Math.Min(Math.Round(seconds * format.Frequency), MaxFrames(format));
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
                int attackFrames = BassSustainStretch.FindAttackEnd(Samples, format);
                bool shortBody = FrameCount - attackFrames < format.FramesFor(SHORT_BODY_SECONDS);
                if (shortBody && BassSustainLoop.TryFindPeriodic(Samples, format, attackFrames,
                    out bodyStart, out loopEnd, out int periodicFadeFrames))
                {
                    YargLogger.LogFormatInfo("[LoopDebug] periodic: loop {0} ms",
                        (loopEnd - bodyStart - periodicFadeFrames) * 1000.0 / format.Frequency);
                    SetLoop(bodyStart: bodyStart, loopEnd: loopEnd, fadeFrames: periodicFadeFrames, format: format);
                    return true;
                }

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

        public BassStemClip Retuned(int endFrame, double targetHz, BassStemFormat format)
        {
            int channels = format.Channels;
            double step = targetHz / PitchHz;
            var samples = new float[((int) (endFrame / step) + 2) * channels];
            var mapped = new int[endFrame + 1];
            int nextSource = 0;
            int frameCount = 0;
            double position = 0;
            while (position < endFrame - 2)
            {
                int index = (int) position;
                while (nextSource <= index)
                {
                    mapped[nextSource++] = frameCount;
                }

                float fraction = (float) (position - index);
                int before = Math.Max(index - 1, 0);
                int after = Math.Min(index + 2, FrameCount - 1);
                for (int channel = 0; channel < channels; channel++)
                {
                    float y0 = Samples[before * channels + channel];
                    float y1 = Samples[index * channels + channel];
                    float y2 = Samples[(index + 1) * channels + channel];
                    float y3 = Samples[after * channels + channel];
                    float c1 = 0.5f * (y2 - y0);
                    float c2 = y0 - 2.5f * y1 + 2 * y2 - 0.5f * y3;
                    float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
                    samples[frameCount * channels + channel] = ((c3 * fraction + c2) * fraction + c1) * fraction + y1;
                }

                frameCount++;
                position += step;
            }

            while (nextSource <= endFrame)
            {
                mapped[nextSource++] = frameCount;
            }

            Array.Resize(ref samples, frameCount * channels);
            return WithRetunedSamples(samples, frameCount, mapped, endFrame, targetHz, format);
        }

        public BassStemClip? StretchRetuned(BassStretchRender render, int endFrame, double targetHz,
            BassStemFormat format)
        {
            if (endFrame <= 0)
            {
                return null;
            }

            int channels = format.Channels;
            var samples = new float[endFrame * channels];
            if (!render.SetPitch((float) (targetHz / PitchHz), (float) PitchHz)
                || !render.Begin(Samples, channels, 0)
                || !render.Read(Samples, channels, 0, samples))
            {
                return null;
            }

            var mapped = new int[endFrame + 1];
            for (int frame = 0; frame <= endFrame; frame++)
            {
                mapped[frame] = frame;
            }

            return WithRetunedSamples(samples, endFrame, mapped, endFrame, targetHz, format);
        }

        private BassStemClip WithRetunedSamples(float[] samples, int frameCount, int[] mapped, int endFrame,
            double targetHz, BassStemFormat format)
        {
            var retuned = new BassStemClip
            {
                SongSeconds = SongSeconds,
                StartFrame = StartFrame,
                Samples = samples,
                FrameCount = frameCount,
                IsSustain = IsSustain,
                AttackEndFrame = mapped[Math.Min(AttackEndFrame, endFrame)],
                PitchHz = targetHz,
            };
            if (HasLoop && LoopEndFrame <= endFrame)
            {
                int bodyStart = mapped[LoopStartFrame - LoopFadeFrames];
                int fadeFrames = mapped[LoopStartFrame] - bodyStart;
                int loopEnd = Math.Min(mapped[LoopEndFrame], frameCount);
                if (fadeFrames > 0 && loopEnd - bodyStart > fadeFrames * 2)
                {
                    retuned.SetLoop(bodyStart: bodyStart, loopEnd: loopEnd, fadeFrames: fadeFrames, format: format);
                }
            }

            return retuned;
        }

        public bool LockLoopToPeriod(double periodFrames, BassStemFormat format)
        {
            double currentError = HasLoop
                ? BassSustainLoop.MatchError(Samples, format.Channels, headFrame: LoopStartFrame - LoopFadeFrames,
                    tailFrame: LoopEndFrame - LoopFadeFrames, frames: LoopFadeFrames, frameStep: 1)
                : double.MaxValue;
            bool found = BassSustainLoop.TryFindPitched(Samples, format, AttackEndFrame, periodFrames,
                out int bodyStart, out int loopEnd, out int fadeFrames, out double error);
            YargLogger.LogFormatInfo("[LoopDebug] pitch lock: current seam {0}, pitched seam {1}, clip {2} ms",
                Math.Round(currentError, 4), Math.Round(error, 4), Math.Round(FrameCount * 1000.0 / format.Frequency));
            if (!found || error >= currentError)
            {
                return false;
            }

            SetLoop(bodyStart: bodyStart, loopEnd: loopEnd, fadeFrames: fadeFrames, format: format);
            return true;
        }

        public float[] Unrolled(int frameCount, int channels)
        {
            var samples = new float[frameCount * channels];
            int pushedFrames = 0;
            int written = 0;
            while (written < frameCount)
            {
                int frames = NextLoopSegment(pushedFrames, frameCount - written, out bool fromTail, out int sourceFrame);
                Array.Copy(fromTail ? LoopTail : Samples, sourceFrame * channels, samples, written * channels,
                    frames * channels);
                written += frames;
                pushedFrames += frames;
                if (pushedFrames == LoopEndFrame)
                {
                    pushedFrames = LoopStartFrame;
                }
            }

            return samples;
        }

        public BassStemClip WithLoopedSamples(float[] samples, int bodyStart, int fadeFrames, BassStemFormat format)
        {
            var looped = new BassStemClip
            {
                SongSeconds = SongSeconds,
                StartFrame = StartFrame,
                Samples = samples,
                FrameCount = samples.Length / format.Channels,
                IsSustain = IsSustain,
                AttackEndFrame = AttackEndFrame,
                PitchHz = PitchHz,
                Note = Note,
            };
            looped.SetLoop(bodyStart: bodyStart, loopEnd: looped.FrameCount, fadeFrames: fadeFrames, format: format);
            return looped;
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
