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

        internal static void Render(float[] region, BassStemFormat format, List<BassSoloStretchTarget> targets,
            CancellationToken token, BassStretchRenderFactory createRender)
        {
            targets.Sort((left, right) => left.BodyStartFrame.CompareTo(right.BodyStartFrame));
            var spans = SplitSpans(targets, format);
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
                RenderSpan(region, format, span, token, createRender);
            });
        }

        private static List<List<BassSoloStretchTarget>> SplitSpans(List<BassSoloStretchTarget> targets,
            BassStemFormat format)
        {
            long maxGap = format.FramesFor(MAX_GAP_SECONDS);
            long maxSpan = format.FramesFor(MAX_SPAN_SECONDS);
            var spans = new List<List<BassSoloStretchTarget>>();
            long spanStart = 0;
            long spanCoverage = 0;
            foreach (var target in targets)
            {
                long gapFromCoverage = target.BodyStartFrame - spanCoverage;
                long spanLength = target.BodyStartFrame - spanStart;
                bool gapTooBig = gapFromCoverage > maxGap;
                bool spanTooLong = spanLength > maxSpan;
                bool startsNewSpan = spans.Count == 0 || gapTooBig || spanTooLong;
                if (startsNewSpan)
                {
                    spans.Add(new List<BassSoloStretchTarget>());
                    spanStart = target.BodyStartFrame;
                }

                spans[spans.Count - 1].Add(target);
                spanCoverage = Math.Max(spanCoverage, target.BodyStartFrame + target.BodyFrames);
            }
            return spans;
        }

        private static void RenderSpan(float[] region, BassStemFormat format, List<BassSoloStretchTarget> span,
            CancellationToken token, BassStretchRenderFactory createRender)
        {
            int channels = format.Channels;
            using var render = createRender(channels, format.Frequency, SPEED);
            if (render == null)
            {
                return;
            }

            long preroll = format.FramesFor(PREROLL_SECONDS);
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
