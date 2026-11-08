#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using YARG.Audio.BASS;

namespace YARG.Tests.EditMode
{
    public class BassSoloStretchTests
    {
        private static readonly BassStemFormat FORMAT = new(frequency: 48000, channels: 2);

        private sealed class FakeStretchRender : IBassStretchRender
        {
            public readonly List<long> Reads = new();
            public long BeginFrame { get; private set; }
            public bool IsDisposed { get; private set; }

            public bool Begin(float[] input, int channels, long startFrame)
            {
                BeginFrame = startFrame;
                return true;
            }

            public bool Read(float[] input, int channels, long outputFrame, float[] output)
            {
                Reads.Add(outputFrame);
                for (int sample = 0; sample < output.Length; sample++)
                {
                    output[sample] = 1f;
                }

                return true;
            }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }

        [Test]
        public void Render_MapsTargetsToStretchedOutputFrames()
        {
            var renders = new List<FakeStretchRender>();
            BassStretchRenderFactory factory = (channels, frequency, speed) =>
            {
                var render = new FakeStretchRender();
                renders.Add(render);
                return render;
            };
            var region = new float[FORMAT.FramesFor(1.0) * FORMAT.Channels];
            var first = new BassSoloStretchTarget(bodyStartFrame: 4800, bodyFrames: 2000);
            var second = new BassSoloStretchTarget(bodyStartFrame: 10000, bodyFrames: 4000);

            BassSoloStretch.Render(region, FORMAT, new List<BassSoloStretchTarget> { first, second },
                CancellationToken.None, factory);

            int firstOutputFrames = (int) Math.Round(2000 / (double) BassSoloStretch.SPEED);
            int secondOutputFrames = (int) Math.Round(4000 / (double) BassSoloStretch.SPEED);

            Assert.That(renders.Count, Is.EqualTo(1));
            Assert.That(renders[0].BeginFrame, Is.EqualTo(4800 - FORMAT.FramesFor(0.05)));
            Assert.That(renders[0].Reads, Is.EqualTo(new[] { 9600L, 30400L }));
            Assert.That(renders[0].IsDisposed, Is.True);

            Assert.That(first.Output, Is.Not.Null);
            Assert.That(first.Output!.Length, Is.EqualTo(firstOutputFrames * FORMAT.Channels));
            Assert.That(second.Output, Is.Not.Null);
            Assert.That(second.Output!.Length, Is.EqualTo(secondOutputFrames * FORMAT.Channels));
        }

        [Test]
        public void Render_LeavesOutputsNullWhenRendererIsUnavailable()
        {
            var region = new float[FORMAT.FramesFor(1.0) * FORMAT.Channels];
            var target = new BassSoloStretchTarget(bodyStartFrame: 4800, bodyFrames: 2000);

            BassSoloStretch.Render(region, FORMAT, new List<BassSoloStretchTarget> { target },
                CancellationToken.None, (channels, frequency, speed) => null);

            Assert.That(target.Output, Is.Null);
        }
    }
}
