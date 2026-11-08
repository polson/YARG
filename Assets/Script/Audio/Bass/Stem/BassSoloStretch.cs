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

        internal static float[]?[] Render(float[] region, BassStemFormat format,
            List<(long BodyStartFrame, int BodyFrames)> bodies, CancellationToken token,
            BassStretchRenderFactory createRender)
        {
            var outputs = new float[bodies.Count][];
            var order = new List<int>(bodies.Count);
            for (int index = 0; index < bodies.Count; index++)
            {
                order.Add(index);
            }

            order.Sort((left, right) => bodies[left].BodyStartFrame.CompareTo(bodies[right].BodyStartFrame));
            var spans = SplitSpans(order, bodies, format);
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
                RenderSpan(region, format, span, bodies, outputs, token, createRender);
            });
            return outputs;
        }

        private static List<List<int>> SplitSpans(List<int> order, List<(long BodyStartFrame, int BodyFrames)> bodies,
            BassStemFormat format)
        {
            long maxGap = format.FramesFor(MAX_GAP_SECONDS);
            long maxSpan = format.FramesFor(MAX_SPAN_SECONDS);
            var spans = new List<List<int>>();
            long spanStart = 0;
            long spanCoverage = 0;
            foreach (var index in order)
            {
                var body = bodies[index];
                long gapFromCoverage = body.BodyStartFrame - spanCoverage;
                long spanLength = body.BodyStartFrame - spanStart;
                bool gapTooBig = gapFromCoverage > maxGap;
                bool spanTooLong = spanLength > maxSpan;
                bool startsNewSpan = spans.Count == 0 || gapTooBig || spanTooLong;
                if (startsNewSpan)
                {
                    spans.Add(new List<int>());
                    spanStart = body.BodyStartFrame;
                }

                spans[spans.Count - 1].Add(index);
                spanCoverage = Math.Max(spanCoverage, body.BodyStartFrame + body.BodyFrames);
            }
            return spans;
        }

        private static void RenderSpan(float[] region, BassStemFormat format, List<int> span,
            List<(long BodyStartFrame, int BodyFrames)> bodies, float[]?[] outputs, CancellationToken token,
            BassStretchRenderFactory createRender)
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
            foreach (var index in span)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var body = bodies[index];
                long outputFrame = (long) Math.Round((body.BodyStartFrame - startFrame) / (double) SPEED);
                if (!started || outputFrame < nextOutputFrame)
                {
                    started = true;
                    startFrame = Math.Max(0, body.BodyStartFrame - preroll);
                    if (!render.Begin(region, channels, startFrame))
                    {
                        return;
                    }
                    outputFrame = (long) Math.Round((body.BodyStartFrame - startFrame) / (double) SPEED);
                }

                int outputFrames = (int) Math.Round(body.BodyFrames / (double) SPEED);
                var output = new float[outputFrames * channels];
                if (!render.Read(region, channels, outputFrame, output))
                {
                    return;
                }
                outputs[index] = output;
                nextOutputFrame = outputFrame + outputFrames;
            }
        }
    }
}
