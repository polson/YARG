#nullable enable
using System;
using YARG.Audio.BASS.Native;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    internal sealed class BassOverdriveDsp : NativeDspHandle
    {
        private const string EFFECT_NAME = "native overdrive DSP";

        private BassOverdriveDsp()
        {
        }

        internal static BassOverdriveDsp? Attach(int channelHandle, float mix, float drive, int priority = 0)
        {
            if (channelHandle == 0 || !YargAudioNative.AreFinite(mix, drive))
            {
                YargLogger.LogFormatError("Cannot attach {0}: channel={1}, mix={2}, drive={3}, priority={4}.",
                    EFFECT_NAME, channelHandle, mix, drive, priority);
                return null;
            }

            return YargAudioNative.Attach(EFFECT_NAME, channelHandle,
                (out BassOverdriveDsp handle, out int bassError) =>
                    YargAudioBindings.OverdriveDspAttach(channelHandle, mix, drive, priority, out handle, out bassError));
        }

        internal bool Set(float mix, float drive)
        {
            if (!YargAudioNative.AreFinite(mix, drive))
            {
                YargLogger.LogFormatError("Ignoring non-finite overdrive params for {0}: mix={1}, drive={2}.",
                    EFFECT_NAME, mix, drive);
                return false;
            }

            return YargAudioNative.TryInvoke(this, handle => YargAudioBindings.OverdriveDspSet(handle, mix, drive));
        }

        protected override void Destroy(IntPtr handle) =>
            YargAudioBindings.OverdriveDspDestroy(handle);
    }
}
