#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoloSeeds
    {
        public const int FRET_COUNT = 5;

        private static readonly int[] FRET_KEYS = { 1, 2, 4, 8, 16 };

        private const int    PITCH_CLASSES      = BassSoloHarmony.PITCH_CLASSES;
        private const int    MAX_WHAMMY_BEND    = 4;
        private const int    DEFAULT_WHAMMY     = 2;
        private const double CONTINUITY_SECONDS = 1.0;

        public readonly double                                          StartTime;
        public readonly double                                          EndTime;
        public readonly int                                             LowestNote;
        public readonly Dictionary<int, List<BassStemClip>>             ByKey = new();
        public readonly List<BassStemClip>                              Ladder = new();
        public readonly List<(double SongSeconds, int Note, int Fret)>  Windows = new();
        public readonly List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> Notes;
        public          bool[]                                                  Key = Array.Empty<bool>();
        public readonly List<(BassStemClip Clip, float[] Raw, int AttackFrames)> Candidates = new();
        public          float[]                                                  Region = Array.Empty<float>();
        public          long                                                     RegionStartFrame;

        private readonly List<int>?[] _allowedRungs = new List<int>?[PITCH_CLASSES + 1];
        private          double       _songSeconds;
        private          int          _root = -1;
        private          int          _lastFret = -1;
        private          int          _lastNote;
        private          double       _lastSeconds;

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

            var good = GoodPitchClasses();
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
                    clip = RungClip(fret, followLast: false);
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
            if (Ladder.Count == 0)
            {
                return TryGetClip(key, out clip);
            }

            int fret = Array.IndexOf(FRET_KEYS, key);
            if (fret < 0)
            {
                return TryGetClip(key, out clip);
            }

            int rung = RungFor(fret, followLast: true);
            clip = Ladder[AllowedRungs()[rung]];
            _lastFret = fret;
            _lastNote = clip.Note;
            _lastSeconds = _songSeconds;
            return true;
        }

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            if (Ladder.Count > 0)
            {
                clip = RungClip(0, followLast: false);
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

        private BassStemClip RungClip(int fret, bool followLast)
        {
            return Ladder[AllowedRungs()[RungFor(fret, followLast)]];
        }

        private int RungFor(int fret, bool followLast)
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
            int position = first + fret;
            if (followLast && _lastFret >= 0 && fret != _lastFret
                && _songSeconds - _lastSeconds <= CONTINUITY_SECONDS)
            {
                position = RungBeyond(rungs, position, fret > _lastFret);
            }

            return position;
        }

        private int RungBeyond(List<int> rungs, int position, bool up)
        {
            int step = up ? 1 : -1;
            for (int index = position; index >= 0 && index < rungs.Count; index += step)
            {
                int note = Ladder[rungs[index]].Note;
                if (up ? note > _lastNote : note < _lastNote)
                {
                    return index;
                }
            }

            return position;
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
            int slot = _root >= 0 ? _root : PITCH_CLASSES;
            if (_allowedRungs[slot] is { } cached)
            {
                return cached;
            }

            var good = GoodPitchClasses();
            var rungs = new List<int>(Ladder.Count);
            for (int index = 0; index < Ladder.Count; index++)
            {
                if (good[BassSoloHarmony.PitchClass(Ladder[index].Note)])
                {
                    rungs.Add(index);
                }
            }

            if (rungs.Count < FRET_COUNT)
            {
                rungs.Clear();
                for (int index = 0; index < Ladder.Count; index++)
                {
                    rungs.Add(index);
                }
            }

            _allowedRungs[slot] = rungs;
            return rungs;
        }

        private bool[] GoodPitchClasses()
        {
            var key = Key.Length == PITCH_CLASSES ? Key : AllPitchClasses();
            if (_root < 0)
            {
                return key;
            }

            int third = BassSoloHarmony.Third(key, _root);
            int fifth = (_root + BassSoloHarmony.FIFTH) % PITCH_CLASSES;
            return BassSoloHarmony.ChordNotes(key, _root, third, fifth);
        }

        private static bool[] AllPitchClasses()
        {
            var pitchClasses = new bool[PITCH_CLASSES];
            for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
            {
                pitchClasses[pitchClass] = true;
            }

            return pitchClasses;
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
