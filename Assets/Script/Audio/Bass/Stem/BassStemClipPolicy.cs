#nullable enable
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

        private static bool IsSingleFret(int key)
        {
            return (key & (key - 1)) == 0;
        }
    }
}
