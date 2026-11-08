using System.Collections.Generic;
using NUnit.Framework;
using YARG.Audio.BASS;
using YARG.Core.Engine.Guitar;

namespace YARG.Tests.EditMode
{
    public class BassStemClipPolicyTests
    {
        private sealed class FakeClipSource : IBassStemClipSource
        {
            private readonly Dictionary<int, BassStemClip> _clips = new();

            public void Add(int key)
            {
                _clips[key] = new BassStemClip
                {
                    Samples = new float[2],
                    FrameCount = 1,
                    FilledFrames = 1,
                };
            }

            public bool TryGetClip(int key, out BassStemClip clip)
            {
                return _clips.TryGetValue(key, out clip!) && clip.FrameCount > 0;
            }

            public bool TryGetFretClip(int key, out BassStemClip clip)
            {
                for (int fret = 1; fret <= key; fret <<= 1)
                {
                    if ((key & fret) != 0 && TryGetClip(fret, out clip))
                    {
                        return true;
                    }
                }

                clip = null!;
                return false;
            }

            public bool TryGetAnyClip(out BassStemClip clip)
            {
                foreach (var candidate in _clips.Values)
                {
                    clip = candidate;
                    return true;
                }

                clip = null!;
                return false;
            }
        }

        [Test]
        public void SeedPolicy_FallsBackToAnyClipForSingleFret()
        {
            var source = new FakeClipSource();
            source.Add(2);

            Assert.That(BassStemClipPolicy.TryGetSeedClip(source, key: 1, out _), Is.True);
        }

        [Test]
        public void RecordedPolicy_RejectsSingleFretFallback()
        {
            var source = new FakeClipSource();
            source.Add(2);

            Assert.That(BassStemClipPolicy.TryGetRecordedClip(source, key: 1, out _), Is.False);
        }

        [Test]
        public void RecordedPolicy_FallsBackForChords()
        {
            var source = new FakeClipSource();
            source.Add(1);

            Assert.That(BassStemClipPolicy.TryGetRecordedClip(source, key: 3, out _), Is.True);
        }

        [Test]
        public void RecordedPolicy_ExactMatchWins()
        {
            var source = new FakeClipSource();
            source.Add(1);
            source.Add(3);

            Assert.That(BassStemClipPolicy.TryGetRecordedClip(source, key: 3, out var clip), Is.True);
            Assert.That(source.TryGetClip(3, out var expected), Is.True);
            Assert.That(clip, Is.SameAs(expected));
        }

        [Test]
        public void RecordedPolicy_OpenMaskHasNoFallback()
        {
            var source = new FakeClipSource();
            source.Add(2);

            Assert.That(BassStemClipPolicy.TryGetRecordedClip(source, GuitarEngine.OPEN_MASK, out _), Is.False);
        }

        [Test]
        public void EmptySource_ResolvesNothing()
        {
            var source = new FakeClipSource();

            Assert.That(BassStemClipPolicy.TryGetSeedClip(source, key: 3, out _), Is.False);
            Assert.That(BassStemClipPolicy.TryGetRecordedClip(source, key: 3, out _), Is.False);
        }
    }
}
