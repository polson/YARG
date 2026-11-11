#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal static class BassSustainBodies
    {
        private const double LOOP_SECONDS          = 2.0;
        private const double SEAM_FADE_SECONDS     = 0.04;
        private const double RENDER_MARGIN_SECONDS = 0.1;
        private const int    BLOCK_FRAMES          = 256;

        internal static BassStemClip? RenderBent(BassStemClip clip, BassStretchRender render, BassStemFormat format,
            int semitones)
        {
            if (!clip.HasLoop)
            {
                return null;
            }

            int bodyStartFrame = clip.LoopStartFrame;
            var body = RenderBody(clip, render, format, bodyStartFrame, LoopPeriod(clip, format), _ => semitones * 100.0);
            return body == null
                ? null
                : clip.WithLoopedSamples(body, bodyStartFrame, format.FramesFor(SEAM_FADE_SECONDS), format);
        }

        private static float[]? RenderBody(BassStemClip clip, BassStretchRender render,
            BassStemFormat format, int bodyStartFrame, int periodFrames, Func<double, double> centsAt)
        {
            int channels = format.Channels;
            int frameCount = bodyStartFrame + periodFrames + format.FramesFor(SEAM_FADE_SECONDS);
            var source = clip.Unrolled(frameCount + format.FramesFor(RENDER_MARGIN_SECONDS), channels);
            if (BassPsola.Enabled && clip.PitchHz > 0)
            {
                var shifted = BassPsola.Shift(source, channels, frameCount, format.Frequency / clip.PitchHz,
                    clip.AttackEndFrame, dryFrames: 0, frame => Math.Pow(2, centsAt(frame) / 1200));
                if (shifted != null)
                {
                    return shifted;
                }
            }

            var samples = new float[frameCount * channels];
            var block = new float[BLOCK_FRAMES * channels];
            float baseHz = (float) Math.Max(clip.PitchHz, 0);
            for (int frame = 0; frame < frameCount; frame += BLOCK_FRAMES)
            {
                int frames = Math.Min(BLOCK_FRAMES, frameCount - frame);
                if (frames < BLOCK_FRAMES)
                {
                    block = new float[frames * channels];
                }

                float factor = (float) Math.Pow(2, centsAt(frame + frames / 2.0) / 1200);
                if (!render.SetPitch(factor, baseHz))
                {
                    return null;
                }

                if (frame == 0 && !render.Begin(source, channels, 0))
                {
                    return null;
                }

                if (!render.Read(source, channels, frame, block))
                {
                    return null;
                }

                Array.Copy(block, 0, samples, frame * channels, frames * channels);
            }

            return samples;
        }

        private static int LoopPeriod(BassStemClip clip, BassStemFormat format)
        {
            int loopFrames = clip.LoopEndFrame - clip.LoopStartFrame;
            return loopFrames * Math.Max(1, (int) Math.Round(format.FramesFor(LOOP_SECONDS) / (double) loopFrames));
        }
    }
}
