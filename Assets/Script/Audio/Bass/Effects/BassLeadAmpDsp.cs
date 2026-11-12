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
}
