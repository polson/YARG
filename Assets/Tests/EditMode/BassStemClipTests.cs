using System;
using NUnit.Framework;
using YARG.Audio.BASS;

namespace YARG.Tests.EditMode
{
    public class BassStemClipTests
    {
        private static readonly BassStemFormat FORMAT = new(frequency: 48000, channels: 2);

        private static BassStemClip CreateClip(int frames)
        {
            var clip = new BassStemClip
            {
                Samples = new float[frames * FORMAT.Channels],
                FrameCount = frames,
                FilledFrames = frames,
            };
            for (int frame = 0; frame < frames; frame++)
            {
                float sample = (float) Math.Sin(2 * Math.PI * 200 * frame / FORMAT.Frequency);
                for (int channel = 0; channel < FORMAT.Channels; channel++)
                {
                    clip.Samples[frame * FORMAT.Channels + channel] = sample;
                }
            }

            return clip;
        }

        [Test]
        public void ApplyStartFade_RampsUpAndLeavesTheRestUntouched()
        {
            var clip = CreateClip(FORMAT.FramesFor(0.1));
            var original = (float[]) clip.Samples.Clone();

            clip.ApplyStartFade(FORMAT);

            int fadeFrames = FORMAT.FramesFor(0.004);
            Assert.That(clip.Samples[0], Is.EqualTo(0f));
            for (int sample = fadeFrames * FORMAT.Channels; sample < clip.Samples.Length; sample++)
            {
                Assert.That(clip.Samples[sample], Is.EqualTo(original[sample]));
            }
        }

        [Test]
        public void ApplyEndFade_TapersTheTail()
        {
            var clip = CreateClip(FORMAT.FramesFor(0.1));

            clip.ApplyEndFade(firstFrame: 0, frameCount: clip.FrameCount, FORMAT);

            int lastFrame = clip.FrameCount - 1;
            for (int channel = 0; channel < FORMAT.Channels; channel++)
            {
                Assert.That(Math.Abs(clip.Samples[lastFrame * FORMAT.Channels + channel]), Is.LessThan(0.01f));
            }
        }

        [Test]
        public void PrepareLoop_BuildsLoopLongEnoughForTheCrossfade()
        {
            var clip = CreateClip(FORMAT.FramesFor(0.5));

            clip.PrepareLoop(sustainStartFrame: FORMAT.FramesFor(0.1), FORMAT);

            Assert.That(clip.HasLoop, Is.True);
            Assert.That(clip.Loop.EndFrame, Is.EqualTo(clip.FrameCount));
            Assert.That(clip.Loop.FadeFrames, Is.GreaterThan(0));
            Assert.That(clip.Loop.Tail.Length, Is.EqualTo(clip.Loop.FadeFrames * FORMAT.Channels));

            int bodyStart = clip.Loop.StartFrame - clip.Loop.FadeFrames;
            int loopFrames = clip.Loop.EndFrame - bodyStart;
            Assert.That(loopFrames, Is.GreaterThanOrEqualTo(clip.Loop.FadeFrames * 2));
        }

        [Test]
        public void NextLoopSegment_CoversTheLoopWithoutGaps()
        {
            var clip = CreateClip(FORMAT.FramesFor(0.5));
            clip.PrepareLoop(sustainStartFrame: FORMAT.FramesFor(0.1), FORMAT);

            int loopFrames = clip.Loop.EndFrame - clip.Loop.StartFrame;
            int pushed = clip.Loop.StartFrame;
            int fed = 0;
            for (int segment = 0; fed < loopFrames * 4; segment++)
            {
                Assert.That(segment, Is.LessThan(10000));
                int frames = clip.Loop.NextSegment(pushed, remaining: 512, out bool fromTail, out int sourceFrame);
                Assert.That(frames, Is.GreaterThan(0));
                Assert.That(pushed + frames, Is.LessThanOrEqualTo(clip.Loop.EndFrame));

                if (fromTail)
                {
                    Assert.That(sourceFrame, Is.InRange(0, clip.Loop.FadeFrames - 1));
                    Assert.That(sourceFrame + frames, Is.LessThanOrEqualTo(clip.Loop.FadeFrames));
                }
                else
                {
                    Assert.That(sourceFrame, Is.EqualTo(pushed));
                }

                pushed += frames;
                fed += frames;
                if (pushed == clip.Loop.EndFrame)
                {
                    pushed = clip.Loop.StartFrame;
                }
            }
        }
    }
}
