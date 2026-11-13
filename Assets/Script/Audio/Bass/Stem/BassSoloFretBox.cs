#nullable enable
using System;
using System.Collections.Generic;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Maps the five frets onto the solo's note ladder for the current phrase, keeping repeated frets
    /// stable, adjacent frets apart and the box inside the solo's scale.
    /// </summary>
    internal sealed class BassSoloFretBox
    {
        private const int FRET_COUNT = BassSoloPhrase.FRET_COUNT;

        private static readonly int[] ROOT_OFFSETS  = { 0, -1, 1 };
        private static readonly int[] COLOR_OFFSETS = { 0, -1, 1, -2, 2 };

        private const int    BOX_SHIFT_RUNGS   = 3;
        private const int    EDGE_SHIFT_RUNGS  = 3;
        private const double LOW_FRET_AVERAGE  = 1.5;
        private const double HIGH_FRET_AVERAGE = 2.5;
        private const double COLOR_BOX_CHANCE  = 0.35;

        private const double PHRASE_RESET_SECONDS = 0.6;

        private readonly BassSoloScale _scale;
        private readonly Random        _random;
        private readonly IReadOnlyList<(double SongSeconds, int Note, int Fret)> _windows;

        private readonly int[]  _fretRungs = new int[FRET_COUNT];
        private          double _songSeconds;
        private          int    _root = -1;
        private          bool   _colorBox;
        private          int    _boxStart = -1;
        private          int    _phraseFretTotal;
        private          int    _phraseFretCount;
        private          int    _lastFret = -1;
        private          int    _fretBeforeLast = -1;
        private          int    _lastShift;
        private          double _lastPlaySeconds = double.MinValue;

        public BassSoloFretBox(BassSoloScale scale, IReadOnlyList<(double SongSeconds, int Note, int Fret)> windows,
            Random random)
        {
            _scale = scale;
            _windows = windows;
            _random = random;
        }

        public void SetPosition(double songSeconds, BassChordTrack? backingChords)
        {
            _songSeconds = songSeconds;
            _root = backingChords?.RootAt(songSeconds) ?? -1;
        }

        public BassStemClip RungClip(int fret)
        {
            return _scale.ClipAt(RungFor(fret));
        }

        public void NoteTaken(int fret, BassStemClip clip)
        {
            _phraseFretTotal += fret;
            _phraseFretCount++;
            FollowRun(fret);
            var (windowNote, _) = CurrentWindow();
            YargLogger.LogFormatDebug("[SoloFret] {0:0.00}s fret {1} note {2} window {3} root {4}",
                _songSeconds, fret, clip.Note, windowNote, _root);
        }

        /// <summary>
        /// Locks the five frets to stable scale positions during an active phrase so that
        /// hitting the same fret repeatedly is guaranteed to sound the exact same note,
        /// and adjacent frets never share a pitch.
        /// </summary>
        private int RungFor(int fret)
        {
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
                int distance = Math.Abs(_scale.NoteAt(rungs[index]) - note);
                int nearestDistance = Math.Abs(_scale.NoteAt(rungs[nearest]) - note);
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
                if (start >= 0 && start <= lastStart && BassSoloScale.PitchClass(_scale.NoteAt(rungs[start])) == _root)
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
            for (int fret = 0; fret < FRET_COUNT; fret++)
            {
                if (!_scale.Pentatonic[BassSoloScale.PitchClass(_scale.NoteAt(rungs[start + fret]))])
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
                return (_scale.NoteAt(_scale.Count / 2), FRET_COUNT / 2);
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
            return _colorBox ? _scale.ColorRungs! : _scale.ScaleRungs;
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
            var colorScale = _scale.ColorScale;
            if (colorScale == null)
            {
                return false;
            }

            if (_random.NextDouble() >= COLOR_BOX_CHANCE)
            {
                return false;
            }

            var pentatonic = _scale.Pentatonic;
            if (_root >= 0)
            {
                for (int pitchClass = 0; pitchClass < BassSoloScale.PITCH_CLASSES; pitchClass++)
                {
                    bool isColor = colorScale[pitchClass] && !pentatonic[pitchClass];
                    bool isMinorSecond = BassSoloScale.PitchClass(pitchClass - _root) == 1;
                    if (isColor && isMinorSecond)
                    {
                        return false;
                    }
                }
            }

            YargLogger.LogFormatDebug("[SoloFret] {0:0.00}s switching to the solo's color box", _songSeconds);
            return true;
        }
    }
}
