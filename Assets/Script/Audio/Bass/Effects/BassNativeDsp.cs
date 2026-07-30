using System;
using System.Runtime.InteropServices;
using ManagedBass;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    /// <summary>
    /// Native callback signature used by BASS DSP procedures compiled with Burst.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate void BassNativeDspProcedure(
        int dspHandle, int channelHandle, void* buffer, int length, void* user);

    /// <summary>
    /// Owns a native BASS DSP registration and its unmanaged callback context.
    /// </summary>
    public abstract class BassNativeDsp : IDisposable
    {
        [DllImport("bass", EntryPoint = "BASS_ChannelSetDSP",
            CallingConvention = CallingConvention.Winapi)]
        private static extern int NativeChannelSetDsp(
            int channelHandle, IntPtr procedure, IntPtr user, int priority);

        [DllImport("bass", EntryPoint = "BASS_ChannelRemoveDSP",
            CallingConvention = CallingConvention.Winapi)]
        private static extern int NativeChannelRemoveDsp(int channelHandle, int dspHandle);

        protected readonly object LifecycleLock = new object();

        private readonly int _channelHandle;
        private readonly string _effectName;

        private IntPtr _context;
        private int _dspHandle;
        private bool _disposed;

        protected bool IsDisposed => _disposed;
        protected IntPtr Context => _context;

        internal BassNativeDsp(int channelHandle, int dspHandle, IntPtr context, string effectName)
        {
            _channelHandle = channelHandle;
            _dspHandle = dspHandle;
            _context = context;
            _effectName = effectName;
        }

        protected static bool TryGetFloatChannelFormat(int channelHandle, string effectName,
            out int frequency, out int channelCount)
        {
            var info = Bass.ChannelGetInfo(channelHandle);
            frequency = info.Frequency;
            channelCount = info.Channels;
            if (frequency <= 0 || channelCount <= 0)
            {
                YargLogger.LogFormatError("Failed to query channel format for {0}: {1}",
                    effectName, Bass.LastError);
                return false;
            }

            if ((info.Flags & BassFlags.Float) == 0 && !Bass.FloatingPointDSP)
            {
                YargLogger.LogFormatError(
                    "{0} requires a floating-point channel or BASS floating-point DSP mode.",
                    effectName);
                return false;
            }

            return true;
        }

        protected static bool TryAttach(int channelHandle, IntPtr procedure, IntPtr context,
            int priority, string effectName, out int dspHandle)
        {
            dspHandle = NativeChannelSetDsp(channelHandle, procedure, context, priority);
            if (dspHandle != 0)
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to attach {0}: {1}", effectName, Bass.LastError);
            return false;
        }

        protected abstract void FreeContext(IntPtr context);

        public void Dispose()
        {
            lock (LifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                // Locking waits for current processing and prevents another callback from starting
                // while its unmanaged context is detached and freed.
                if (!Bass.ChannelLock(_channelHandle))
                {
                    YargLogger.LogFormatError(
                        "Failed to lock channel while removing {0}: {1}", _effectName, Bass.LastError);
                    return;
                }

                try
                {
                    if (NativeChannelRemoveDsp(_channelHandle, _dspHandle) == 0)
                    {
                        // Retain the context if removal fails. A leak is safer than a callback
                        // accessing freed memory.
                        YargLogger.LogFormatError("Failed to remove {0}: {1}",
                            _effectName, Bass.LastError);
                        return;
                    }

                    _dspHandle = 0;
                    FreeContext(_context);
                    _context = IntPtr.Zero;
                    _disposed = true;
                }
                finally
                {
                    if (!Bass.ChannelLock(_channelHandle, false))
                    {
                        YargLogger.LogFormatError(
                            "Failed to unlock channel after removing {0}: {1}",
                            _effectName, Bass.LastError);
                    }
                }
            }
        }
    }
}
