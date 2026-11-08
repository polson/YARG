using System.Collections.Generic;
using NUnit.Framework;
using YARG.Audio.BASS;
using YARG.Core;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Input;
using YARG.Gameplay.Player;

namespace YARG.Tests.EditMode
{
    public class FreestyleSoloTests
    {
        private const double TICKS_PER_SEC = 960.0;
        private const int    FRET_ROWS     = 0x1F;
        private const int    MAX_FRET_ROW  = 4;
        private const double HOPO_LENIENCY = 0.1;

        private static GuitarNote Note(FiveFretGuitarFret fret, uint tick, uint tickLength, NoteFlags flags)
        {
            return new GuitarNote(fret, GuitarNoteType.Strum, GuitarNoteFlags.None, flags,
                tick / TICKS_PER_SEC, tickLength / TICKS_PER_SEC, tick, tickLength);
        }

        private static YargFiveFretGuitarEngine CreateEngine()
        {
            var notes = new[]
            {
                Note(FiveFretGuitarFret.Green, 0, 0, NoteFlags.None),
                Note(FiveFretGuitarFret.Red, 480, 0, NoteFlags.Solo | NoteFlags.SoloStart),
                Note(FiveFretGuitarFret.Yellow, 720, 0, NoteFlags.Solo),
                Note(FiveFretGuitarFret.Blue, 960, 240, NoteFlags.Solo | NoteFlags.SoloEnd),
                Note(FiveFretGuitarFret.Green, 1920, 0, NoteFlags.None),
            };

            for (int i = 0; i < notes.Length; i++)
            {
                if (i > 0) notes[i].PreviousNote = notes[i - 1];
                if (i < notes.Length - 1) notes[i].NextNote = notes[i + 1];
            }

            var difficulty = new InstrumentDifficulty<GuitarNote>(Instrument.FiveFretGuitar, Difficulty.Expert,
                new(notes), new(), new());

            var thresholds = new[] { 0.08f, 0.17f, 0.28f, 0.41f, 0.55f, 0.71f, 0.88f, 1.0f };
            var engineParams = new GuitarEngineParameters(
                new HitWindowSettings(0.1, 0.1, 1.0, false, 0, 1.0, 1.0, 0.15, 0.25),
                4, 0, 0.05, thresholds, thresholds,
                0.1, 0.1, 0.1,
                false, true, false, false, true, freestyleSolos: true);

            var syncTrack = new SyncTrack(480);
            syncTrack.Tempos.Add(new TempoChange(120, 0, 0));

            var engine = new YargFiveFretGuitarEngine(difficulty, syncTrack, engineParams, false);
            engine.SetSpeed(1.0);
            return engine;
        }

        private sealed class RecordingAudio : IFreestyleSoloAudio
        {
            public readonly List<string> Starts = new();

            public int   HeldFrets;
            public float WhammyPitch;
            public bool  HasSeeds = true;

            public bool HasSeedSet(double soloTime)
            {
                return HasSeeds;
            }

            public void SelectSeedSet(double soloTime, double songTime)
            {
                Starts.Add($"select {soloTime}");
            }

            public void Play(int key, int semitones, double gain)
            {
                Starts.Add($"play {key}");
            }

            public void PlayStrum(int fretMask, int semitones, double gain)
            {
                Starts.Add($"strum {fretMask}");
            }

            public void SetHeldFrets(int fretMask)
            {
                HeldFrets = fretMask;
            }

            public void SetWhammyPitch(float percent)
            {
                WhammyPitch = percent;
            }
        }

        private sealed class Harness
        {
            public readonly YargFiveFretGuitarEngine Engine;
            public readonly RecordingAudio         Audio = new();
            public readonly List<bool>             Mutes = new();
            public readonly FreestyleSolo          Solo;

            public Harness()
            {
                Engine = CreateEngine();
                Solo = new FreestyleSolo(Audio, Engine, Mutes.Add, fretRowMask: FRET_ROWS,
                    maxFretIndex: MAX_FRET_ROW, hopoLeniency: HOPO_LENIENCY);
            }

