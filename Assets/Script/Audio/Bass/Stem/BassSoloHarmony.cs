using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal static class BassSoloHarmony
    {
        public const int PITCH_CLASSES = 12;
        public const int FIFTH         = 7;

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
    }
}
