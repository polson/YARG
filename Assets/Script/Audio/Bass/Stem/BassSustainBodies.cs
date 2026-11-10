#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal static class BassSustainBodies
    {
        private const double ONSET_SECONDS         = 0.3;
        private const double RATE_HZ               = 4.5;
        private const double CLIP_RATE_SPREAD      = 0.1;
        private const double CYCLE_RATE_SPREAD     = 0.15;
        private const double DEPTH_CENTS           = 60;
        private const double DEPTH_SPREAD          = 0.2;
        private const double DRIFT_CENTS           = 6;
        private const double LOOP_SECONDS          = 2.0;
        private const double SEAM_FADE_SECONDS     = 0.04;
        private const double ONSET_FADE_SECONDS    = 0.01;
        private const double RENDER_MARGIN_SECONDS = 0.1;
        private const int    BLOCK_FRAMES          = 256;

        private static readonly double[] DRIFT_RATES_HZ = { 0.37, 0.71, 1.13 };

        private sealed class Curve
        {
            public int      OnsetFrame;
            public int      BodyStartFrame;
            public int      PeriodFrames;
            public int      LeadInCycles;
            public double[] CycleBounds = Array.Empty<double>();
            public double[] CycleDepths = Array.Empty<double>();
            public double[] DriftCycles = Array.Empty<double>();
            public double[] DriftPhases = Array.Empty<double>();

            public double CentsAt(double frame)
            {
                if (frame < OnsetFrame)
                {
                    return 0;
                }

                double ramp = 1;
                double vibrato;
                if (frame < BodyStartFrame)
                {
                    double phase = (frame - OnsetFrame) / (BodyStartFrame - OnsetFrame) * LeadInCycles;
                    ramp = Math.Min(1, phase);
                    vibrato = CycleDepths[0] * ramp * Bump(phase - Math.Floor(phase));
                }
                else
                {
                    double offset = (frame - BodyStartFrame) % PeriodFrames;
                    int cycle = 0;
                    while (cycle < CycleDepths.Length - 1 && offset >= CycleBounds[cycle + 1])
                    {
                        cycle++;
                    }

                    double length = CycleBounds[cycle + 1] - CycleBounds[cycle];
                    vibrato = CycleDepths[cycle] * Bump((offset - CycleBounds[cycle]) / length);
                }

                double drift = 0;
                for (int index = 0; index < DriftCycles.Length; index++)
                {
                    drift += Math.Sin(2 * Math.PI * DriftCycles[index] * (frame - BodyStartFrame) / PeriodFrames
                        + DriftPhases[index]);
                }

                return vibrato + ramp * DRIFT_CENTS * drift / DriftCycles.Length;
            }

            private static double Bump(double phase)
            {
                return 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase);
            }
        }

        internal static BassStemClip? RenderVibrato(BassStemClip clip, BassStretchRender render, BassStemFormat format,
            int seed)
        {
            if (!clip.HasLoop)
            {
                return null;
            }

            var curve = BuildCurve(clip, format, new Random(seed));
            var body = RenderBody(clip, render, format, curve.BodyStartFrame, curve.PeriodFrames, curve.CentsAt);
            if (body == null)
            {
                return null;
            }

            KeepDryOnset(body.Value.Source, body.Value.Samples, curve.OnsetFrame, format);
            return clip.WithLoopedSamples(body.Value.Samples, curve.BodyStartFrame, format.FramesFor(SEAM_FADE_SECONDS),
                format);
        }

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
                : clip.WithLoopedSamples(body.Value.Samples, bodyStartFrame, format.FramesFor(SEAM_FADE_SECONDS), format);
        }

        private static (float[] Source, float[] Samples)? RenderBody(BassStemClip clip, BassStretchRender render,
            BassStemFormat format, int bodyStartFrame, int periodFrames, Func<double, double> centsAt)
        {
            int channels = format.Channels;
            int frameCount = bodyStartFrame + periodFrames + format.FramesFor(SEAM_FADE_SECONDS);
            var source = clip.Unrolled(frameCount + format.FramesFor(RENDER_MARGIN_SECONDS), channels);
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

            return (source, samples);
        }

        private static int LoopPeriod(BassStemClip clip, BassStemFormat format)
        {
            int loopFrames = clip.LoopEndFrame - clip.LoopStartFrame;
            return loopFrames * Math.Max(1, (int) Math.Round(format.FramesFor(LOOP_SECONDS) / (double) loopFrames));
        }

        private static Curve BuildCurve(BassStemClip clip, BassStemFormat format, Random random)
        {
            double cycleFrames = format.Frequency / (RATE_HZ * (1 + CLIP_RATE_SPREAD * Spread(random)));
            int onsetFrame = format.FramesFor(ONSET_SECONDS);
            int bodyStartFrame = Math.Max(clip.LoopStartFrame, onsetFrame + (int) cycleFrames);
            int periodFrames = LoopPeriod(clip, format);

            int cycles = Math.Max(1, (int) Math.Round(periodFrames / cycleFrames));
            var weights = new double[cycles];
            double totalWeight = 0;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                weights[cycle] = 1 + CYCLE_RATE_SPREAD * Spread(random);
                totalWeight += weights[cycle];
            }

            var bounds = new double[cycles + 1];
            var depths = new double[cycles];
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                bounds[cycle + 1] = bounds[cycle] + periodFrames * weights[cycle] / totalWeight;
                depths[cycle] = DEPTH_CENTS * (1 + DEPTH_SPREAD * Spread(random));
            }

            var driftCycles = new double[DRIFT_RATES_HZ.Length];
            var driftPhases = new double[DRIFT_RATES_HZ.Length];
            for (int index = 0; index < DRIFT_RATES_HZ.Length; index++)
            {
                driftCycles[index] = Math.Max(1, Math.Round(DRIFT_RATES_HZ[index] * periodFrames / format.Frequency));
                driftPhases[index] = random.NextDouble() * 2 * Math.PI;
            }

            return new Curve
            {
                OnsetFrame = onsetFrame,
                BodyStartFrame = bodyStartFrame,
                PeriodFrames = periodFrames,
                LeadInCycles = Math.Max(1, (int) Math.Round((bodyStartFrame - onsetFrame) / cycleFrames)),
                CycleBounds = bounds,
                CycleDepths = depths,
                DriftCycles = driftCycles,
                DriftPhases = driftPhases,
            };
        }

        private static void KeepDryOnset(float[] source, float[] samples, int onsetFrame, BassStemFormat format)
        {
            int channels = format.Channels;
            int fadeFrames = Math.Min(format.FramesFor(ONSET_FADE_SECONDS), onsetFrame);
            int dryFrames = onsetFrame - fadeFrames;
            Array.Copy(source, samples, dryFrames * channels);
            for (int frame = 0; frame < fadeFrames; frame++)
            {
                float blend = (float) (frame + 1) / (fadeFrames + 1);
                for (int channel = 0; channel < channels; channel++)
                {
                    int index = (dryFrames + frame) * channels + channel;
                    samples[index] = source[index] * (1 - blend) + samples[index] * blend;
                }
            }
        }

        private static double Spread(Random random)
        {
            return 2 * random.NextDouble() - 1;
        }
    }
}