            public void Input(double time, GuitarAction action, bool pressed)
            {
                var input = GameInput.Create(time, action, pressed);
                Engine.QueueInput(ref input);
                if (!pressed)
                {
                    return;
                }

                if (action is GuitarAction.StrumUp or GuitarAction.StrumDown)
                {
                    Solo.QueueStrum();
                }

                int fretMask = GuitarEngine.FretRowMask(action);
                if (fretMask != 0)
                {
                    Solo.QueueTap(fretMask);
                }
            }

            public void Frame(double time, float whammy = 0, bool seeking = false)
            {
                Engine.Update(time);
                Solo.Update(time, whammy, seeking);
            }

            /// <summary>Hits the chart's first note and advances to just before the solo phrase.</summary>
            public void HitFirstNote()
            {
                Input(0.0, GuitarAction.GreenFret, true);
                Input(0.0, GuitarAction.StrumDown, true);
                Input(0.02, GuitarAction.StrumDown, false);
                Input(0.10, GuitarAction.GreenFret, false);
                Frame(0.0);
                Frame(0.02);
                Frame(0.10);
                Frame(0.40);
            }

            /// <summary>Starts a phrase by strumming the green fret, then leaves the fret held.</summary>
            public void StrumGreenPhrase()
            {
                Input(0.5, GuitarAction.GreenFret, true);
                Input(0.5, GuitarAction.StrumDown, true);
                Input(0.52, GuitarAction.StrumDown, false);
                Frame(0.5);
                Frame(0.52);
            }
        }

        [Test]
        public void StrumWithNoFrets_PlaysTheOpenClip()
        {
            var h = new Harness();
            h.HitFirstNote();
            Assert.That(h.Engine.FreestyleSoloTime, Is.EqualTo(0.5));

            h.Input(0.5, GuitarAction.StrumDown, true);
            h.Frame(0.5);

            Assert.That(h.Audio.Starts, Is.EqualTo(new[] { "select 0.5", $"play {GuitarEngine.OPEN_MASK}" }));
            Assert.That(h.Audio.HeldFrets, Is.EqualTo(GuitarEngine.OPEN_MASK));
            Assert.That(h.Mutes[^1], Is.True);
        }

        [Test]
        public void StrumWithFrets_PlaysOneVoicePerFret()
        {
            var h = new Harness();
            h.HitFirstNote();

            h.Input(0.5, GuitarAction.GreenFret, true);
            h.Input(0.5, GuitarAction.StrumDown, true);
            h.Frame(0.5);

            Assert.That(h.Audio.Starts, Is.EqualTo(new[] { "select 0.5", "strum 1" }));
            Assert.That(h.Audio.HeldFrets, Is.EqualTo(1));
        }

        [Test]
        public void FretTapInsideAPhrase_PlaysAHammerOn()
        {
            var h = new Harness();
            h.HitFirstNote();
            h.StrumGreenPhrase();
            h.Audio.Starts.Clear();

            h.Input(0.6, GuitarAction.GreenFret, false);
            h.Input(0.6, GuitarAction.RedFret, true);
            h.Frame(0.6);

            Assert.That(h.Audio.Starts, Is.EqualTo(new[] { "select 0.5", "play 2" }));
            Assert.That(h.Audio.HeldFrets, Is.EqualTo(3));
        }

        [Test]
        public void FretTapWithNoPhrase_DoesNotStartOne()
        {
            var h = new Harness();
            h.HitFirstNote();

            h.Input(0.5, GuitarAction.RedFret, true);
            h.Frame(0.5);

            Assert.That(h.Audio.Starts, Is.Empty);
            Assert.That(h.Audio.HeldFrets, Is.EqualTo(2));
        }

