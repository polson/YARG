#nullable enable
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal static class BassSoloHarmony
    {
        public const int PITCH_CLASSES = 12;
        public const int FIFTH         = 7;

        private const double MIN_COLOR_SHARE = 0.03;
        private const int    MAX_COLOR_NOTES = 2;

        private static readonly int[] MINOR_PENTATONIC = new int[] { 0, 3, 5, 7, 10 };
        private static readonly int[] MAJOR_PENTATONIC = new int[] { 0, 2, 4, 7, 9 };

        public static int PitchClass(int note)
        {
            return (note % PITCH_CLASSES + PITCH_CLASSES) % PITCH_CLASSES;
        }

        public static bool[] PentatonicScale(IReadOnlyList<(double SongSeconds, int Note)> anchors)
        {
            var weights = new double[PITCH_CLASSES];
            foreach (var (_, note) in anchors)
            {
                weights[PitchClass(note)]++;
            }

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

        public static bool[]? ColorScale(IReadOnlyList<(double SongSeconds, int Note)> anchors, bool[] pentatonic)
        {
            var weights = new int[PITCH_CLASSES];
            foreach (var (_, note) in anchors)
            {
                weights[PitchClass(note)]++;
            }

            var outside = new List<int>();
            for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
            {
                if (!pentatonic[pitchClass] && weights[pitchClass] >= anchors.Count * MIN_COLOR_SHARE && weights[pitchClass] > 0)
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

                if (colors[PitchClass(pitchClass + 1)] || colors[PitchClass(pitchClass - 1)])
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

            return colors;
        }
    }
}
