#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass.Asio;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// ASIO transport: exclusive driver output plus driver-owned input channels.
    /// Owns the no-sound BASS context and the ASIO output backend. All ASIO vocabulary of the
    /// audio control plane lives here (and in the backend/mic/native files).
    /// </summary>
    internal sealed class BassAsioAudioTransport : BassAudioTransport
    {
        public const string ASIO_PREFIX = "ASIO: ";

        private readonly int _asioDeviceIndex;
        private int _bufferLength;

        private BassOutputDevice? _device;
        private BassAsioOutputBackend? _backend;

        public override AudioTransportDescriptor Descriptor { get; }
        public override OutputDevice MixerDevice =>
            _device ?? throw new InvalidOperationException("Transport not activated");
        public override IBassOutputBackend Backend =>
            _backend ?? throw new InvalidOperationException("Transport not activated");

        private BassAsioAudioTransport(int asioDeviceIndex, string driverName, string displayName)
        {
            _asioDeviceIndex = asioDeviceIndex;
            Descriptor = new AudioTransportDescriptor($"asio:{driverName}", displayName,
                AudioOutputBackend.Asio);
        }

        internal static bool IsAsioName(string name) =>
            name.StartsWith(ASIO_PREFIX, StringComparison.Ordinal);

        internal static BassAsioAudioTransport? Create(string name)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!IsAsioName(name))
            {
                return null;
            }

            string driverName = name.Substring(ASIO_PREFIX.Length);
            try
            {
                for (int deviceIndex = 0; deviceIndex < BassAsio.DeviceCount; deviceIndex++)
                {
                    var info = BassAsio.GetDeviceInfo(deviceIndex);
                    if (info.Name == driverName)
                    {
                        return new BassAsioAudioTransport(deviceIndex, driverName, name);
                    }
                }
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to find ASIO device");
            }
#endif
            return null;
        }

        internal static List<(int id, string name)> EnumerateDevices()
        {
            var devices = new List<(int id, string name)>();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                for (int deviceIndex = 0; deviceIndex < BassAsio.DeviceCount; deviceIndex++)
                {
                    var info = BassAsio.GetDeviceInfo(deviceIndex);
                    devices.Add((deviceIndex, ASIO_PREFIX + info.Name));
                }
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to enumerate ASIO devices");
            }
#endif
            return devices;
        }

        public override bool Activate(AudioTransportConfiguration configuration)
        {
            if (_device != null)
            {
                return true;
            }

            _bufferLength = configuration.BufferLength;

            var device = BassOutputDevice.CreateAsio(Descriptor.DisplayName);
            if (device == null)
            {
                return false;
            }

            device.Use();
            var backend = new BassAsioOutputBackend(_asioDeviceIndex, _bufferLength,
                NotifyReinitializeRequested);
            if (!backend.Initialize(device))
            {
                backend.Dispose();
                device.Dispose();
                return false;
            }

            _device = device;
            _backend = backend;
            return true;
        }

        public override void Deactivate()
        {
            _backend?.Dispose();
            _backend = null;
            _device?.Dispose();
            _device = null;
        }

        public override OutputBufferInfo? GetBufferInfo()
        {
            if (_backend == null)
            {
                return null;
            }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                BassAsio.CurrentDevice = _asioDeviceIndex;
                var info = BassAsio.Info;
                var lengths = GetBufferLengths(info);
                int sampleRate = (int) Math.Round(BassAsio.Rate);
                bool isDriverControlled = info.BufferLengthGranularity == 0 &&
                    info.MinBufferLength == info.MaxBufferLength;
                return new OutputBufferInfo(lengths, info.PreferredBufferLength, sampleRate, isDriverControlled);
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to read ASIO buffer sizes");
            }
#endif
            return null;
        }

        public override bool OpenControlPanel()
        {
            if (_backend == null)
            {
                return false;
            }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            BassAsio.CurrentDevice = _asioDeviceIndex;
            if (BassAsio.ControlPanel())
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to open ASIO control panel: {0}", BassAsio.LastError);
#endif
            return false;
        }

        public override IReadOnlyList<AudioInputDescriptor> GetInputs()
        {
            var inputs = new List<AudioInputDescriptor>();
            if (_backend == null)
            {
                return inputs;
            }

            foreach (var descriptor in _backend.GetInputDescriptors())
            {
                string name = GetAsioMicName(descriptor);
                inputs.Add(new AudioInputDescriptor(name, name, descriptor.ChannelIndex));
            }
            return inputs;
        }

        public override MicDevice? CreateInputByName(string name)
        {
            var asioInput = FindAsioInput(name);
            return asioInput != null
                ? BassAsioMicDevice.Create(this, asioInput, name)
                : null;
        }

        public override MicDevice? CreateInput(AudioInputDescriptor descriptor)
        {
            var asioInput = FindAsioInput(descriptor.DisplayName);
            if (asioInput == null || asioInput.ChannelIndex != descriptor.ChannelId)
            {
                return null;
            }
            return BassAsioMicDevice.Create(this, asioInput, descriptor.DisplayName);
        }

        internal AsioInputAcquireResult TryAcquireInput(string driverId, int channelIndex,
            out BassAsioInputLease? lease)
        {
            if (_backend != null)
            {
                return _backend.TryAcquireInput(driverId, channelIndex, out lease);
            }

            lease = null;
            return AsioInputAcquireResult.UnavailableChannel;
        }

        private AsioInputDescriptor? FindAsioInput(string name)
        {
            if (_backend == null)
            {
                return null;
            }

            foreach (var descriptor in _backend.GetInputDescriptors())
            {
                if (string.Equals(GetAsioMicName(descriptor), name, StringComparison.Ordinal))
                {
                    return descriptor;
                }
            }
            return null;
        }

        private static string GetAsioMicName(AsioInputDescriptor descriptor) =>
            $"{ASIO_PREFIX}{descriptor.DriverName} - {descriptor.ChannelIndex}: {descriptor.Name}";

        private static int[] GetBufferLengths(AsioInfo info)
        {
            var lengths = new List<int>();
            int minimum = info.MinBufferLength;
            int maximum = info.MaxBufferLength;

            if (minimum <= 0 || maximum < minimum)
            {
                return Array.Empty<int>();
            }

            if (info.BufferLengthGranularity == -1)
            {
                for (long length = minimum; length <= maximum; length *= 2)
                {
                    lengths.Add((int) length);
                    if (length > int.MaxValue / 2)
                    {
                        break;
                    }
                }
            }
            else if (info.BufferLengthGranularity > 0)
            {
                for (long length = minimum; length <= maximum; length += info.BufferLengthGranularity)
                {
                    lengths.Add((int) length);
                }
            }
            else
            {
                lengths.Add(minimum);
            }

            if (info.PreferredBufferLength >= minimum && info.PreferredBufferLength <= maximum &&
                !lengths.Contains(info.PreferredBufferLength))
            {
                lengths.Add(info.PreferredBufferLength);
                lengths.Sort();
            }
            return lengths.ToArray();
        }
    }
}
