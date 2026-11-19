#nullable enable
using System;
using System.Collections.Generic;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Maps the five frets onto the solo's note ladder for the current phrase, keeping repeated frets
    /// stable, adjacent frets apart and the box inside the solo's scale. The box starts where the
    /// recorded solo begins and moves only when the player steps onto an edge fret.
    /// </summary>
    internal sealed class BassSoloFretBox
    {
        private const int FRET_COUNT = BassSoloPhrase.FRET_COUNT;

        private static readonly int[] ROOT_OFFSETS = { 0, -1, 1 };

        private const int EDGE_SHIFT_RUNGS = 2;

        private readonly BassSoloScale _scale;
        private readonly IReadOnlyList<(double SongSeconds, int Note, int Fret)> _windows;

        private double _songSeconds;
        private int    _boxStart;
        private int    _lastFret = -1;
        private int    _fretBeforeLast = -1;
        private bool   _soloFretsUsed;

        public BassSoloFretBox(BassSoloScale scale, IReadOnlyList<(double SongSeconds, int Note, int Fret)> windows,
            double startSeconds, BassChordTrack? backingChords)
        {
            _scale = scale;
            _windows = windows;
            if (scale.ScaleRungs.Count >= FRET_COUNT)
            {
                _boxStart = PlaceBox(startSeconds, backingChords);
            }
        }

        public int Start => _boxStart;

        public int LastStart => Math.Max(0, _scale.ScaleRungs.Count - FRET_COUNT);

        public void SetPosition(double songSeconds)
        {
            _songSeconds = songSeconds;
        }

        public BassStemClip RungClip(int fret)
        {
            if (fret >= FRET_COUNT && !_soloFretsUsed)
            {
                _soloFretsUsed = true;
                _boxStart = Math.Min(_boxStart, NormalLastStart);
            }

            return _scale.ClipAt(RungFor(fret));
        }

        private int NormalLastStart => _soloFretsUsed ? Math.Max(0, LastStart - FRET_COUNT) : LastStart;

        public void NoteTaken(int fret, BassStemClip clip)
        {
            if (fret < FRET_COUNT)
            {
                FollowFret(fret);
            }

            var (windowNote, _) = WindowAt(_songSeconds);
            YargLogger.LogFormatDebug("[SoloFret] {0:0.00}s fret {1} note {2} window {3} box {4}",
                _songSeconds, fret, clip.Note, windowNote, _boxStart);
        }

        /// <summary>
        /// Locks the five frets to stable scale positions between box shifts so that
        /// hitting the same fret repeatedly is guaranteed to sound the exact same note,
        /// and adjacent frets never share a pitch.
        /// </summary>
        private int RungFor(int fret)
        {
            return _scale.ScaleRungs[Math.Min(_boxStart + fret, _scale.ScaleRungs.Count - 1)];
        }

        private void FollowFret(int fret)
        {
            int beforePrevious = _fretBeforeLast;
            int previous = _lastFret;
            _fretBeforeLast = previous;
            _lastFret = fret;
            if (previous < 0)
            {
                return;
            }

            int top = FRET_COUNT - 1;
            if (fret == top && previous < top && beforePrevious != top)
            {
                _boxStart = Math.Min(_boxStart + EDGE_SHIFT_RUNGS, NormalLastStart);
            }
            else if (fret == 0 && previous > 0 && beforePrevious != 0)
            {
                _boxStart = Math.Max(_boxStart - EDGE_SHIFT_RUNGS, 0);
            }
        }

        private int PlaceBox(double songSeconds, BassChordTrack? backingChords)
        {
            int root = backingChords?.RootAt(songSeconds) ?? -1;
            var rungs = _scale.ScaleRungs;
            int lastStart = rungs.Count - FRET_COUNT;
            return RootAlignedStart(rungs, Math.Clamp(WindowStart(rungs, songSeconds), 0, lastStart), lastStart, root);
        }

        private int WindowStart(List<int> rungs, double songSeconds)
        {
            var (note, windowFret) = WindowAt(songSeconds);
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

        private int RootAlignedStart(List<int> rungs, int target, int lastStart, int root)
        {
            if (root < 0)
            {
                return target;
            }

            foreach (int offset in ROOT_OFFSETS)
            {
                int start = target + offset;
                if (start >= 0 && start <= lastStart && BassSoloScale.PitchClass(_scale.NoteAt(rungs[start])) == root)
                {
                    return start;
                }
            }

            return target;
        }

        private (int Note, int Fret) WindowAt(double songSeconds)
        {
            if (_windows.Count == 0)
            {
                return (_scale.NoteAt(_scale.Count / 2), FRET_COUNT / 2);
            }

            var (_, note, fret) = _windows[0];
            foreach (var window in _windows)
            {
                if (window.SongSeconds > songSeconds)
                {
                    break;
                }

                (_, note, fret) = window;
            }

            return (note, fret);
        }
    }
}
