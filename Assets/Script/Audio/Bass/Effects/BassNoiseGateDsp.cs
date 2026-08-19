#nullable enable
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    internal sealed class BassNoiseGateDsp : SafeHandleZeroOrMinusOneIsInvalid
    {
        private const string EFFECT_NAME = "native noise gate DSP";

        private BassNoiseGateDsp() : base(true)
        {
        }

        internal static BassNoiseGateDsp? Attach(int channelHandle, float threshold,
            float floorGain, float attackMs, float holdMs, float releaseMs, int priority = 0)
        {
            if (channelHandle == 0 || !IsFinite(threshold) || !IsFinite(floorGain) ||
                !IsFinite(attackMs) || !IsFinite(holdMs) || !IsFinite(releaseMs))
            {
                YargLogger.LogFormatError(
                    "Cannot attach {0}: channel={1}, threshold={2}, floor={3}, attack={4}, hold={5}, release={6}, priority={7}.",
                    EFFECT_NAME, channelHandle, threshold, floorGain, attackMs, holdMs, releaseMs, priority);
                return null;
            }

            try
            {
                uint nativeVersion = Native.GetAbiVersion();
                if (nativeVersion != BassHelpers.YARG_AUDIO_ABI_VERSION)
                {
                    YargLogger.LogError(
                        $"Cannot attach {EFFECT_NAME}: ABI mismatch managed={BassHelpers.YARG_AUDIO_ABI_VERSION}, " +
                        $"native={nativeVersion}, channel={channelHandle}, platform={PlatformDescription}.");
                    return null;
                }

                int result = Native.Attach(unchecked((uint) channelHandle), threshold, floorGain,
                    attackMs, holdMs, releaseMs, priority, out BassNoiseGateDsp dsp,
                    out int bassError);
                if (result == 0 && dsp != null && !dsp.IsInvalid)
                {
                    return dsp;
                }

                dsp?.Dispose();
                YargLogger.LogError(
                    $"Failed to attach {EFFECT_NAME}: result={result}, BASS={bassError}, " +
                    $"channel={channelHandle}, threshold={threshold}, floor={floorGain}, " +
                    $"attack={attackMs}, hold={holdMs}, release={releaseMs}, priority={priority}, " +
                    $"platform={PlatformDescription}.");
                return null;
            }
            catch (Exception exception) when (exception is DllNotFoundException or
                EntryPointNotFoundException or BadImageFormatException)
            {
                YargLogger.LogException(exception,
                    $"Failed to load {EFFECT_NAME} for channel {channelHandle} on {PlatformDescription}");
                return null;
            }
        }

        internal bool Reset()
        {
            if (IsClosed || IsInvalid)
            {
                return false;
            }

            try
            {
                return Native.Reset(this) == 0;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        internal bool SetThreshold(float threshold)
        {
            if (!IsFinite(threshold))
            {
                YargLogger.LogFormatError("Ignoring non-finite threshold for {0}: {1}.", EFFECT_NAME, threshold);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetThreshold(this, threshold) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        internal bool SetFloorGain(float floorGain)
        {
            if (!IsFinite(floorGain))
            {
                YargLogger.LogFormatError("Ignoring non-finite floorGain for {0}: {1}.", EFFECT_NAME, floorGain);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetFloorGain(this, floorGain) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        internal bool SetAttack(float attackMs)
        {
            if (!IsFinite(attackMs))
            {
                YargLogger.LogFormatError("Ignoring non-finite attackMs for {0}: {1}.", EFFECT_NAME, attackMs);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetAttack(this, attackMs) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        internal bool SetHold(float holdMs)
        {
            if (!IsFinite(holdMs))
            {
                YargLogger.LogFormatError("Ignoring non-finite holdMs for {0}: {1}.", EFFECT_NAME, holdMs);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetHold(this, holdMs) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        internal bool SetRelease(float releaseMs)
        {
            if (!IsFinite(releaseMs))
            {
                YargLogger.LogFormatError("Ignoring non-finite releaseMs for {0}: {1}.", EFFECT_NAME, releaseMs);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetRelease(this, releaseMs) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NoiseGateParams
        {
            public uint Size;
            public float Threshold;
            public float FloorGain;
            public float AttackMs;
            public float HoldMs;
            public float ReleaseMs;

            public NoiseGateParams(float threshold, float floorGain, float attackMs, float holdMs, float releaseMs)
            {
                Size = (uint) Marshal.SizeOf<NoiseGateParams>();
                Threshold = threshold;
                FloorGain = floorGain;
                AttackMs = attackMs;
                HoldMs = holdMs;
                ReleaseMs = releaseMs;
            }
        }

        internal bool SetParams(float threshold, float floorGain, float attackMs, float holdMs, float releaseMs)
        {
            return SetParams(new NoiseGateParams(threshold, floorGain, attackMs, holdMs, releaseMs));
        }

        internal bool SetParams(in NoiseGateParams parms)
        {
            if (!IsFinite(parms.Threshold) || !IsFinite(parms.FloorGain) || !IsFinite(parms.AttackMs) || !IsFinite(parms.HoldMs) || !IsFinite(parms.ReleaseMs))
            {
                YargLogger.LogFormatError("Ignoring non-finite NoiseGate params for {0}: threshold={1}, floor={2}, attack={3}, hold={4}, release={5}.", EFFECT_NAME, parms.Threshold, parms.FloorGain, parms.AttackMs, parms.HoldMs, parms.ReleaseMs);
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

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_attach",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Attach(uint channel, float threshold, float floorGain,
                float attackMs, float holdMs, float releaseMs, int priority,
                out BassNoiseGateDsp dsp, out int bassError);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_reset",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Reset(BassNoiseGateDsp dsp);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_set_threshold",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetThreshold(BassNoiseGateDsp dsp, float threshold);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_set_floor_gain",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetFloorGain(BassNoiseGateDsp dsp, float floorGain);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_set_attack",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetAttack(BassNoiseGateDsp dsp, float attackMs);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_set_hold",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetHold(BassNoiseGateDsp dsp, float holdMs);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_set_release",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetRelease(BassNoiseGateDsp dsp, float releaseMs);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_set_params",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetParams(BassNoiseGateDsp dsp, in NoiseGateParams parms);

            [DllImport(LIBRARY, EntryPoint = "yarg_noise_gate_dsp_destroy",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void Destroy(IntPtr dsp);
        }
    }
}
