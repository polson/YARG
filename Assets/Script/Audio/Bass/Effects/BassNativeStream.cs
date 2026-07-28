using System;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate int BassNativeStreamProcedure(int streamHandle, void* buffer, int length, void* user);

    internal static class BassNativeStream
    {
        [DllImport("bass", EntryPoint = "BASS_StreamCreate", CallingConvention = CallingConvention.Winapi)]
        private static extern int NativeStreamCreate(int frequency, int channels, BassFlags flags,
            IntPtr procedure, IntPtr user);

        internal static int Create(int frequency, int channels, IntPtr procedure, IntPtr user)
        {
            return NativeStreamCreate(frequency, channels,
                BassFlags.Float | BassFlags.Decode, procedure, user);
        }

        internal static bool AddToMixer(int mixer, int stream)
        {
            return BassMix.MixerAddChannel(mixer, stream, BassFlags.MixerChanNoRampin);
        }

        internal static void RemoveFromMixer(int stream)
        {
            if (stream != 0 && !BassMix.MixerRemoveChannel(stream) && Bass.LastError != Errors.Handle)
                YargLogger.LogFormatError("Failed to remove one-shot stream: {0}", Bass.LastError);
        }
    }
}
