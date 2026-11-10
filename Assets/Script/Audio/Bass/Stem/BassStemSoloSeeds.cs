#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoloSeeds
    {
        public const int FRET_COUNT = 5;

        private static readonly int[] FRET_KEYS = { 1, 2, 4, 8, 16 };

        private const int    PITCH_CLASSES     = 12;
        private const int    FIFTH             = 7;
        private const int    MINOR_THIRD       = 3;
        private const int    MAJOR_THIRD       = 4;
        private const int    MAX_RESOLVE       = 2;
        private const double MIN_EVIDENCE      = 2;
        private const double PREFERENCE_RATIO  = 2;
        private const double CONTINUITY_SECONDS = 1.0;

        public readonly double                                StartTime;
        public readonly double                                EndTime;
        public readonly int                                   LowestNote;
        public readonly Dictionary<int, List<BassStemClip>>   ByKey = new();
        public readonly List<BassStemClip>                    Ladder = new();
        public readonly List<(double SongSeconds, int Start)> Windows = new();
        public readonly List<(double SongSeconds, int Note)>  Anchors = new();
        public readonly List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> Notes;
        public readonly List<(BassStemClip Clip, float[] Raw, int AttackFrames)> Candidates = new();
        public          float[]                                                  Region = Array.Empty<float>();
        public          long                                                     RegionStartFrame;

        private double          _songSeconds;
        private int             _root = -1;
        private double[,]?      _rootWeights;
        private readonly List<int>?[] _allowedRungs = new List<int>?[PITCH_CLASSES + 1];
        private int             _lastFret = -1;
        private int             _lastNote;
        private double          _lastSeconds;

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
            if (_root >= 0 && _rootWeights == null)
            {
                Learn(chords!);
            }
        }

        public int ResolveSemitones(BassStemClip clip)
        {
            if (_root < 0 || clip.Note < 0)
            {
                return 0;
            }

            int pitchClass = PitchClass(clip.Note);
            if (IsChordTone(pitchClass))
            {
                return 0;
            }

            int ceiling = NextFretNote(clip);
            for (int semitones = 1; semitones <= MAX_RESOLVE && clip.Note + semitones < ceiling; semitones++)
            {
                if (IsChordTone((pitchClass + semitones) % PITCH_CLASSES))
                {
                    return semitones;
                }
            }

            return 0;
        }

        public bool TryGetClip(int key, out BassStemClip clip)
        {
            if (Ladder.Count > 0)
            {
                for (int fret = 0; fret < FRET_COUNT; fret++)
                {
                    if (key == 1 << fret)
                    {
                        clip = Ladder[RungFor(fret)];
                        return true;
                    }
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
            if (Ladder.Count == 0 || fret < 0 || clip.Note < 0)
            {
                return true;
            }

            if (_lastFret >= 0 && fret != _lastFret && _songSeconds - _lastSeconds <= CONTINUITY_SECONDS)
            {
                bool up = fret > _lastFret;
                int sounding = Sounding(clip);
                if (up ? sounding <= _lastNote : sounding >= _lastNote)
                {
                    clip = NearestBeyond(_lastNote, up);
                }
            }

            _lastFret = fret;
            _lastNote = Sounding(clip);
            _lastSeconds = _songSeconds;
            return true;
        }

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            if (Ladder.Count > 0)
            {
                clip = Ladder[WindowStart()];
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

        private int WindowStart()
        {
            int start = Windows[0].Start;
            foreach (var window in Windows)
            {
                if (window.SongSeconds > _songSeconds)
                {
                    break;
                }

                start = window.Start;
            }

            return start;
        }

        private int Sounding(BassStemClip clip)
        {
            return clip.Note + ResolveSemitones(clip);
        }

        private BassStemClip NearestBeyond(int note, bool up)
        {
            var rungs = AllowedRungs();
            if (up)
            {
                foreach (int rung in rungs)
                {
                    if (Sounding(Ladder[rung]) > note)
                    {
                        return Ladder[rung];
                    }
                }

                return Ladder[rungs[^1]];
            }

            for (int index = rungs.Count - 1; index >= 0; index--)
            {
                if (Sounding(Ladder[rungs[index]]) < note)
                {
                    return Ladder[rungs[index]];
                }
            }

            return Ladder[rungs[0]];
        }

        private int NextFretNote(BassStemClip clip)
        {
            if (Ladder.Count < FRET_COUNT)
            {
                return int.MaxValue;
            }

            for (int fret = 0; fret < FRET_COUNT - 1; fret++)
            {
                if (Ladder[RungFor(fret)] == clip)
                {
                    return Ladder[RungFor(fret + 1)].Note;
                }
            }

            return int.MaxValue;
        }

        private int RungFor(int fret)
        {
            var rungs = AllowedRungs();
            int start = WindowStart();
            int first = rungs.FindIndex(rung => rung >= start);
            if (first < 0 || first > rungs.Count - FRET_COUNT)
            {
                first = rungs.Count - FRET_COUNT;
            }

            return rungs[first + fret];
        }

        private List<int> AllowedRungs()
        {
            int slot = _root >= 0 ? _root : PITCH_CLASSES;
            if (_allowedRungs[slot] is { } cached)
            {
                return cached;
            }

            var allowed = AllowedPitchClasses();
            var rungs = new List<int>(Ladder.Count);
            for (int index = 0; index < Ladder.Count; index++)
            {
                if (Ladder[index].Note < 0 || allowed[PitchClass(Ladder[index].Note)])
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

            if (_root < 0 || _rootWeights != null)
            {
                _allowedRungs[slot] = rungs;
            }

            return rungs;
        }

        private bool[] AllowedPitchClasses()
        {
            var allowed = ScalePitchClasses();
            if (_root < 0)
            {
                return allowed;
            }

            allowed[(_root + 1) % PITCH_CLASSES] = false;
            for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
            {
                int upper = (pitchClass + 1) % PITCH_CLASSES;
                if (!allowed[pitchClass] || !allowed[upper])
                {
                    continue;
                }

                double lowerWeight = Weight(pitchClass);
                double upperWeight = Weight(upper);
                if (lowerWeight >= MIN_EVIDENCE && lowerWeight > upperWeight * PREFERENCE_RATIO)
                {
                    allowed[upper] = false;
                }
                else if (upperWeight >= MIN_EVIDENCE && upperWeight > lowerWeight * PREFERENCE_RATIO)
                {
                    allowed[pitchClass] = false;
                }
            }

            return allowed;
        }

        private bool[] ScalePitchClasses()
        {
            var scale = new bool[PITCH_CLASSES];
            foreach (var clip in Ladder)
            {
                if (clip.Note >= 0)
                {
                    scale[PitchClass(clip.Note)] = true;
                }
            }

            return scale;
        }

        private bool IsChordTone(int pitchClass)
        {
            return pitchClass == _root || pitchClass == (_root + FIFTH) % PITCH_CLASSES || pitchClass == Third();
        }

        private int Third()
        {
            var scale = ScalePitchClasses();
            int minor = (_root + MINOR_THIRD) % PITCH_CLASSES;
            int major = (_root + MAJOR_THIRD) % PITCH_CLASSES;
            if (scale[minor] != scale[major])
            {
                return scale[minor] ? minor : major;
            }

            if (!scale[minor] || Weight(minor) == Weight(major))
            {
                return -1;
            }

            return Weight(minor) > Weight(major) ? minor : major;
        }

        private double Weight(int pitchClass)
        {
            return _rootWeights?[_root, pitchClass] ?? 0;
        }

        private void Learn(BassChordTrack chords)
        {
            var weights = new double[PITCH_CLASSES, PITCH_CLASSES];
            foreach (var (songSeconds, note) in Anchors)
            {
                int root = chords.RootAt(songSeconds);
                if (root >= 0)
                {
                    weights[root, PitchClass(note)]++;
                }
            }

            _rootWeights = weights;
        }

        private static int PitchClass(int note)
        {
            return (note % PITCH_CLASSES + PITCH_CLASSES) % PITCH_CLASSES;
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
