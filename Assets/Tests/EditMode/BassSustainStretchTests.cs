using System;
using NUnit.Framework;
using YARG.Audio.BASS;

namespace YARG.Tests.EditMode
{
    public class BassSustainStretchTests
    {
        private static readonly BassStemFormat FORMAT = new(frequency: 48000, channels: 1);

        private static float[] DecayingTone(double seconds, double hz)
        {
            var samples = new float[FORMAT.FramesFor(seconds)];
            for (int frame = 0; frame < samples.Length; frame++)
            {
                double time = frame / (double) FORMAT.Frequency;
                samples[frame] = (float) (Math.Sin(2 * Math.PI * hz * time) * Math.Exp(-time * 6));
            }

            return samples;
        }

        [Test]
        public void TryRender_RejectsClipShorterThanMinimumCapture()
        {
            var samples = new float[FORMAT.FramesFor(0.01)];

            bool rendered = BassSustainStretch.TryRender(samples, FORMAT, out _, out _);

            Assert.That(rendered, Is.False);
        }

        [Test]
        public void TryRender_RejectsClipLongerThanMaximumCapture()
        {
            var samples = new float[FORMAT.FramesFor(0.5)];

            bool rendered = BassSustainStretch.TryRender(samples, FORMAT, out _, out _);

            Assert.That(rendered, Is.False);
        }

        [Test]
        public void TryRender_KeepsAttackAndStretchesBody()
        {
            var samples = DecayingTone(seconds: 0.2, hz: 200);
            int attackFrames = BassSustainStretch.FindAttackEnd(samples, FORMAT);

            bool rendered = BassSustainStretch.TryRender(samples, FORMAT, out var output, out int bodyStartFrame);

            Assert.That(rendered, Is.True);
            Assert.That(bodyStartFrame, Is.GreaterThan(attackFrames));
            Assert.That(output.Length, Is.GreaterThan(bodyStartFrame));
            for (int sample = 0; sample < attackFrames; sample++)
            {
                Assert.That(output[sample], Is.EqualTo(samples[sample]));
            }

            foreach (var sample in output)
            {
                Assert.That(float.IsFinite(sample), Is.True);
            }
        }

        [Test]
        public void FindAttackEnd_StaysWithinConfiguredBounds()
        {
            var samples = DecayingTone(seconds: 0.2, hz: 200);

            int attackFrames = BassSustainStretch.FindAttackEnd(samples, FORMAT);

            Assert.That(attackFrames, Is.GreaterThanOrEqualTo(FORMAT.FramesFor(0.015)));
            Assert.That(attackFrames, Is.LessThanOrEqualTo(FORMAT.FramesFor(0.12)));
        }
    }
}
