using NUnit.Framework;
using YARG.Audio.BASS;

namespace YARG.Tests.EditMode
{
    public class BassStemFretMaskTests
    {
        private const int ALL_SIX_FRETS = 63;

        [Test]
        public void Count_CountsSingleFrets()
        {
            Assert.That(BassStemFretMask.Count(1), Is.EqualTo(1));
            Assert.That(BassStemFretMask.Count(32), Is.EqualTo(1));
        }

        [Test]
        public void Count_CountsSixFretChords()
        {
            Assert.That(BassStemFretMask.Count(ALL_SIX_FRETS), Is.EqualTo(6));
        }

        [Test]
        public void Count_IgnoresOpenMask()
        {
            Assert.That(BassStemFretMask.Count(64), Is.EqualTo(0));
        }

        [Test]
        public void Highest_ReturnsSixthFret()
        {
            Assert.That(BassStemFretMask.Highest(ALL_SIX_FRETS, 5), Is.EqualTo(32));
        }

        [Test]
        public void Highest_RespectsHighestFret()
        {
            Assert.That(BassStemFretMask.Highest(ALL_SIX_FRETS, 4), Is.EqualTo(16));
            Assert.That(BassStemFretMask.Highest(ALL_SIX_FRETS, 2), Is.EqualTo(4));
        }

        [Test]
        public void Highest_ReturnsZeroWhenEmpty()
        {
            Assert.That(BassStemFretMask.Highest(0, 5), Is.EqualTo(0));
            Assert.That(BassStemFretMask.Highest(64, 5), Is.EqualTo(0));
        }
    }
}