        [Test]
        public void StrumDuringHopoLeniency_IsSwallowedUntilTheLeniencyExpires()
        {
            var h = new Harness();
            h.HitFirstNote();
            h.StrumGreenPhrase();

            // The engine applies an input queued at `t` on the next frame, so the fret change is
            // only visible to the freestyle model one frame after the tap.
            h.Input(0.6, GuitarAction.GreenFret, false);
            h.Input(0.6, GuitarAction.RedFret, true);
            h.Frame(0.6);
            h.Audio.Starts.Clear();
            h.Frame(0.62);
            Assert.That(h.Audio.Starts, Is.EqualTo(new[] { "select 0.5", "play 2" }));
            h.Audio.Starts.Clear();

            h.Input(0.65, GuitarAction.StrumDown, true);
            h.Frame(0.65);
            Assert.That(h.Audio.Starts, Is.Empty);

            h.Input(0.8, GuitarAction.StrumDown, true);
            h.Frame(0.8);
            Assert.That(h.Audio.Starts, Is.EqualTo(new[] { "select 0.5", "strum 2" }));
        }

        [Test]
        public void PhraseEndsAfterTheTapWindow_AndTheNextStrumStartsANewOne()
        {
            var h = new Harness();
            h.HitFirstNote();
            h.StrumGreenPhrase();
            h.Input(0.55, GuitarAction.GreenFret, false);
            h.Frame(0.55);
            h.Audio.Starts.Clear();

            h.Frame(0.9);
            Assert.That(h.Audio.Starts, Is.Empty);

            h.Input(0.95, GuitarAction.RedFret, true);
            h.Input(0.95, GuitarAction.StrumDown, true);
            h.Frame(0.95);

            Assert.That(h.Audio.Starts, Is.EqualTo(new[] { "select 0.5", "strum 2" }));
        }

        [Test]
        public void BeforeTheSolo_LeavesNoHeldFretsOrWhammy()
        {
            var h = new Harness();
            h.HitFirstNote();

            h.Frame(0.2, whammy: 0.8f);

            Assert.That(h.Audio.HeldFrets, Is.Zero);
            Assert.That(h.Audio.WhammyPitch, Is.Zero);
            Assert.That(h.Audio.Starts, Is.Empty);
        }

        [Test]
        public void SeekingReplay_StopsFreestyleAudio()
        {
            var h = new Harness();
            h.HitFirstNote();
            h.StrumGreenPhrase();
            h.Audio.Starts.Clear();

            h.Frame(0.6, whammy: 0.8f, seeking: true);

            Assert.That(h.Audio.HeldFrets, Is.Zero);
            Assert.That(h.Audio.WhammyPitch, Is.Zero);
            Assert.That(h.Audio.Starts, Is.Empty);
            Assert.That(h.Mutes, Does.Contain(true));
            Assert.That(h.Mutes[^1], Is.False);
        }

        [Test]
        public void NoSeedSet_DoesNotStartAPhrase()
        {
            var h = new Harness();
            h.Audio.HasSeeds = false;
            h.HitFirstNote();

            h.Input(0.5, GuitarAction.GreenFret, true);
            h.Input(0.5, GuitarAction.StrumDown, true);
            h.Frame(0.5);

            Assert.That(h.Audio.Starts, Is.Empty);
            Assert.That(h.Audio.HeldFrets, Is.EqualTo(1));
            Assert.That(h.Mutes, Is.All.False);
        }

        [Test]
        public void WhammyIsForwardedWhileTheSoloIsActive()
        {
            var h = new Harness();
            h.HitFirstNote();

            h.Frame(0.5, whammy: 0.75f);

            Assert.That(h.Audio.WhammyPitch, Is.EqualTo(0.75f));
        }

        [Test]
        public void Reset_UnmutesOnlyWhenFreestyleMutedIt()
        {
            var muted = new Harness();
            muted.HitFirstNote();
            Assert.That(muted.Mutes, Is.EqualTo(new[] { true }));

            muted.Solo.Reset();
            Assert.That(muted.Mutes, Is.EqualTo(new[] { true, false }));

            var neverMuted = new Harness();
            neverMuted.Audio.HasSeeds = false;
            neverMuted.HitFirstNote();
            neverMuted.Mutes.Clear();

            neverMuted.Solo.Reset();
            Assert.That(neverMuted.Mutes, Is.Empty);
        }
    }
}
