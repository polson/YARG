namespace YARG.Audio.BASS
{
    internal enum BassOneShotBackend
    {
        Burst,
        Native
    }

    internal static class BassOneShotBackendSelection
    {
#if YARG_AUDIO_ONE_SHOT_BURST
        // Explicit A/B or rollback build. Native failures never fall back here.
        internal const BassOneShotBackend Current = BassOneShotBackend.Burst;
#else
        // Native path is default. Native failures never fall back to Burst.
        internal const BassOneShotBackend Current = BassOneShotBackend.Native;
#endif
    }
}
