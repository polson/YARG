namespace YARG.Audio.BASS
{
    internal enum BassOneShotBackend
    {
        Burst,
        Native
    }

    internal static class BassOneShotBackendSelection
    {
        // Native path active for controlled runtime testing; failures do not fall back.
        internal const BassOneShotBackend Current = BassOneShotBackend.Native;
    }
}
