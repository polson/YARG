using ManagedBass;
using YARG.Core.Audio;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// A BASS device context that mixers route to. The device itself carries no driver
    /// identity: whether it backs a shared-mode device or an ASIO driver is a property of
    /// the owning transport.
    /// </summary>
    public sealed class BassOutputDevice : OutputDevice
    {
        public readonly int DeviceId;
        private readonly BassDeviceContextLease _contextLease;

        internal static BassOutputDevice? Create(int deviceId, string name) =>
            Initialize(deviceId, name, DeviceInitFlags.Default | DeviceInitFlags.Latency,
                resolveDeviceId: true);

        /// <summary>
        /// Creates the no-sound BASS context (device 0) that ASIO output uses purely to own
        /// decode streams. ASIO has no BASS output device.
        /// </summary>
        internal static BassOutputDevice? CreateAsio(string name) =>
            Initialize(0, name, DeviceInitFlags.Default, resolveDeviceId: false);

        private static BassOutputDevice? Initialize(int deviceId, string name,
            DeviceInitFlags flags, bool resolveDeviceId)
        {
            var contextLease = BassDeviceContextLease.Acquire(deviceId, name, flags, resolveDeviceId);
            if (contextLease == null)
            {
                return null;
            }

            return new BassOutputDevice(contextLease.ResolvedDeviceId, name, contextLease);
        }

        public BassOutputDevice Use()
        {
            Bass.CurrentDevice = DeviceId;

            return this;
        }

        private BassOutputDevice(int deviceId, string name, BassDeviceContextLease contextLease)
            : base(name)
        {
            DeviceId = deviceId;
            _contextLease = contextLease;
            Use();
        }

        protected override void DisposeManagedResources()
        {
            _contextLease.Dispose();
        }
    }
}
