#nullable enable
using System;
using System.Collections.Generic;
using YARG.Audio.BASS.Effects;
using YARG.Core.Engine.Guitar;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassSoloPhrase
    {
        public const int FRET_COUNT = 5;

        private static readonly int[] FRET_KEYS = { 1, 2, 4, 8, 16 };
        private static readonly int[] ROOT_OFFSETS = { 0, -1, 1 };
        private static readonly int[] COLOR_OFFSETS = { 0, -1, 1, -2, 2 };
        private static readonly int[] MINOR_PENTATONIC = { 0, 3, 5, 7, 10 };
        private static readonly int[] MAJOR_PENTATONIC = { 0, 2, 4, 7, 9 };

        private const int FIFTH_SEMITONES      = 7;
        private const int FOURTH_SEMITONES     = 5;
        private const int OCTAVE_SEMITONES     = 12;
        private const int FULL_CHORD_FRETS     = 3;
        private const int PITCH_CLASSES        = 12;

        private const double MIN_COLOR_SHARE   = 0.03;
        private const int    MAX_COLOR_NOTES   = 2;

        private const int    MAX_WHAMMY_BEND   = 4;
        private const int    DEFAULT_WHAMMY    = 2;
        private const int    BOX_SHIFT_RUNGS   = 3;
        private const int    EDGE_SHIFT_RUNGS  = 3;
        private const double LOW_FRET_AVERAGE  = 1.5;
        private const double HIGH_FRET_AVERAGE = 2.5;
        private const double COLOR_BOX_CHANCE  = 0.35;

        private const double PHRASE_RESET_SECONDS = 0.6;

        public readonly double          StartTime;
        public readonly double          EndTime;
        public          BassLeadAmpTone Tone { get; private set; } = BassLeadAmpTone.Default;
        public          double          SynthGain { get; private set; }
        public          bool            IsEmpty => _ladder.Count == 0;

        private readonly List<BassStemClip>                             _ladder = new();
        private readonly List<(double SongSeconds, int Note, int Fret)> _windows = new();
        private readonly List<(double SongSeconds, int Note)>           _anchors = new();
        private readonly int[]        _fretRungs = new int[FRET_COUNT];
        private          double       _songSeconds;
        private          int          _root = -1;
        private          bool[]?      _pentatonicScale;
        private          int[]?       _pitchClassWeights;
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
        private          double       _lastPlaySeconds = double.MinValue;
        private readonly Dictionary<BassStemClip, int> _lastTakes = new();
        private readonly Random       _random = new();

        public BassSoloPhrase(double startTime, double endTime)
        {
            StartTime = startTime;
            EndTime = endTime;
        }

        public void Publish(IReadOnlyList<BassStemClip> ladder,
            IReadOnlyList<(double SongSeconds, int Note, int Fret)> windows,
            IReadOnlyList<(double SongSeconds, int Note)> anchors, BassLeadAmpTone tone, double synthGain)
        {
            _ladder.Clear();
            _ladder.AddRange(ladder);
            _windows.Clear();
            _windows.AddRange(windows);
            _anchors.Clear();
            _anchors.AddRange(anchors);
            Tone = tone;
            SynthGain = synthGain;
        }

        public bool Covers(double soloTime)
        {
            return soloTime >= StartTime && soloTime <= EndTime;
        }

        public void SetPosition(double songSeconds, BassChordTrack? chords)
        {
            _songSeconds = songSeconds;
            _root = chords?.RootAt(songSeconds) ?? -1;
        }

        public int WhammySemitones(BassStemClip clip)
        {
            if (clip.Note < 0)
            {
                return DEFAULT_WHAMMY;
            }

            var scale = PentatonicScale();
            for (int semitones = 1; semitones <= MAX_WHAMMY_BEND; semitones++)
            {
                if (scale[PitchClass(clip.Note + semitones)])
                {
                    return semitones;
                }
            }

            return DEFAULT_WHAMMY;
        }

        private bool TryGetClip(int fretMask, out BassStemClip clip)
        {
            int fret = Array.IndexOf(FRET_KEYS, fretMask);
            if (fret < 0 || _ladder.Count == 0)
            {
                clip = null!;
                return false;
            }

            clip = RungClip(fret);
            return true;
        }

        private bool TryTakeClip(int fretMask, out BassStemClip clip)
        {
            if (!TryGetClip(fretMask, out clip))
            {
                return false;
            }

            int fret = Array.IndexOf(FRET_KEYS, fretMask);
            clip = NextTake(clip);
            _phraseFretTotal += fret;
            _phraseFretCount++;
            FollowRun(fret);
            var (windowNote, _) = CurrentWindow();
            YargLogger.LogFormatDebug("[SoloFret] {0:0.00}s fret {1} note {2} window {3} root {4}",
                _songSeconds, fret, clip.Note, windowNote, _root);
            return true;
        }

        public bool TryPickClip(int fretMask, out BassStemClip clip)
        {
            if (fretMask == GuitarEngine.OPEN_MASK)
            {
                return TryGetRandomFretClip(out clip) || TryGetAnyClip(out clip);
            }

            if (TryTakeClip(fretMask, out clip))
            {
                return true;
            }

            for (int fret = 1; fret <= fretMask; fret <<= 1)
            {
                if ((fretMask & fret) != 0 && TryGetClip(fret, out clip))
                {
                    return true;
                }
            }

            return TryGetAnyClip(out clip);
        }

        private bool TryGetRandomFretClip(out BassStemClip clip)
        {
            clip = null!;
            int availableFrets = 0;
            for (int fretMask = 1; fretMask < GuitarEngine.OPEN_MASK; fretMask <<= 1)
            {
                if (!TryGetClip(fretMask, out var fretClip))
                {
                    continue;
                }

                availableFrets++;
                if (_random.Next(availableFrets) == 0)
                {
                    clip = fretClip;
                }
            }

            return availableFrets > 0;
        }

        public bool TryGetChordClips(int fretMask, List<BassStemClip> clips)
        {
            clips.Clear();
            int rootFret = fretMask & -fretMask;
            if (!TryTakeClip(rootFret, out var root))
            {
                return false;
            }

            clips.Add(root);
            BassStemClip? fifth = null;
            if (InScale(root.Note + FIFTH_SEMITONES))
            {
                fifth = LadderTake(root.Note + FIFTH_SEMITONES) ?? LadderTake(root.Note - FOURTH_SEMITONES);
            }

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
            int pitchClass = PitchClass(note);
            return PentatonicScale()[pitchClass] || ColorScale()?[pitchClass] == true;
        }

        internal static int FretCount(int fretMask)
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
            foreach (var clip in _ladder)
            {
                if (clip.Note == note)
                {
                    return NextTake(clip);
                }
            }

            return null;
        }

        private bool TryGetAnyClip(out BassStemClip clip)
        {
            if (_ladder.Count == 0)
            {
                clip = null!;
                return false;
            }

            clip = RungClip(0);
            return true;
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
            return _ladder[RungFor(fret)];
        }

        /// <summary>
        /// Locks the five frets to stable scale positions during an active phrase so that
        /// hitting the same fret repeatedly is guaranteed to sound the exact same note,
        /// and adjacent frets never share a pitch.
        /// </summary>
        private int RungFor(int fret)
        {
            fret = Math.Clamp(fret, 0, FRET_COUNT - 1);
            if (_songSeconds - _lastPlaySeconds > PHRASE_RESET_SECONDS)
            {
                UpdateFretRungs();
            }

            _lastPlaySeconds = _songSeconds;
            return _fretRungs[fret];
        }

        private void UpdateFretRungs()
        {
            var previous = BoxRungs();
            int targetRung = -1;
            if (_boxStart >= 0)
            {
                int shifted = Math.Clamp(_boxStart + PhraseShift(), 0, previous.Count - FRET_COUNT);
                targetRung = previous[shifted];
            }
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
                int distance = Math.Abs(_ladder[rungs[index]].Note - note);
                int nearestDistance = Math.Abs(_ladder[rungs[nearest]].Note - note);
                if (distance < nearestDistance)
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
                if (start >= 0 && start <= lastStart && PitchClass(_ladder[rungs[start]].Note) == _root)
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
                if (!pentatonic[PitchClass(_ladder[rungs[start + fret]].Note)])
                {
                    return true;
                }
            }

            return false;
        }

        private (int Note, int Fret) CurrentWindow()
        {
            if (_windows.Count == 0)
            {
                return (_ladder[_ladder.Count / 2].Note, FRET_COUNT / 2);
            }

            var (_, note, fret) = _windows[0];
            foreach (var window in _windows)
            {
                if (window.SongSeconds > _songSeconds)
                {
                    break;
                }

                (_, note, fret) = window;
            }

            return (note, fret);
        }

        private List<int> BoxRungs()
        {
            if (!_colorBox)
            {
                return ScaleRungs();
            }

            return ColorRungs() ?? ScaleRungs();
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
            if (ColorRungs() == null || colorScale == null)
            {
                return false;
            }

            if (_random.NextDouble() >= COLOR_BOX_CHANCE)
            {
                return false;
            }

            var pentatonic = PentatonicScale();
            if (_root >= 0)
            {
                for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
                {
                    bool isColor = colorScale[pitchClass] && !pentatonic[pitchClass];
                    bool isMinorSecond = PitchClass(pitchClass - _root) == 1;
                    if (isColor && isMinorSecond)
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
            if (_colorChecked)
            {
                return _colorScale;
            }

            _colorChecked = true;
            var pentatonic = PentatonicScale();
            var weights = PitchClassWeights();
            var outside = new List<int>();
            for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
            {
                int weight = weights[pitchClass];
                bool frequent = weight >= _anchors.Count * MIN_COLOR_SHARE && weight > 0;
                if (!pentatonic[pitchClass] && frequent)
                {
                    outside.Add(pitchClass);
                }
            }

            outside.Sort((left, right) => weights[right].CompareTo(weights[left]));
            var colors = new bool[PITCH_CLASSES];
            int added = 0;
            foreach (int pitchClass in outside)
            {
                if (added == MAX_COLOR_NOTES)
                {
                    break;
                }

                bool nextToColor = colors[PitchClass(pitchClass + 1)] || colors[PitchClass(pitchClass - 1)];
                if (nextToColor)
                {
                    continue;
                }

                colors[pitchClass] = true;
                added++;
            }

            if (added == 0)
            {
                return null;
            }

            for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
            {
                colors[pitchClass] |= pentatonic[pitchClass];
            }

            _colorScale = colors;
            return colors;
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

            var rungs = new List<int>(_ladder.Count);
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

            var rungs = new List<int>(_ladder.Count);
            CollectRungs(rungs, PentatonicScale());
            FilterConsecutiveSemitones(rungs);
            if (rungs.Count < FRET_COUNT)
            {
                rungs.Clear();
                for (int index = 0; index < _ladder.Count; index += 2)
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
                int semitones = _ladder[rungs[index]].Note - _ladder[rungs[index - 1]].Note;
                if (semitones < 2)
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

            var weights = PitchClassWeights();
            int bestRoot = 0;
            bool bestIsMinor = true;
            double bestScore = -1;

            for (int root = 0; root < PITCH_CLASSES; root++)
            {
                double minorScore = 0;
                foreach (int interval in MINOR_PENTATONIC)
                {
                    minorScore += weights[(root + interval) % PITCH_CLASSES];
                }

                if (minorScore > bestScore)
                {
                    bestScore = minorScore;
                    bestRoot = root;
                    bestIsMinor = true;
                }

                double majorScore = 0;
                foreach (int interval in MAJOR_PENTATONIC)
                {
                    majorScore += weights[(root + interval) % PITCH_CLASSES];
                }

                if (majorScore > bestScore)
                {
                    bestScore = majorScore;
                    bestRoot = root;
                    bestIsMinor = false;
                }
            }

            var scale = new bool[PITCH_CLASSES];
            int[] intervals = bestIsMinor ? MINOR_PENTATONIC : MAJOR_PENTATONIC;
            foreach (int interval in intervals)
            {
                scale[(bestRoot + interval) % PITCH_CLASSES] = true;
            }

            _pentatonicScale = scale;
            return scale;
        }

        private static int PitchClass(int note)
        {
            return (note % PITCH_CLASSES + PITCH_CLASSES) % PITCH_CLASSES;
        }

        private int[] PitchClassWeights()
        {
            if (_pitchClassWeights != null)
            {
                return _pitchClassWeights;
            }

            var weights = new int[PITCH_CLASSES];
            foreach (var (_, note) in _anchors)
            {
                weights[PitchClass(note)]++;
            }

            _pitchClassWeights = weights;
            return weights;
        }

        private void CollectRungs(List<int> rungs, bool[] good)
        {
            rungs.Clear();
            for (int index = 0; index < _ladder.Count; index++)
            {
                if (good[PitchClass(_ladder[index].Note)])
                {
                    rungs.Add(index);
                }
            }
        }
    }
}
