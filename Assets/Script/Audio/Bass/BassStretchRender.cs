#nullable enable
using System;
using Microsoft.Win32.SafeHandles;
using YARG.Audio.BASS.Native;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStretchRender : SafeHandleZeroOrMinusOneIsInvalid
    {
        private BassStretchRender() : base(true)
        {
        }

        internal static bool IsAvailable()
        {
            try
            {
                if (!YargAudioNative.CheckAbi())
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return false;
            }

            using var probe = Create(channels: 1, frequency: 44100, speed: 1);
            return probe != null;
        }

        internal static BassStretchRender? Create(int channels, int frequency, float speed)
        {
            try
            {
                int result = YargAudioBindings.StretchRenderCreate((uint) channels, (uint) frequency, speed,
                    out var render);
                if (result != 0)
                {
                    render?.Dispose();
                    YargLogger.LogFormatError("Failed to create YargStretch renderer: {0}", result);
                    return null;
                }

                return render;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                or BadImageFormatException)
            {
                YargLogger.LogException(exception, "Failed to load YargStretch renderer");
                return null;
            }
        }

        public bool SetPitch(float factor, float baseHz)
        {
            return YargAudioBindings.StretchRenderSetPitch(this, factor, baseHz) == 0;
        }

        public unsafe bool Begin(float[] input, int channels, long startFrame)
        {
            fixed (float* source = input)
            {
                return YargAudioBindings.StretchRenderBegin(this, (IntPtr) source, input.Length / channels,
                    startFrame) == 0;
            }
        }

        public unsafe bool Read(float[] input, int channels, long outputFrame, float[] output)
        {
            fixed (float* source = input)
            fixed (float* destination = output)
            {
                return YargAudioBindings.StretchRenderRead(this, (IntPtr) source, input.Length / channels,
                    outputFrame, (IntPtr) destination, output.Length / channels) == 0;
            }
        }

        protected override bool ReleaseHandle()
        {
            YargAudioBindings.StretchRenderDestroy(handle);
            return true;
        }
    }
}
