#nullable enable
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// The pentatonic and colour scales measured from one solo, and the ladder rungs that belong to them.
    /// </summary>
    internal sealed class BassSoloScale
    {
        private const int FRET_COUNT = BassSoloPhrase.FRET_COUNT;

        private static readonly int[] MINOR_PENTATONIC = { 0, 3, 5, 7, 10 };
        private static readonly int[] MAJOR_PENTATONIC = { 0, 2, 4, 7, 9 };

        internal const int PITCH_CLASSES = 12;

        private const double MIN_COLOR_SHARE = 0.03;
        private const int    MAX_COLOR_NOTES = 2;

        private const int MAX_WHAMMY_BEND = 4;
        private const int DEFAULT_WHAMMY  = 2;

        private readonly IReadOnlyList<BassStemClip> _ladder;

        public BassSoloScale(IReadOnlyList<BassStemClip> ladder, IReadOnlyList<(double SongSeconds, int Note)> anchors)
        {
            _ladder = ladder;
            var weights = PitchClassWeights(anchors);
            Pentatonic = BestPentatonic(weights);
            ScaleRungs = PentatonicRungs();
            var colorScale = ColorScaleFrom(weights, anchors.Count);
            if (colorScale == null)
            {
                return;
            }

            var colorRungs = RungsIn(colorScale);
            if (colorRungs.Count < FRET_COUNT)
            {
                return;
            }

            ColorScale = colorScale;
            ColorRungs = colorRungs;
        }

        public bool[]     Pentatonic { get; }
        public bool[]?    ColorScale { get; }
        public List<int>  ScaleRungs { get; }
        public List<int>? ColorRungs { get; }

        public int Count => _ladder.Count;

        public int NoteAt(int index)
        {
            return _ladder[index].Note;
        }

        public BassStemClip ClipAt(int index)
        {
            return _ladder[index];
        }

        public bool InScale(int note)
        {
            int pitchClass = PitchClass(note);
            return Pentatonic[pitchClass] || ColorScale?[pitchClass] == true;
        }

        public int WhammySemitones(BassStemClip clip)
        {
            for (int semitones = 1; semitones <= MAX_WHAMMY_BEND; semitones++)
            {
                if (Pentatonic[PitchClass(clip.Note + semitones)])
                {
                    return semitones;
                }
            }

            return DEFAULT_WHAMMY;
        }

        public static int PitchClass(int note)
        {
            return (note % PITCH_CLASSES + PITCH_CLASSES) % PITCH_CLASSES;
        }

        private List<int> PentatonicRungs()
        {
            var rungs = RungsIn(Pentatonic);
            FilterConsecutiveSemitones(rungs);
            if (rungs.Count >= FRET_COUNT)
            {
                return rungs;
            }

            rungs.Clear();
            for (int index = 0; index < _ladder.Count; index += 2)
            {
                rungs.Add(index);
            }

            return rungs;
        }

        private static bool[] BestPentatonic(int[] weights)
        {
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

            return scale;
        }

        private bool[]? ColorScaleFrom(int[] weights, int anchorCount)
        {
            var outside = new List<int>();
            for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
            {
                int weight = weights[pitchClass];
                bool frequent = weight >= anchorCount * MIN_COLOR_SHARE && weight > 0;
                if (!Pentatonic[pitchClass] && frequent)
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
                colors[pitchClass] |= Pentatonic[pitchClass];
            }

            return colors;
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

        private static int[] PitchClassWeights(IReadOnlyList<(double SongSeconds, int Note)> anchors)
        {
            var weights = new int[PITCH_CLASSES];
            foreach (var (_, note) in anchors)
            {
                weights[PitchClass(note)]++;
            }

            return weights;
        }

        private List<int> RungsIn(bool[] scale)
        {
            var rungs = new List<int>(_ladder.Count);
            for (int index = 0; index < _ladder.Count; index++)
            {
                if (scale[PitchClass(_ladder[index].Note)])
                {
                    rungs.Add(index);
                }
            }

            return rungs;
        }
    }
}
