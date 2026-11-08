#nullable enable
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    internal static class BassStemFretMask
    {
        public static int Count(int fretMask)
        {
            int count = 0;
            for (int fret = 1; fret < GuitarEngine.OPEN_MASK; fret <<= 1)
            {
                if ((fretMask & fret) != 0)
                {
                    count++;
                }
            }

            return count;
        }

        public static int Highest(int noteMask, int highestFret)
        {
            for (int fret = highestFret; fret >= 0; fret--)
            {
                int mask = 1 << fret;
                if ((noteMask & mask) != 0)
                {
                    return mask;
                }
            }

            return 0;
        }
    }
}
