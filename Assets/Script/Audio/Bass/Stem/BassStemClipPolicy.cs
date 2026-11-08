#nullable enable
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    internal static class BassStemClipPolicy
    {
        public static bool TryGetSeedClip(IBassStemClipSource source, int key, out BassStemClip clip)
        {
            return source.TryGetClip(key, out clip) ||
                source.TryGetFretClip(key, out clip) ||
                source.TryGetAnyClip(out clip);
        }

        public static bool TryGetRecordedClip(IBassStemClipSource source, int key, out BassStemClip clip)
        {
            if (source.TryGetClip(key, out clip))
            {
                return true;
            }

            if (IsSingleFret(key))
            {
                return false;
            }

            return source.TryGetFretClip(key, out clip) || source.TryGetAnyClip(out clip);
        }

        public static int FretCount(int fretMask)
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

        public static int HighestFret(int noteMask, int highestFret)
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

        private static bool IsSingleFret(int key)
        {
            return (key & (key - 1)) == 0;
        }
    }
}
