#nullable enable
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using YARG.Audio.BASS;
using YARG.Core.Engine.Guitar;

namespace YARG.Tests.EditMode
{
    public class BassStemSeedLibraryTests
    {
        private static readonly BassStemFormat FORMAT = new(frequency: 48000, channels: 2);

        private static IBassStretchRender? CreateRender(int channels, int frequency, float speed)
        {
            return null;
        }

        private static List<BassStemSeedNote> CreateNotes()
        {
            return new List<BassStemSeedNote>
            {
                new(key: 1, songSeconds: 10.0, playbackSeconds: 1.0, durationSeconds: 0.2,
                    sustainStartSeconds: 0, isSustain: false),
                new(key: 2, songSeconds: 11.0, playbackSeconds: 2.0, durationSeconds: 0.2,
                    sustainStartSeconds: 0, isSustain: false),
            };
        }

        private static BassStemSeedLibrary CreateLibrary()
        {
            var renderer = new BassStemSeedRenderer(FORMAT, CreateRender, () => false);
            var library = new BassStemSeedLibrary(renderer);
            library.Request(new List<BassStemSeedSet>
            {
                new(startTime: 10.0, endTime: 12.0, notes: CreateNotes()),
            });

            var jobs = new List<BassStemSampleJob>();
            library.PrepareJobs(jobs);
            library.Publish(CancellationToken.None);
            return library;
        }

        [Test]
        public void PrepareJobs_EmitsOneJobPerSeedNote()
        {
            var renderer = new BassStemSeedRenderer(FORMAT, CreateRender, () => false);
            var library = new BassStemSeedLibrary(renderer);
            library.Request(new List<BassStemSeedSet>
            {
                new(startTime: 10.0, endTime: 12.0, notes: CreateNotes()),
            });

            var jobs = new List<BassStemSampleJob>();
            library.PrepareJobs(jobs);

            Assert.That(jobs.Count, Is.EqualTo(2));
        }

        [Test]
        public void HasSeeds_TracksSoloRange()
        {
            var library = CreateLibrary();

            Assert.That(library.HasSeeds(10.5), Is.True);
            Assert.That(library.HasSeeds(13.0), Is.False);
        }

        [Test]
        public void Select_ResolvesSeedClipsPerKey()
        {
            var library = CreateLibrary();

            library.Select(soloTime: 10.5, songTime: 10.5);

            Assert.That(library.IsSelected, Is.True);
            Assert.That(library.TryGetClip(1, out var green), Is.True);
            Assert.That(library.TryGetClip(2, out var red), Is.True);
            Assert.That(green, Is.Not.SameAs(red));
            Assert.That(green.FrameCount, Is.GreaterThan(0));
        }

        [Test]
        public void Select_ResolvesOpenMaskToAFrettedClip()
        {
            var library = CreateLibrary();

            library.Select(soloTime: 10.5, songTime: 10.5);

            Assert.That(library.TryGetClip(GuitarEngine.OPEN_MASK, out var clip), Is.True);
            Assert.That(library.TryGetClip(1, out var green), Is.True);
            Assert.That(library.TryGetClip(2, out var red), Is.True);
            Assert.That(clip == green || clip == red, Is.True);
        }

        [Test]
        public void FretLookups_ResolveFromSelectedClips()
        {
            var library = CreateLibrary();
            library.Select(soloTime: 10.5, songTime: 10.5);

            Assert.That(library.TryGetFretClip(3, out _), Is.True);
            Assert.That(library.TryGetFretClip(4, out _), Is.False);
            Assert.That(library.TryGetAnyClip(out _), Is.True);
        }

        [Test]
        public void Selection_FollowsClipReplacement()
        {
            var clips = new BassStemSoloSeeds(startTime: 10.0, endTime: 12.0);
            var seed = new BassStemSeed(songSeconds: 10.5, clip: new BassStemClip(), sustainStartFrame: 0,
                startFrame: 0);
            clips.ByKey.Add(1, new List<BassStemSeed> { seed });
            clips.Select(10.5);

            var replacement = new BassStemClip();
            seed.Clip = replacement;

            Assert.That(clips.TryGetClip(1, out var clip), Is.True);
            Assert.That(clip, Is.SameAs(replacement));
        }

        [Test]
        public void ClearSelection_StopsResolvingClips()
        {
            var library = CreateLibrary();
            library.Select(soloTime: 10.5, songTime: 10.5);

            library.ClearSelection();

            Assert.That(library.IsSelected, Is.False);
            Assert.That(library.TryGetClip(1, out _), Is.False);
            Assert.That(library.TryGetAnyClip(out _), Is.False);
        }

        [Test]
        public void Select_OutsideAnySet_LeavesNoClips()
        {
            var library = CreateLibrary();

            library.Select(soloTime: 30.0, songTime: 30.0);

            Assert.That(library.TryGetClip(1, out _), Is.False);
            Assert.That(library.HasSeeds(30.0), Is.False);
        }
    }
}
