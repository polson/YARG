#nullable enable
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    public sealed class BassDattorroReverbDsp : SafeHandleZeroOrMinusOneIsInvalid, IBassReverbDsp
    {
        private const string EFFECT_NAME = "native Dattorro reverb DSP";

        private BassDattorroReverbDsp() : base(true)
        {
        }

        public static BassDattorroReverbDsp? Create(int streamHandle, float dryMix, float wetMix,
            float roomSize, float damp, float width = 1, int priority = 0)
        {
            if (streamHandle == 0 || !IsFinite(dryMix) || !IsFinite(wetMix) ||
                !IsFinite(roomSize) || !IsFinite(damp) || !IsFinite(width))
            {
                YargLogger.LogFormatError(
                    "Cannot attach {0}: channel={1}, dry={2}, wet={3}, room={4}, damp={5}, width={6}, priority={7}.",
                    EFFECT_NAME, streamHandle, dryMix, wetMix, roomSize, damp, width, priority);
                return null;
            }

            try
            {
                uint nativeVersion = Native.GetAbiVersion();
                if (nativeVersion != BassHelpers.YARG_AUDIO_ABI_VERSION)
                {
                    YargLogger.LogError(
                        $"Cannot attach {EFFECT_NAME}: ABI mismatch managed={BassHelpers.YARG_AUDIO_ABI_VERSION}, " +
                        $"native={nativeVersion}, channel={streamHandle}, " +
                        $"platform={PlatformDescription}.");
                    return null;
                }

                int result = Native.Attach(unchecked((uint) streamHandle), dryMix, wetMix,
                    roomSize, damp, width, priority, out BassDattorroReverbDsp dsp,
                    out int bassError);
                if (result == 0 && dsp != null && !dsp.IsInvalid)
                {
                    return dsp;
                }

                dsp?.Dispose();
                YargLogger.LogError(
                    $"Failed to attach {EFFECT_NAME}: result={result}, BASS={bassError}, " +
                    $"channel={streamHandle}, dry={dryMix}, wet={wetMix}, room={roomSize}, " +
                    $"damp={damp}, width={width}, priority={priority}, " +
                    $"platform={PlatformDescription}.");
                return null;
            }
            catch (Exception exception) when (exception is DllNotFoundException or
                EntryPointNotFoundException or BadImageFormatException)
            {
                YargLogger.LogException(exception,
                    $"Failed to load {EFFECT_NAME} for channel {streamHandle} " +
                    $"on {PlatformDescription}");
                return null;
            }
        }

        public void RequestReset()
        {
            if (IsClosed || IsInvalid) return;
            try { Native.Reset(this); }
            catch (ObjectDisposedException) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DattorroReverbParams
        {
            public uint Size;
            public float DryMix;
            public float WetMix;
            public float RoomSize;
            public float Damp;
            public float Width;

            public DattorroReverbParams(float dryMix, float wetMix, float roomSize, float damp, float width)
            {
                Size = (uint) Marshal.SizeOf<DattorroReverbParams>();
                DryMix = dryMix;
                WetMix = wetMix;
                RoomSize = roomSize;
                Damp = damp;
                Width = width;
            }
        }

        public bool SetParams(float dryMix, float wetMix, float roomSize, float damp, float width)
        {
            return SetParams(new DattorroReverbParams(dryMix, wetMix, roomSize, damp, width));
        }

        public bool SetParams(in DattorroReverbParams parms)
        {
            if (!IsFinite(parms.DryMix) || !IsFinite(parms.WetMix) || !IsFinite(parms.RoomSize) || !IsFinite(parms.Damp) || !IsFinite(parms.Width))
            {
                YargLogger.LogFormatError("Ignoring non-finite Dattorro params for {0}: dry={1}, wet={2}, room={3}, damp={4}, width={5}.", EFFECT_NAME, parms.DryMix, parms.WetMix, parms.RoomSize, parms.Damp, parms.Width);
                return false;
            }
            if (IsClosed || IsInvalid) return false;
            try { return Native.SetParams(this, in parms) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        protected override bool ReleaseHandle()
        {
            Native.Destroy(handle);
            return true;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static string PlatformDescription =>
            $"{RuntimeInformation.OSDescription}/{RuntimeInformation.ProcessArchitecture}/{IntPtr.Size * 8}-bit";

        private static class Native
        {
            private const string LIBRARY = "yarg_audio";

            [DllImport(LIBRARY, EntryPoint = "yarg_audio_get_abi_version",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint GetAbiVersion();

            [DllImport(LIBRARY, EntryPoint = "yarg_dattorro_reverb_dsp_attach",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Attach(uint channel, float dryMix, float wetMix,
                float roomSize, float damp, float width, int priority,
                out BassDattorroReverbDsp dsp, out int bassError);

            [DllImport(LIBRARY, EntryPoint = "yarg_dattorro_reverb_dsp_reset",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Reset(BassDattorroReverbDsp dsp);

            [DllImport(LIBRARY, EntryPoint = "yarg_dattorro_reverb_dsp_set_params",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetParams(BassDattorroReverbDsp dsp, in DattorroReverbParams parms);

            [DllImport(LIBRARY, EntryPoint = "yarg_dattorro_reverb_dsp_destroy",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void Destroy(IntPtr dsp);
        }
    }
}
