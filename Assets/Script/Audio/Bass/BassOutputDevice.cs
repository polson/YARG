using ManagedBass;
using System;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassOutputDevice : OutputDevice
    {
        public const string ASIO_PREFIX = "ASIO: ";

        public readonly int DeviceId;
        public readonly int AsioDeviceId;
        public bool IsAsio => AsioDeviceId >= 0;

#nullable enable
        internal static BassOutputDevice? Create(int deviceId, string name)
#nullable disable
        {
            try
            {
                if (!Bass.Init(deviceId, 44100, DeviceInitFlags.Default | DeviceInitFlags.Latency, IntPtr.Zero))
                {
                    if (Bass.LastError != Errors.Already)
                    {
                        YargLogger.LogFormatError("Failed to initialize BASS device '{0}': {1}!", name,
                            Bass.LastError);

                        return null;
                    }
                }
            }
            catch (BassException e)
            {
                YargLogger.LogException(e);
                return null;
            }

            // Device 1 can be BASS' dynamic "Default" device. Keep resolved ID so all
            // streams and later cleanup use device BASS actually initialized.
            return new BassOutputDevice(Bass.CurrentDevice, -1, name);
        }

        internal static BassOutputDevice? CreateAsio(int deviceId, string name)
        {
            try
            {
                // ASIO pulls from a decoding mixer, but BASS still needs a device context
                // for creating and owning its streams.
                if (!Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero) &&
                    Bass.LastError != Errors.Already)
                {
                    YargLogger.LogFormatError("Failed to initialize BASS no-sound device for ASIO '{0}': {1}!",
                        name, Bass.LastError);
                    return null;
                }
            }
            catch (BassException e)
            {
                YargLogger.LogException(e);
                return null;
            }

            return new BassOutputDevice(0, deviceId, ASIO_PREFIX + name);
        }

        public BassOutputDevice Use()
        {
            Bass.CurrentDevice = DeviceId;

            return this;
        }

        private BassOutputDevice(int deviceId, int asioDeviceId, string name)
            : base(name)
        {
            DeviceId = deviceId;
            AsioDeviceId = asioDeviceId;
            Use();
        }

        protected override void DisposeManagedResources()
        {
            Bass.CurrentDevice = DeviceId;
            Bass.Free();
        }
    }
}
