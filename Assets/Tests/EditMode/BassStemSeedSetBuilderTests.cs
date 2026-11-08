using System.Collections.Generic;
using NUnit.Framework;
using YARG.Audio.BASS;
using YARG.Core.Chart;
using YARG.Core.Engine;

namespace YARG.Tests.EditMode
{
    public class BassStemSeedSetBuilderTests
    {
        private const double TICKS_PER_SEC = 960.0;

        private static GuitarNote Note(FiveFretGuitarFret fret, uint tick, uint tickLength, NoteFlags flags)
        {
            return new GuitarNote(fret, GuitarNoteType.Strum, GuitarNoteFlags.None, flags,
                tick / TICKS_PER_SEC, tickLength / TICKS_PER_SEC, tick, tickLength);
        }

        private static List<GuitarNote> Chain(params GuitarNote[] notes)
        {
            for (int i = 0; i < notes.Length; i++)
            {
                if (i > 0) notes[i].PreviousNote = notes[i - 1];
                if (i < notes.Length - 1) notes[i].NextNote = notes[i + 1];
            }

            return new List<GuitarNote>(notes);
        }

        [Test]
        public void Build_KeysEachSeedSetByItsPhrasesNotes()
        {
            var notes = Chain(
                Note(FiveFretGuitarFret.Green, 0, 0, NoteFlags.None),
                Note(FiveFretGuitarFret.Red, 960, 0, NoteFlags.Solo | NoteFlags.SoloStart),
                Note(FiveFretGuitarFret.Yellow, 1920, 480, NoteFlags.Solo | NoteFlags.SoloEnd),
                Note(FiveFretGuitarFret.Blue, 2880, 0, NoteFlags.None));

            var phrases = new List<SoloSection> { new(960u, 1920u, 2) };

            var seedSets = BassStemSeedSetBuilder.Build(phrases, notes, playbackTime: t => t + 10);

            Assert.That(seedSets, Has.Count.EqualTo(1));

            var set = seedSets[0];
            Assert.That(set.StartTime, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(set.EndTime, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(set.Notes, Has.Count.EqualTo(2));
            Assert.That(set.Notes[0].Key, Is.EqualTo(notes[1].NoteMask));
            Assert.That(set.Notes[0].SongSeconds, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(set.Notes[0].PlaybackSeconds, Is.EqualTo(11.0).Within(1e-9));
            Assert.That(set.Notes[1].IsSustain, Is.True);
            Assert.That(set.Notes[1].DurationSeconds, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(set.Covers(1.5), Is.True);
            Assert.That(set.Covers(2.5), Is.False);
        }

        [Test]
        public void Build_TakesPhraseBoundariesFromTheSection_NotTheNoteFlags()
        {
            var notes = Chain(
                Note(FiveFretGuitarFret.Green, 0, 0, NoteFlags.None),
                Note(FiveFretGuitarFret.Red, 960, 0, NoteFlags.None),
                Note(FiveFretGuitarFret.Yellow, 1920, 0, NoteFlags.None));

            var phrases = new List<SoloSection> { new(960u, 1920u, 2) };

            var seedSets = BassStemSeedSetBuilder.Build(phrases, notes, playbackTime: t => t);

            Assert.That(seedSets, Has.Count.EqualTo(1));
            Assert.That(seedSets[0].Notes, Has.Count.EqualTo(2));
            Assert.That(seedSets[0].StartTime, Is.EqualTo(1.0).Within(1e-9));
        }

        [Test]
        public void Build_SkipsAPhraseWithNoNotes()
        {
            var notes = Chain(
                Note(FiveFretGuitarFret.Green, 0, 0, NoteFlags.None),
                Note(FiveFretGuitarFret.Red, 4800, 0, NoteFlags.None));

            var phrases = new List<SoloSection>
            {
                new(960u, 1920u, 1),
                new(4800u, 4800u, 1),
            };

            var seedSets = BassStemSeedSetBuilder.Build(phrases, notes, playbackTime: t => t);

            Assert.That(seedSets, Has.Count.EqualTo(1));
            Assert.That(seedSets[0].StartTime, Is.EqualTo(5.0).Within(1e-9));
        }
    }
}
