using System;
using NUnit.Framework;
using YARG.Audio.BASS;

namespace YARG.Tests.EditMode
{
    public class BassSustainLoopTests
    {
        private static readonly BassStemFormat FORMAT = new(frequency: 48000, channels: 1);

        private static float[] Tone(double seconds, double hz)
        {
            var samples = new float[FORMAT.FramesFor(seconds)];
            for (int frame = 0; frame < samples.Length; frame++)
            {
                samples[frame] = (float) Math.Sin(2 * Math.PI * hz * frame / FORMAT.Frequency);
            }

            return samples;
        }

        [Test]
        public void TryFind_FindsLoopInPeriodicSignal()
        {
            var samples = Tone(seconds: 0.5, hz: 200);
            int fadeFrames = FORMAT.FramesFor(0.002);

            bool found = BassSustainLoop.TryFind(samples, FORMAT, firstFrame: 0, fadeFrames: fadeFrames,
                out int startFrame, out int endFrame);

            Assert.That(found, Is.True);
            Assert.That(startFrame, Is.GreaterThan(0));
            Assert.That(endFrame, Is.GreaterThan(startFrame));
            Assert.That(endFrame, Is.LessThanOrEqualTo(samples.Length));
            Assert.That(endFrame - startFrame, Is.GreaterThanOrEqualTo(FORMAT.FramesFor(0.06) + fadeFrames));
        }

        [Test]
        public void TryFind_RejectsSilence()
        {
            var samples = new float[FORMAT.FramesFor(0.5)];

            bool found = BassSustainLoop.TryFind(samples, FORMAT, firstFrame: 0, fadeFrames: 96,
                out _, out _);

            Assert.That(found, Is.False);
        }

        [Test]
        public void TryFind_RejectsUncorrelatedNoise()
        {
            var random = new Random(1234);
            var samples = new float[FORMAT.FramesFor(0.5)];
            for (int frame = 0; frame < samples.Length; frame++)
            {
                samples[frame] = (float) (random.NextDouble() * 2 - 1);
            }

            bool found = BassSustainLoop.TryFind(samples, FORMAT, firstFrame: 0, fadeFrames: 96,
                out _, out _);

            Assert.That(found, Is.False);
        }
    }
}
