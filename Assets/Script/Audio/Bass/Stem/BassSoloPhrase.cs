#nullable enable
using System;
using System.Collections.Generic;
using YARG.Audio.BASS.Effects;
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// The prepared audio material for one freestyle solo: its note ladder, tone and gain, and the
    /// clip picked for each fret the player sounds.
    /// </summary>
    internal sealed class BassSoloPhrase
    {
        public const int FRET_COUNT = 5;

        private static readonly int[] FRET_KEYS = { 1, 2, 4, 8, 16 };

        private const int FIFTH_SEMITONES  = 7;
        private const int FOURTH_SEMITONES = 5;
        private const int OCTAVE_SEMITONES = 12;
        private const int FULL_CHORD_FRETS = 3;

        public readonly double          StartTime;
        public readonly double          EndTime;
        public readonly BassLeadAmpTone Tone;
        public readonly double          SynthGain;
        public          bool            IsEmpty => _ladder.Count == 0;

        private readonly List<BassStemClip>            _ladder;
        private readonly Dictionary<BassStemClip, int> _lastTakes = new();
        private readonly Random                        _random = new();
        private readonly BassSoloScale                 _scale;
        private readonly BassSoloFretBox               _box;

        public BassSoloPhrase(double startTime, double endTime, List<BassStemClip> ladder,
            IReadOnlyList<(double SongSeconds, int Note, int Fret)> windows,
            IReadOnlyList<(double SongSeconds, int Note)> anchors, BassLeadAmpTone tone, double synthGain)
        {
            StartTime = startTime;
            EndTime = endTime;
            _ladder = ladder;
            Tone = tone;
            SynthGain = synthGain;
            _scale = new BassSoloScale(ladder, anchors);
            _box = new BassSoloFretBox(_scale, windows, _random);
        }

        public bool Covers(double soloTime)
        {
            return soloTime >= StartTime && soloTime <= EndTime;
        }

        public void SetPosition(double songSeconds, BassChordTrack? backingChords)
        {
            _box.SetPosition(songSeconds, backingChords);
        }

        public int WhammySemitones(BassStemClip clip)
        {
            return _scale.WhammySemitones(clip);
        }

        internal static int FretIndex(int fretMask)
        {
            return Array.IndexOf(FRET_KEYS, fretMask);
        }

        private bool TryGetClip(int fretMask, out BassStemClip clip)
        {
            int fret = FretIndex(fretMask);
            if (fret < 0 || _ladder.Count == 0)
            {
                clip = null!;
                return false;
            }

            clip = _box.RungClip(fret);
            return true;
        }

        private bool TryTakeClip(int fretMask, out BassStemClip clip)
        {
            int fret = FretIndex(fretMask);
            if (!TryGetClip(fretMask, out clip))
            {
                return false;
            }

            clip = NextTake(clip);
            _box.NoteTaken(fret, clip);
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
            if (_scale.InScale(root.Note + FIFTH_SEMITONES))
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

            clip = _box.RungClip(0);
            return true;
        }

        private BassStemClip NextTake(BassStemClip clip)
        {
            _lastTakes.TryGetValue(clip, out int lastTake);
            var taken = clip.TakeAfter(lastTake, _random, out int take);
            _lastTakes[clip] = take;
            return taken;
        }
    }
}
