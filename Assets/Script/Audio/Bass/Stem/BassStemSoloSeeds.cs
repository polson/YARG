#nullable enable
using System;
using System.Collections.Generic;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoloSeeds
    {
        public const int FRET_COUNT = 5;

        private static readonly int[] FRET_KEYS = { 1, 2, 4, 8, 16 };

        private const int    PITCH_CLASSES      = BassSoloHarmony.PITCH_CLASSES;
        private const int    MAX_WHAMMY_BEND    = 4;
        private const int    DEFAULT_WHAMMY     = 2;

        private static readonly double[] VOCABULARY_SECONDS = { 1.0, 2.0, 4.0, double.MaxValue };

        public readonly double                                          StartTime;
        public readonly double                                          EndTime;
        public readonly int                                             LowestNote;
        public readonly Dictionary<int, List<BassStemClip>>             ByKey = new();
        public readonly List<BassStemClip>                              Ladder = new();
        public readonly List<(double SongSeconds, int Note, int Fret)>  Windows = new();
        public readonly List<(double SongSeconds, int Note)>            Anchors = new();
        public readonly List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> Notes;
        public readonly List<(BassStemClip Clip, float[] Raw, int AttackFrames)> Candidates = new();
        public          float[]                                                  Region = Array.Empty<float>();
        public          long                                                     RegionStartFrame;

        private const double PHRASE_RESET_SECONDS = 1.0;

        private readonly int[]        _fretRungs = new int[FRET_COUNT];
        private          bool         _fretRungsValid;
        private          double       _songSeconds;
        private          int          _root = -1;
        private          bool[]?      _pentatonicScale;
        private          double       _lastPlaySeconds;

        public BassStemSoloSeeds(double startTime, double endTime,
            List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> notes,
            int lowestNote)
        {
            StartTime = startTime;
            EndTime = endTime;
            Notes = notes;
            LowestNote = lowestNote;
        }

        public bool IsEmpty => ByKey.Count == 0;

        public bool HasRegion => Region.Length > 0;

        public bool Covers(double soloTime)
        {
            return soloTime >= StartTime && soloTime <= EndTime;
        }

        public void Select(double songSeconds, BassChordTrack? chords)
        {
            _songSeconds = songSeconds;
            _root = chords?.RootAt(songSeconds) ?? -1;
        }

        public int WhammyBend(BassStemClip clip)
        {
            if (clip.Note < 0)
            {
                return DEFAULT_WHAMMY;
            }

            var good = GoodPitchClasses(VOCABULARY_SECONDS[0]);
            for (int semitones = 1; semitones <= MAX_WHAMMY_BEND; semitones++)
            {
                if (good[BassSoloHarmony.PitchClass(clip.Note + semitones)])
                {
                    return semitones;
                }
            }

            return DEFAULT_WHAMMY;
        }

        public bool TryGetClip(int key, out BassStemClip clip)
        {
            if (Ladder.Count > 0)
            {
                int fret = Array.IndexOf(FRET_KEYS, key);
                if (fret >= 0)
                {
                    clip = RungClip(fret);
                    return true;
                }

                clip = null!;
                return false;
            }

            if (ByKey.TryGetValue(key, out var clips))
            {
                clip = ClipAt(clips);
                return true;
            }

            clip = null!;
            return false;
        }

        public bool TryTakeClip(int key, out BassStemClip clip)
        {
            if (!TryGetClip(key, out clip))
            {
                return false;
            }

            int fret = Array.IndexOf(FRET_KEYS, key);
            if (Ladder.Count > 0 && fret >= 0)
            {
                var (windowNote, _) = CurrentWindow();
                YargLogger.LogFormatDebug("[SoloFret] {0:0.00}s fret {1} note {2} window {3} root {4}",
                    _songSeconds, fret, clip.Note, windowNote, _root);
            }

            return true;
        }

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            if (Ladder.Count > 0)
            {
                clip = RungClip(0);
                return true;
            }

            foreach (var clips in ByKey.Values)
            {
                clip = ClipAt(clips);
                return true;
            }

            clip = null!;
            return false;
        }

        private BassStemClip RungClip(int fret)
        {
            return Ladder[RungFor(fret)];
        }

        /// <summary>
        /// Locks the five frets to stable scale positions during an active phrase so that
        /// hitting the same fret repeatedly is guaranteed to sound the exact same note,
        /// and adjacent frets never share a pitch.
        /// </summary>
        private int RungFor(int fret)
        {
            fret = Math.Clamp(fret, 0, FRET_COUNT - 1);
            if (!_fretRungsValid || _songSeconds - _lastPlaySeconds > PHRASE_RESET_SECONDS)
            {
                UpdateFretRungs();
            }

            _lastPlaySeconds = _songSeconds;
            return _fretRungs[fret];
        }

        private void UpdateFretRungs()
        {
            var rungs = AllowedRungs();
            var (note, windowFret) = CurrentWindow();
            int nearest = 0;
            for (int index = 1; index < rungs.Count; index++)
            {
                if (Math.Abs(Ladder[rungs[index]].Note - note) < Math.Abs(Ladder[rungs[nearest]].Note - note))
                {
                    nearest = index;
                }
            }

            int first = Math.Max(0, Math.Min(nearest - windowFret, rungs.Count - FRET_COUNT));
            for (int f = 0; f < FRET_COUNT; f++)
            {
                _fretRungs[f] = rungs[first + f];
            }

            _fretRungsValid = true;
        }

        private (int Note, int Fret) CurrentWindow()
        {
            if (Windows.Count == 0)
            {
                return (Ladder[Ladder.Count / 2].Note, FRET_COUNT / 2);
            }

            var (_, note, fret) = Windows[0];
            foreach (var window in Windows)
            {
                if (window.SongSeconds > _songSeconds)
                {
                    break;
                }

                (_, note, fret) = window;
            }

            return (note, fret);
        }

        private List<int> AllowedRungs()
        {
            var rungs = new List<int>(Ladder.Count);
            foreach (double seconds in VOCABULARY_SECONDS)
            {
                CollectRungs(rungs, GoodPitchClasses(seconds));
                FilterConsecutiveSemitones(rungs);
                if (rungs.Count >= FRET_COUNT)
                {
                    return rungs;
                }
            }

            CollectRungs(rungs, PentatonicScale());
            FilterConsecutiveSemitones(rungs);
            if (rungs.Count >= FRET_COUNT)
            {
                return rungs;
            }

            rungs.Clear();
            for (int index = 0; index < Ladder.Count; index += 2)
            {
                rungs.Add(index);
            }

            return rungs;
        }

        private void FilterConsecutiveSemitones(List<int> rungs)
        {
            for (int index = rungs.Count - 1; index >= 1; index--)
            {
                if (Ladder[rungs[index]].Note - Ladder[rungs[index - 1]].Note < 2)
                {
                    rungs.RemoveAt(index);
                }
            }
        }

        private bool[] PentatonicScale()
        {
            if (_pentatonicScale != null)
            {
                return _pentatonicScale;
            }

            _pentatonicScale = BassSoloHarmony.PentatonicScale(Anchors);
            return _pentatonicScale;
        }

        private void CollectRungs(List<int> rungs, bool[] good)
        {
            rungs.Clear();
            for (int index = 0; index < Ladder.Count; index++)
            {
                if (good[BassSoloHarmony.PitchClass(Ladder[index].Note)])
                {
                    rungs.Add(index);
                }
            }
        }

        private bool[] GoodPitchClasses(double seconds)
        {
            var good = new bool[PITCH_CLASSES];
            foreach (var (songSeconds, note) in Anchors)
            {
                if (Math.Abs(songSeconds - _songSeconds) <= seconds)
                {
                    good[BassSoloHarmony.PitchClass(note)] = true;
                }
            }

            if (_root >= 0 && HasAnchorPitchClass(_root))
            {
                good[_root] = true;
                int fifth = (_root + BassSoloHarmony.FIFTH) % PITCH_CLASSES;
                if (HasAnchorPitchClass(fifth))
                {
                    good[fifth] = true;
                }
            }

            return good;
        }

        private bool HasAnchorPitchClass(int pitchClass)
        {
            foreach (var (_, note) in Anchors)
            {
                if (BassSoloHarmony.PitchClass(note) == pitchClass)
                {
                    return true;
                }
            }

            return false;
        }

        private BassStemClip ClipAt(List<BassStemClip> clips)
        {
            foreach (var clip in clips)
            {
                if (clip.SongSeconds >= _songSeconds)
                {
                    return clip;
                }
            }

            return clips[clips.Count - 1];
        }
    }
}
