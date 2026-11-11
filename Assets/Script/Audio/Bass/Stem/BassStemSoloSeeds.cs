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
        private static readonly int[] ROOT_OFFSETS = { 0, -1, 1 };
        private static readonly int[] COLOR_OFFSETS = { 0, -1, 1, -2, 2 };

        private const int FIFTH_SEMITONES      = 7;
        private const int FOURTH_SEMITONES     = 5;
        private const int OCTAVE_SEMITONES     = 12;
        private const int FULL_CHORD_FRETS     = 3;

        private const int    MAX_WHAMMY_BEND   = 4;
        private const int    DEFAULT_WHAMMY    = 2;
        private const int    BOX_SHIFT_RUNGS   = 3;
        private const int    EDGE_SHIFT_RUNGS  = 3;
        private const double LOW_FRET_AVERAGE  = 1.5;
        private const double HIGH_FRET_AVERAGE = 2.5;
        private const double COLOR_BOX_CHANCE  = 0.35;

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
        public          bool                                                     CanStretch;

        private const double PHRASE_RESET_SECONDS = 0.6;

        private readonly int[]        _fretRungs = new int[FRET_COUNT];
        private          bool         _fretRungsValid;
        private          double       _songSeconds;
        private          int          _root = -1;
        private          bool[]?      _pentatonicScale;
        private          List<int>?   _scaleRungs;
        private          List<int>?   _colorRungs;
        private          bool[]?      _colorScale;
        private          bool         _colorChecked;
        private          bool         _colorBox;
        private          int          _boxStart = -1;
        private          int          _phraseFretTotal;
        private          int          _phraseFretCount;
        private          int          _lastFret = -1;
        private          int          _fretBeforeLast = -1;
        private          int          _lastShift;
        private          double       _lastPlaySeconds;
        private readonly Dictionary<BassStemClip, int> _lastTakes = new();
        private readonly Random       _random = new();

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

            var scale = PentatonicScale();
            for (int semitones = 1; semitones <= MAX_WHAMMY_BEND; semitones++)
            {
                if (scale[BassSoloHarmony.PitchClass(clip.Note + semitones)])
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
                clip = NextTake(clip);
                _phraseFretTotal += fret;
                _phraseFretCount++;
                FollowRun(fret);
                var (windowNote, _) = CurrentWindow();
                YargLogger.LogFormatDebug("[SoloFret] {0:0.00}s fret {1} note {2} window {3} root {4}",
                    _songSeconds, fret, clip.Note, windowNote, _root);
            }

            return true;
        }

        public bool TryGetChordClips(int fretMask, List<BassStemClip> clips)
        {
            clips.Clear();
            if (Ladder.Count == 0)
            {
                if (!ByKey.TryGetValue(fretMask, out var recorded))
                {
                    return false;
                }

                clips.Add(ClipAt(recorded));
                return true;
            }

            if (!TryTakeClip(fretMask & -fretMask, out var root))
            {
                return false;
            }

            clips.Add(root);
            var fifth = InScale(root.Note + FIFTH_SEMITONES)
                ? LadderTake(root.Note + FIFTH_SEMITONES) ?? LadderTake(root.Note - FOURTH_SEMITONES)
                : null;
            if (fifth != null)
            {
                clips.Add(fifth);
            }

            if (fifth == null || FretCount(fretMask) >= FULL_CHORD_FRETS)
            {
                var octave = LadderTake(root.Note + OCTAVE_SEMITONES) ?? LadderTake(root.Note - OCTAVE_SEMITONES);
                if (octave != null)
                {
                    clips.Add(octave);
                }
            }

            clips.Sort((left, right) => left.Note.CompareTo(right.Note));
            return true;
        }

        private bool InScale(int note)
        {
            int pitchClass = BassSoloHarmony.PitchClass(note);
            return PentatonicScale()[pitchClass] || ColorScale()?[pitchClass] == true;
        }

        private static int FretCount(int fretMask)
        {
            int count = 0;
            foreach (int key in FRET_KEYS)
            {
                if ((fretMask & key) != 0)
                {
                    count++;
                }
            }

            return count;
        }

        private BassStemClip? LadderTake(int note)
        {
            foreach (var clip in Ladder)
            {
                if (clip.Note == note)
                {
                    return NextTake(clip);
                }
            }

            return null;
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

        private BassStemClip NextTake(BassStemClip clip)
        {
            int count = clip.Takes.Length + 1;
            if (count == 1)
            {
                return clip;
            }

            _lastTakes.TryGetValue(clip, out int last);
            int take = (last + 1 + _random.Next(count - 1)) % count;
            _lastTakes[clip] = take;
            return take == 0 ? clip : clip.Takes[take - 1];
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
            var previous = BoxRungs();
            int targetRung = _boxStart < 0
                ? -1
                : previous[Math.Clamp(_boxStart + PhraseShift(), 0, previous.Count - FRET_COUNT)];
            _colorBox = ChooseColorBox();
            var rungs = BoxRungs();
            int lastStart = rungs.Count - FRET_COUNT;
            int target = targetRung < 0 ? WindowStart(rungs) : FirstAtOrAbove(rungs, targetRung);
            _boxStart = RootAlignedStart(rungs, Math.Clamp(target, 0, lastStart), lastStart);
            if (_colorBox)
            {
                _boxStart = ColorAlignedStart(rungs, _boxStart, lastStart);
            }
            _phraseFretTotal = 0;
            _phraseFretCount = 0;
            _lastFret = -1;
            _fretBeforeLast = -1;
            _lastShift = 0;
            FillBox(rungs);
            _fretRungsValid = true;
        }

        private void FillBox(List<int> rungs)
        {
            for (int f = 0; f < FRET_COUNT; f++)
            {
                _fretRungs[f] = rungs[_boxStart + f];
            }
        }

        private void FollowRun(int fret)
        {
            int previous = _lastFret;
            int beforePrevious = _fretBeforeLast;
            int lastShift = _lastShift;
            _fretBeforeLast = previous;
            _lastFret = fret;
            _lastShift = 0;
            if (previous < 0 || beforePrevious == fret)
            {
                return;
            }

            if (fret == FRET_COUNT - 1 && previous < fret && lastShift >= 0)
            {
                ShiftBox(EDGE_SHIFT_RUNGS);
                _lastShift = 1;
            }
            else if (fret == 0 && previous > fret && lastShift <= 0)
            {
                ShiftBox(-EDGE_SHIFT_RUNGS);
                _lastShift = -1;
            }
        }

        private void ShiftBox(int rungCount)
        {
            var rungs = BoxRungs();
            int start = Math.Clamp(_boxStart + rungCount, 0, rungs.Count - FRET_COUNT);
            if (start == _boxStart)
            {
                return;
            }

            _boxStart = start;
            FillBox(rungs);
        }

        private int WindowStart(List<int> rungs)
        {
            var (note, windowFret) = CurrentWindow();
            int nearest = 0;
            for (int index = 1; index < rungs.Count; index++)
            {
                if (Math.Abs(Ladder[rungs[index]].Note - note) < Math.Abs(Ladder[rungs[nearest]].Note - note))
                {
                    nearest = index;
                }
            }

            return nearest - windowFret;
        }

        private int PhraseShift()
        {
            if (_phraseFretCount == 0)
            {
                return 0;
            }

            double average = (double) _phraseFretTotal / _phraseFretCount;
            if (average >= HIGH_FRET_AVERAGE)
            {
                return BOX_SHIFT_RUNGS;
            }

            return average <= LOW_FRET_AVERAGE ? -BOX_SHIFT_RUNGS : 0;
        }

        private int RootAlignedStart(List<int> rungs, int target, int lastStart)
        {
            if (_root < 0)
            {
                return target;
            }

            foreach (int offset in ROOT_OFFSETS)
            {
                int start = target + offset;
                if (start >= 0 && start <= lastStart && BassSoloHarmony.PitchClass(Ladder[rungs[start]].Note) == _root)
                {
                    return start;
                }
            }

            return target;
        }

        private int ColorAlignedStart(List<int> rungs, int start, int lastStart)
        {
            foreach (int offset in COLOR_OFFSETS)
            {
                int candidate = start + offset;
                if (candidate >= 0 && candidate <= lastStart && HasColorNote(rungs, candidate))
                {
                    return candidate;
                }
            }

            return start;
        }

        private bool HasColorNote(List<int> rungs, int start)
        {
            var pentatonic = PentatonicScale();
            for (int fret = 0; fret < FRET_COUNT; fret++)
            {
                if (!pentatonic[BassSoloHarmony.PitchClass(Ladder[rungs[start + fret]].Note)])
                {
                    return true;
                }
            }

            return false;
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

        public bool IsPlayableRung(int index)
        {
            return ScaleRungs().Contains(index) || ColorRungs()?.Contains(index) == true;
        }

        private List<int> BoxRungs()
        {
            return _colorBox ? ColorRungs() ?? ScaleRungs() : ScaleRungs();
        }

        private static int FirstAtOrAbove(List<int> rungs, int ladderIndex)
        {
            for (int index = 0; index < rungs.Count; index++)
            {
                if (rungs[index] >= ladderIndex)
                {
                    return index;
                }
            }

            return rungs.Count - 1;
        }

        private bool ChooseColorBox()
        {
            var colorScale = ColorScale();
            if (ColorRungs() == null || colorScale == null || _random.NextDouble() >= COLOR_BOX_CHANCE)
            {
                return false;
            }

            var pentatonic = PentatonicScale();
            if (_root >= 0)
            {
                for (int pitchClass = 0; pitchClass < BassSoloHarmony.PITCH_CLASSES; pitchClass++)
                {
                    bool isColor = colorScale[pitchClass] && !pentatonic[pitchClass];
                    if (isColor && BassSoloHarmony.PitchClass(pitchClass - _root) == 1)
                    {
                        return false;
                    }
                }
            }

            YargLogger.LogFormatDebug("[SoloFret] {0:0.00}s switching to the solo's color box", _songSeconds);
            return true;
        }

        private bool[]? ColorScale()
        {
            if (!_colorChecked)
            {
                _colorChecked = true;
                _colorScale = BassSoloHarmony.ColorScale(Anchors, PentatonicScale());
            }

            return _colorScale;
        }

        private List<int>? ColorRungs()
        {
            if (_colorRungs != null)
            {
                return _colorRungs;
            }

            var colorScale = ColorScale();
            if (colorScale == null)
            {
                return null;
            }

            var rungs = new List<int>(Ladder.Count);
            CollectRungs(rungs, colorScale);
            if (rungs.Count < FRET_COUNT)
            {
                _colorScale = null;
                return null;
            }

            _colorRungs = rungs;
            return rungs;
        }

        private List<int> ScaleRungs()
        {
            if (_scaleRungs != null)
            {
                return _scaleRungs;
            }

            var rungs = new List<int>(Ladder.Count);
            CollectRungs(rungs, PentatonicScale());
            FilterConsecutiveSemitones(rungs);
            if (rungs.Count < FRET_COUNT)
            {
                rungs.Clear();
                for (int index = 0; index < Ladder.Count; index += 2)
                {
                    rungs.Add(index);
                }
            }

            _scaleRungs = rungs;
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
