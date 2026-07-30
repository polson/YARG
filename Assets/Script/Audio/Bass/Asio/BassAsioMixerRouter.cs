#nullable enable
using System;
using System.Runtime.InteropServices;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal enum AsioMixerRouterState : uint
    {
        Created,
        Attached,
        Prefilling,
        Ready,
        Running,
        Starved,
        SourceFailed,
        Stopping,
        Stopped,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AsioMixerRouterStats
    {
        public uint Size;
        public AsioMixerRouterState State;
        public int LastError;
        public uint QueuedFrames;
        public uint MinimumQueuedFrames;
        public ulong ProducedFrames;
        public ulong ConsumedSongFrames;
        public ulong RequestedOutputFrames;
        public ulong UnderrunFrames;
        public ulong UnderrunEvents;
        public ulong MaximumRenderNanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AsioMixerRouterConfig
    {
        public uint Size;
        public int BassDeviceId;
        public uint SampleRate;
        public uint Channels;
        public uint CallbackFrames;
    }

    /// <summary>
    /// Owns native router state. ASIO must be stopped before this object is disposed.
    /// Mixer handles are borrowed and must outlive this object.
    /// </summary>
    internal sealed class BassAsioMixerRouter : IDisposable
    {
        private const uint ABI_VERSION = 1;

        private IntPtr _handle;

        private BassAsioMixerRouter(IntPtr handle)
        {
            _handle = handle;
        }

        public static BassAsioMixerRouter? Create(int bassDeviceId, int sampleRate,
            int channels, int callbackFrames)
        {
            if (sampleRate <= 0 || channels <= 0 || callbackFrames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                uint nativeVersion = Native.GetAbiVersion();
                if (nativeVersion != ABI_VERSION)
                {
                    YargLogger.LogFormatError(
                        "YargAudio ABI mismatch: managed={0}, native={1}",
                        ABI_VERSION, nativeVersion);
                    return null;
                }

                var config = new AsioMixerRouterConfig
                {
                    Size = (uint) Marshal.SizeOf<AsioMixerRouterConfig>(),
                    BassDeviceId = bassDeviceId,
                    SampleRate = checked((uint) sampleRate),
                    Channels = checked((uint) channels),
                    CallbackFrames = checked((uint) callbackFrames),
                };

                int result = Native.Create(in config, out IntPtr handle);
                if (result != 0 || handle == IntPtr.Zero)
                {
                    YargLogger.LogFormatError("Failed to create native ASIO router: {0}", result);
                    return null;
                }
                return new BassAsioMixerRouter(handle);
            }
            catch (Exception exception) when (exception is DllNotFoundException or
                EntryPointNotFoundException or BadImageFormatException)
            {
                YargLogger.LogException(exception, "Failed to load YargAudio native plugin");
                return null;
            }
#else
            return null;
#endif
        }

        public bool AttachMixer(int mixerHandle, int bufferMilliseconds)
        {
            ThrowIfDisposed();
            // BASS handles are DWORDs exposed by ManagedBass as signed ints. High-bit handles
            // are valid and must be passed to native code without numeric conversion checks.
            if (mixerHandle == 0 || bufferMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mixerHandle));
            }
            return Native.AttachMixer(_handle, unchecked((uint) mixerHandle),
                checked((uint) bufferMilliseconds)) == 0;
        }

        public bool Prefill(int mixerHandle, int timeoutMilliseconds)
        {
            ThrowIfDisposed();
            if (mixerHandle == 0 || timeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mixerHandle));
            }
            return Native.Prefill(_handle, unchecked((uint) mixerHandle),
                checked((uint) timeoutMilliseconds)) == 0;
        }

        public bool EnableOutput(int firstAsioChannel)
        {
            ThrowIfDisposed();
            if (firstAsioChannel < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(firstAsioChannel));
            }
            return Native.EnableOutput(_handle, checked((uint) firstAsioChannel)) == 0;
        }

        public bool FlushMixer(int mixerHandle)
        {
            ThrowIfDisposed();
            if (mixerHandle == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mixerHandle));
            }
            return Native.FlushMixer(_handle, unchecked((uint) mixerHandle)) == 0;
        }

        public long GetSourcePosition(int sourceHandle, int hardwareLatencyFrames)
        {
            ThrowIfDisposed();
            if (sourceHandle == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceHandle));
            }
            long position = Native.GetSourcePosition(_handle, unchecked((uint) sourceHandle),
                checked((uint) Math.Max(0, hardwareLatencyFrames)), out int error);
            return error == 0 ? position : -1;
        }

        public AsioMixerRouterStats GetStats()
        {
            ThrowIfDisposed();
            var stats = new AsioMixerRouterStats
            {
                Size = (uint) Marshal.SizeOf<AsioMixerRouterStats>(),
            };
            int result = Native.GetStats(_handle, ref stats);
            if (result != 0)
            {
                stats.LastError = result;
            }
            return stats;
        }

        public bool SetVolume(double volume)
        {
            ThrowIfDisposed();
            return Native.SetVolume(_handle, (float) volume) == 0;
        }

        public void Dispose()
        {
            IntPtr handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                Native.Destroy(handle);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_handle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(BassAsioMixerRouter));
            }
        }

        private static class Native
        {
            private const string LIBRARY = "yarg_audio";

            [DllImport(LIBRARY, EntryPoint = "yarg_audio_get_abi_version",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint GetAbiVersion();

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_create",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Create(in AsioMixerRouterConfig config, out IntPtr router);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_attach_mixer",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int AttachMixer(IntPtr router, uint mixer, uint bufferMilliseconds);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_prefill",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int Prefill(IntPtr router, uint mixer, uint timeoutMilliseconds);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_enable_output",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int EnableOutput(IntPtr router, uint firstAsioChannel);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_flush_mixer",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int FlushMixer(IntPtr router, uint mixer);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_get_source_position",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern long GetSourcePosition(IntPtr router, uint source,
                uint outputLatencyFrames, out int error);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_get_stats",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int GetStats(IntPtr router, ref AsioMixerRouterStats stats);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_set_volume",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int SetVolume(IntPtr router, float volume);

            [DllImport(LIBRARY, EntryPoint = "yarg_asio_router_destroy",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void Destroy(IntPtr router);
        }
    }
}
