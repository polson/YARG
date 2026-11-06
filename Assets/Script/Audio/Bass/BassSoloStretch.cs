#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YARG.Audio.BASS
{
    internal static class BassSoloStretch
    {
        internal const float SPEED = 0.25f;

        private const double PREROLL_SECONDS  = 0.05;
        private const double MAX_GAP_SECONDS  = 0.5;
        private const double MAX_SPAN_SECONDS = 4.0;

        internal sealed class Target
        {
            public readonly long BodyStartFrame;
            public readonly int  BodyFrames;
            public float[]?      Output;

            public Target(long bodyStartFrame, int bodyFrames)
            {
                BodyStartFrame = bodyStartFrame;
                BodyFrames = bodyFrames;
            }
        }

        internal static void Render(float[] region, int frequency, int channels, List<Target> targets,
            CancellationToken token)
        {
            targets.Sort((left, right) => left.BodyStartFrame.CompareTo(right.BodyStartFrame));
            var spans = SplitSpans(targets, frequency);
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            };
            Parallel.ForEach(spans, options, (span, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                    return;
                }
                RenderSpan(region, frequency, channels, span, token);
            });
        }

        private static List<List<Target>> SplitSpans(List<Target> targets, int frequency)
        {
            long maxGap = (long) Math.Round(MAX_GAP_SECONDS * frequency);
            long maxSpan = (long) Math.Round(MAX_SPAN_SECONDS * frequency);
            var spans = new List<List<Target>>();
            List<Target>? span = null;
            long spanStart = 0;
            long spanEnd = 0;
            foreach (var target in targets)
            {
                if (span == null || target.BodyStartFrame - spanEnd > maxGap ||
                    target.BodyStartFrame - spanStart > maxSpan)
                {
                    span = new List<Target>();
                    spans.Add(span);
                    spanStart = target.BodyStartFrame;
                }
                span.Add(target);
                spanEnd = Math.Max(spanEnd, target.BodyStartFrame + target.BodyFrames);
            }
            return spans;
        }

        private static void RenderSpan(float[] region, int frequency, int channels, List<Target> span,
            CancellationToken token)
        {
            using var render = BassStretchRender.Create(channels, frequency, SPEED);
            if (render == null)
            {
                return;
            }

            long preroll = (long) Math.Round(PREROLL_SECONDS * frequency);
            long startFrame = 0;
            long nextOutputFrame = 0;
            bool started = false;
            foreach (var target in span)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                long outputFrame = (long) Math.Round((target.BodyStartFrame - startFrame) / (double) SPEED);
                if (!started || outputFrame < nextOutputFrame)
                {
                    started = true;
                    startFrame = Math.Max(0, target.BodyStartFrame - preroll);
                    if (!render.Begin(region, channels, startFrame))
                    {
                        return;
                    }
                    outputFrame = (long) Math.Round((target.BodyStartFrame - startFrame) / (double) SPEED);
                }

                int outputFrames = (int) Math.Round(target.BodyFrames / (double) SPEED);
                var output = new float[outputFrames * channels];
                if (!render.Read(region, channels, outputFrame, output))
                {
                    return;
                }
                target.Output = output;
                nextOutputFrame = outputFrame + outputFrames;
            }
        }
    }
}
