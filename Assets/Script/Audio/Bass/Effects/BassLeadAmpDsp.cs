#nullable enable
using System;
using YARG.Audio.BASS.Native;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    internal sealed class BassLeadAmpDsp : NativeDspHandle
    {
        private const string EFFECT_NAME = "native lead amp DSP";

        private BassLeadAmpDsp()
        {
        }

        internal static BassLeadAmpDsp? Attach(int channelHandle, float gain, float[]? cabinet, int cabinetRate,
            int priority = 0)
        {
            if (channelHandle == 0 || !YargAudioNative.IsFinite(gain))
            {
                YargLogger.LogFormatError("Cannot attach {0}: channel={1}, gain={2}, priority={3}.",
                    EFFECT_NAME, channelHandle, gain, priority);
                return null;
            }

            return YargAudioNative.Attach(EFFECT_NAME, channelHandle,
                (out BassLeadAmpDsp handle, out int bassError) =>
                    YargAudioBindings.LeadAmpDspAttach(channelHandle, gain, cabinet, cabinet?.Length ?? 0,
                        cabinetRate, priority, out handle, out bassError));
        }

        internal static BassLeadAmpTone? Match(int sampleRate, float[]? cabinet, int cabinetRate, float[] stem, float[] di,
            uint[] noteFrames)
        {
            try
            {
                if (!YargAudioNative.CheckAbi())
                {
                    return null;
                }

                var bands = new float[BassLeadAmpTone.BAND_COUNT];
                int result = YargAudioBindings.LeadAmpMatch(sampleRate, cabinet, cabinet?.Length ?? 0, cabinetRate, stem,
                    di, noteFrames, noteFrames.Length, out float drive, bands, bands.Length, out float levelGain);
                if (result != 0)
                {
                    YargLogger.LogFormatInfo("Lead amp tone match skipped for {0} notes: result {1}",
                        noteFrames.Length, result);
                    return null;
                }

                return new BassLeadAmpTone(drive, bands, levelGain);
            }
            catch (Exception exception) when (exception is DllNotFoundException or
                EntryPointNotFoundException or BadImageFormatException)
            {
                YargLogger.LogException(exception, "Failed to load the lead amp tone match");
                return null;
            }
        }

        internal bool SetTone(BassLeadAmpTone tone)
        {
            return YargAudioNative.TryInvoke(this,
                handle => YargAudioBindings.LeadAmpDspSetTone(handle, tone.Drive, tone.BandsDb, tone.BandsDb.Length));
        }

        internal bool SetGain(float gain)
        {
            if (!YargAudioNative.IsFinite(gain))
            {
                YargLogger.LogFormatError("Ignoring non-finite gain for {0}: {1}.", EFFECT_NAME, gain);
                return false;
            }

            return YargAudioNative.TryInvoke(this, handle => YargAudioBindings.LeadAmpDspSetGain(handle, gain));
        }

        protected override void Destroy(IntPtr handle) =>
            YargAudioBindings.LeadAmpDspDestroy(handle);
    }

    internal sealed class BassLeadAmpTone
    {
        internal const int BAND_COUNT = 9;

        internal static readonly BassLeadAmpTone Default = new(1, new float[BAND_COUNT], 1);

        public readonly float   Drive;
        public readonly float[] BandsDb;
        public readonly float   LevelGain;

        internal BassLeadAmpTone(float drive, float[] bandsDb, float levelGain)
        {
            Drive = drive;
            BandsDb = bandsDb;
            LevelGain = levelGain;
        }
    }
}
