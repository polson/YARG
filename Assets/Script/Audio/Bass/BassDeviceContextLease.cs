#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Ref-counted ownership of one initialized BASS device context.
    ///
    /// Several transports can resolve to the same BASS device id (the "Default" device and the
    /// ASIO no-sound context both land on device 0). The context stays initialized while any
    /// lease is held; BASS_Free runs only when the last lease for that device id is released.
    /// This replaces the ownership hand-off that previously moved a single free flag between
    /// devices sharing an id.
    /// </summary>
    internal sealed class BassDeviceContextLease : IDisposable
    {
        private static readonly object Lock = new();
        private static readonly Dictionary<int, int> RefCounts = new();

        private readonly int _deviceId;
        private readonly bool _ownsContext;
        private bool _released;

        /// <summary>BASS device id this lease covers (resolved through "Default" if applicable).</summary>
        public int ResolvedDeviceId => _deviceId;

        public static BassDeviceContextLease? Acquire(int deviceId, string name, DeviceInitFlags flags,
            bool resolveDeviceId)
        {
            bool initialized;
            try
            {
                initialized = Bass.Init(deviceId, 44100, flags, IntPtr.Zero);
                if (!initialized && Bass.LastError != Errors.Already)
                {
                    YargLogger.LogFormatError("Failed to initialize BASS device '{0}': {1}!", name,
                        Bass.LastError);
                    return null;
                }
            }
            catch (BassException e)
            {
                YargLogger.LogException(e);
                return null;
            }

            // "Default" resolves through BASS to a concrete device. Keep resolved ID so all
            // streams and later cleanup use the device BASS actually initialized.
            int resolvedDeviceId = resolveDeviceId ? Bass.CurrentDevice : deviceId;
            lock (Lock)
            {
                RefCounts[resolvedDeviceId] = RefCounts.GetValueOrDefault(resolvedDeviceId) + 1;
            }
            return new BassDeviceContextLease(resolvedDeviceId, initialized);
        }

        private BassDeviceContextLease(int deviceId, bool ownsContext)
        {
            _deviceId = deviceId;
            _ownsContext = ownsContext;
        }

        /// <summary>
        /// Clears all lease state. Only used by editor play-mode cleanup: BASS may be freed
        /// directly on domain/play-mode restart, which would otherwise leave stale refcounts.
        /// </summary>
        internal static void ResetForEditor()
        {
            lock (Lock)
            {
                RefCounts.Clear();
            }
        }

        public void Dispose()
        {
            lock (Lock)
            {
                if (_released)
                {
                    return;
                }
                _released = true;

                if (!RefCounts.TryGetValue(_deviceId, out int count))
                {
                    return;
                }

                count--;
                if (count > 0)
                {
                    RefCounts[_deviceId] = count;
                    return;
                }

                RefCounts.Remove(_deviceId);
                if (_ownsContext)
                {
                    Bass.CurrentDevice = _deviceId;
                    Bass.Free();
                }
            }
        }
    }
}
