#nullable enable
namespace YARG.Audio.BASS
{
    /// <summary>
    /// The stem audio a freestyle solo drives. Narrowed from the soundboard so the solo input model
    /// can be driven without BASS.
    /// </summary>
    internal interface IFreestyleSoloAudio
    {
        bool HasSeedSet(double soloTime);
        void SelectSeedSet(double soloTime, double songTime);
        void Play(int key, int semitones, double gain);
        void PlayStrum(int fretMask, int semitones, double gain);
        void SetHeldFrets(int fretMask);
        void SetWhammyPitch(float percent);
    }
}
