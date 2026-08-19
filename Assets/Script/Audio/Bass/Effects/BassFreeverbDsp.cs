#nullable enable
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    /// <summary>
    /// Owns a Freeverb DSP implemented and attached entirely by the YargAudio native plugin.
    /// The BASS channel passed to <see cref="Create"/> must outlive this handle.
    /// </summary>
    public sealed class BassFreeverbDsp : SafeHandleZeroOrMinusOneIsInvalid
    {
        private const string EFFECT_NAME = "native Freeverb DSP";

        private BassFreeverbDsp() : base(true)
        {
        }

        /// <summary>
        /// Creates and attaches native Freeverb to a BASS stream.
        /// </summary>
        /// <param name="streamHandle">BASS stream receiving effect.</param>
        /// <param name="dryMix">Dry level, clamped to [0, 1].</param>
        /// <param name="wetMix">Wet level, clamped to [0, 3].</param>
        /// <param name="roomSize">Reverb decay control, clamped to [0, 1].</param>
        /// <param name="damp">High-frequency damping, clamped to [0, 1].</param>
        /// <param name="width">Stereo width, clamped to [0, 1].</param>
        /// <param name="priority">DSP priority. Higher values run earlier.</param>
        /// <returns>Attached DSP, or <c>null</c> if creation fails.</returns>
        public static BassFreeverbDsp? Create(int streamHandle, float dryMix, float wetMix,
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
                    roomSize, damp, width, priority, out BassFreeverbDsp dsp,
                    out int bassError);
                if (result == 0 && dsp != null && !dsp.IsInvalid)
                {
                    return dsp;
                }

                // Native initializes output to null on failure. Dispose unexpected handle so
                // partial success cannot leak through this path.
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

        /// <summary>
        /// Clears delay and filter state during next native BASS DSP callback.
        /// </summary>
        public void RequestReset()
        {
            if (IsClosed || IsInvalid)
            {
                return;
            }

            try
            {
                Native.Reset(this);
            }
            catch (ObjectDisposedException)
            {
                // Disposal won race with reset request.
            }
        }

        public bool SetDryMix(float dryMix)
        {
            if (!IsFinite(dryMix))
            {
                YargLogger.LogFormatError("Ignoring non-finite dryMix for {0}: {1}.", EFFECT_NAME, dryMix);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetDryMix(this, dryMix) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        public bool SetWetMix(float wetMix)
        {
            if (!IsFinite(wetMix))
            {
                YargLogger.LogFormatError("Ignoring non-finite wetMix for {0}: {1}.", EFFECT_NAME, wetMix);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetWetMix(this, wetMix) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        public bool SetRoomSize(float roomSize)
        {
            if (!IsFinite(roomSize))
            {
                YargLogger.LogFormatError("Ignoring non-finite roomSize for {0}: {1}.", EFFECT_NAME, roomSize);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetRoomSize(this, roomSize) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        public bool SetDamp(float damp)
        {
            if (!IsFinite(damp))
            {
                YargLogger.LogFormatError("Ignoring non-finite damp for {0}: {1}.", EFFECT_NAME, damp);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetDamp(this, damp) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        public bool SetWidth(float width)
        {
            if (!IsFinite(width))
            {
                YargLogger.LogFormatError("Ignoring non-finite width for {0}: {1}.", EFFECT_NAME, width);
                return false;
            }

            if (IsClosed || IsInvalid) return false;
            try { return Native.SetWidth(this, width) == 0; }
            catch (ObjectDisposedException) { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FreeverbParams
        {
            public uint Size;
            public float DryMix;
            public float WetMix;
            public float RoomSize;
            public float Damp;
            public float Width;

            public FreeverbParams(float dryMix, float wetMix, float roomSize, float damp, float width)
            {
                Size = (uint) Marshal.SizeOf<FreeverbParams>();
                DryMix = dryMix;
                WetMix = wetMix;
                RoomSize = roomSize;
                Damp = damp;
                Width = width;
            }
        }

        public bool SetParams(float dryMix, float wetMix, float roomSize, float damp, float width)
        {
            return SetParams(new FreeverbParams(dryMix, wetMix, roomSize, damp, width));
        }

        public bool SetParams(in FreeverbParams parms)
        {
            if (!IsFinite(parms.DryMix) || !IsFinite(parms.WetMix) || !IsFinite(parms.RoomSize) || !IsFinite(parms.Damp) || !IsFinite(parms.Width))
            {
                YargLogger.LogFormatError("Ignoring non-finite Freeverb params for {0}: dry={1}, wet={2}, room={3}, damp={4}, width={5}.", EFFECT_NAME, parms.DryMix, parms.WetMix, parms.RoomSize, parms.Damp, parms.Width);
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

        // Thread-safe .NET equivalents of Unity's Application.platform/SystemInfo.processorType.
        // Attach runs from background threads (e.g. music player audio load), where Unity APIs throw.
        private static string PlatformDescription =>
            $"{RuntimeInformation.OSDescription}/{RuntimeInformation.ProcessArchitecture}/{IntPtr.Size * 8}-bit";

        private static class Native
        {
            private const string LIBRARY = "yarg_audio";

            [DllImport(LIBRARY, EntryPoint = "yarg_audio_get_abi_version",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint GetAbiVersion();

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_attach",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Attach(uint channel, float dryMix, float wetMix,
                float roomSize, float damp, float width, int priority,
                out BassFreeverbDsp dsp, out int bassError);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_reset",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Reset(BassFreeverbDsp dsp);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_set_dry_mix",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetDryMix(BassFreeverbDsp dsp, float dryMix);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_set_wet_mix",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetWetMix(BassFreeverbDsp dsp, float wetMix);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_set_room_size",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetRoomSize(BassFreeverbDsp dsp, float roomSize);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_set_damp",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetDamp(BassFreeverbDsp dsp, float damp);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_set_width",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetWidth(BassFreeverbDsp dsp, float width);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_set_params",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetParams(BassFreeverbDsp dsp, in FreeverbParams parms);

            [DllImport(LIBRARY, EntryPoint = "yarg_freeverb_dsp_destroy",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void Destroy(IntPtr dsp);
        }
    }
}
