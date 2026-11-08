using NUnit.Framework;
using YARG.Audio.BASS;

namespace YARG.Tests.EditMode
{
    public class BassStemFormatTests
    {
        private static readonly BassStemFormat FORMAT = new(frequency: 48000, channels: 2);

        [Test]
        public void FramesFor_RoundsToNearestFrame()
        {
            Assert.That(FORMAT.FramesFor(0), Is.EqualTo(0));
            Assert.That(FORMAT.FramesFor(0.5), Is.EqualTo(24000));
            Assert.That(FORMAT.FramesFor(1.0 / 48000), Is.EqualTo(1));
        }

        [Test]
        public void ClipFrames_ClampsToMaxClipLength()
        {
            Assert.That(BassStemClip.FramesFor(FORMAT, 1), Is.EqualTo(48000));
            Assert.That(BassStemClip.FramesFor(FORMAT, double.MaxValue), Is.EqualTo(BassStemClip.MaxFrames(FORMAT)));
        }

        [Test]
        public void MaxClipFrames_IsThreeSeconds()
        {
            Assert.That(BassStemClip.MaxFrames(FORMAT), Is.EqualTo(144000));
        }
    }
}
