namespace YARG.Audio.BASS
{
    internal static class BassSoloHarmony
    {
        public const int PITCH_CLASSES = 12;
        public const int MINOR_THIRD   = 3;
        public const int MAJOR_THIRD   = 4;
        public const int TRITONE       = 6;
        public const int FIFTH         = 7;

        private static readonly int[] MAJOR_SCALE = { 0, 2, 4, 5, 7, 9, 11 };

        public static int PitchClass(int note)
        {
            return (note % PITCH_CLASSES + PITCH_CLASSES) % PITCH_CLASSES;
        }

        /// <summary>
        /// The diatonic set that carries the most of the measured weight. All seven modes of a major
        /// scale share one set, so the tonic does not matter here: only which seven notes are played.
        /// </summary>
        public static bool[] DiatonicSet(double[] weights)
        {
            var best = new bool[PITCH_CLASSES];
            double bestCoverage = double.MinValue;
            double bestTonic = double.MinValue;
            for (int root = 0; root < PITCH_CLASSES; root++)
            {
                var scale = PitchClasses(root);
                double coverage = 0;
                for (int pitchClass = 0; pitchClass < PITCH_CLASSES; pitchClass++)
                {
                    if (scale[pitchClass])
                    {
                        coverage += weights[pitchClass];
                    }
                }

                if (coverage > bestCoverage || coverage == bestCoverage && weights[root] > bestTonic)
                {
                    bestCoverage = coverage;
                    bestTonic = weights[root];
                    best = scale;
                }
            }

            return best;
        }

        public static int Third(bool[] key, int root)
        {
            int minor = (root + MINOR_THIRD) % PITCH_CLASSES;
            int major = (root + MAJOR_THIRD) % PITCH_CLASSES;
            if (key[minor] && !key[major])
            {
                return minor;
            }

            if (key[major])
            {
                return major;
            }

            return key[minor] ? minor : -1;
        }

        public static bool[] ChordNotes(bool[] key, int root, int third, int fifth)
        {
            var notes = (bool[]) key.Clone();
            notes[(root + 1) % PITCH_CLASSES] = false;
            notes[(fifth + 1) % PITCH_CLASSES] = false;
            notes[(root + TRITONE) % PITCH_CLASSES] = false;
            if (third >= 0)
            {
                int otherThird = third == (root + MINOR_THIRD) % PITCH_CLASSES
                    ? (root + MAJOR_THIRD) % PITCH_CLASSES
                    : (root + MINOR_THIRD) % PITCH_CLASSES;
                notes[otherThird] = false;
                notes[(third + 1) % PITCH_CLASSES] = false;
                notes[third] = true;
            }

            notes[root] = true;
            notes[fifth] = true;
            return notes;
        }

        private static bool[] PitchClasses(int root)
        {
            var pitchClasses = new bool[PITCH_CLASSES];
            foreach (int step in MAJOR_SCALE)
            {
                pitchClasses[(root + step) % PITCH_CLASSES] = true;
            }

            return pitchClasses;
        }
    }
}
