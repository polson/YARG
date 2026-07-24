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
        private bool _ownsBassDevice;

#nullable enable
        internal static BassOutputDevice? Create(int deviceId, string name)
#nullable disable
        {
            bool initialized;
            try
            {
                initialized = Bass.Init(deviceId, 44100,
                    DeviceInitFlags.Default | DeviceInitFlags.Latency, IntPtr.Zero);
                if (!initialized)
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
            return new BassOutputDevice(Bass.CurrentDevice, -1, name, initialized);
        }

        internal static BassOutputDevice? CreateAsio(int deviceId, string name)
        {
            bool initialized;
            try
            {
                // ASIO pulls from a decoding mixer, but BASS still needs a device context
                // for creating and owning its streams.
                initialized = Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero);
                if (!initialized && Bass.LastError != Errors.Already)
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

            return new BassOutputDevice(0, deviceId, ASIO_PREFIX + name, initialized);
        }

        public BassOutputDevice Use()
        {
            Bass.CurrentDevice = DeviceId;

            return this;
        }

        internal void TransferOwnershipTo(BassOutputDevice replacement)
        {
            if (DeviceId == replacement.DeviceId && _ownsBassDevice)
            {
                _ownsBassDevice = false;
                replacement._ownsBassDevice = true;
            }
        }

        private BassOutputDevice(int deviceId, int asioDeviceId, string name, bool ownsBassDevice)
            : base(name)
        {
            DeviceId = deviceId;
            AsioDeviceId = asioDeviceId;
            _ownsBassDevice = ownsBassDevice;
            Use();
        }

        protected override void DisposeManagedResources()
        {
            if (!_ownsBassDevice)
            {
                return;
            }
            Bass.CurrentDevice = DeviceId;
            Bass.Free();
            _ownsBassDevice = false;
        }
    }
}
