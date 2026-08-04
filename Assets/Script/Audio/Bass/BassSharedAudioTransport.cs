#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Shared-mode transport: a normal BASS output device plus BASS recording-device inputs.
    /// Owns the BASS device context and the device output backend.
    /// </summary>
    internal sealed class BassSharedAudioTransport : BassAudioTransport
    {
        private readonly int _deviceId;
        private readonly BassAudioOutput _audioOutput;

        private BassOutputDevice? _device;
        private BassDeviceOutputBackend? _backend;

        public override AudioTransportDescriptor Descriptor { get; }
        public override OutputDevice MixerDevice =>
            _device ?? throw new InvalidOperationException("Transport not activated");
        public override IBassOutputBackend Backend =>
            _backend ?? throw new InvalidOperationException("Transport not activated");

        private BassSharedAudioTransport(int deviceId, string name, BassAudioOutput audioOutput)
        {
            _deviceId = deviceId;
            _audioOutput = audioOutput;
            Descriptor = new AudioTransportDescriptor($"bass-shared:{name}", name,
                AudioOutputBackend.WindowsAudio);
        }

        internal static BassSharedAudioTransport? Create(string name, BassAudioOutput audioOutput)
        {
            for (int deviceIndex = 0; Bass.GetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled devices
                if (!info.IsEnabled)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                // Ensure device names match
                if (info.Name != name)
                {
                    continue;
                }

                return new BassSharedAudioTransport(deviceIndex, name, audioOutput);
            }

            return null;
        }

        internal static List<(int id, string name)> EnumerateDevices()
        {
            var devices = new List<(int id, string name)>();

            for (int deviceIndex = 1; Bass.GetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled devices
                if (!info.IsEnabled)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                devices.Add((deviceIndex, info.Name));
            }

            return devices;
        }

        public override bool Activate(AudioTransportConfiguration configuration)
        {
            if (_device != null)
            {
                return true;
            }

            var device = BassOutputDevice.Create(_deviceId, Descriptor.DisplayName);
            if (device == null)
            {
                return false;
            }

            device.Use();
            var backend = new BassDeviceOutputBackend();
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

        public override IReadOnlyList<AudioInputDescriptor> GetInputs()
        {
            var mics = new List<AudioInputDescriptor>();

            for (int deviceIndex = 0; Bass.RecordGetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled/claimed devices
                if (!info.IsEnabled || info.IsInitialized)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                // The "Default" device is excluded here since we want the user to explicitly pick
                // which microphone to use
                if (info.Name == "Default")
                {
                    continue;
                }

                mics.Add(new AudioInputDescriptor(info.Name, info.Name, deviceIndex));
            }

            return mics;
        }

        public override MicDevice? CreateInputByName(string name)
        {
            for (int deviceIndex = 0; Bass.RecordGetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled/claimed devices
                if (!info.IsEnabled || info.IsInitialized)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                if (info.Name == "Default" || info.Name != name)
                {
                    continue;
                }

                return CreateInput(new AudioInputDescriptor(name, name, deviceIndex));
            }

            return null;
        }

        public override MicDevice? CreateInput(AudioInputDescriptor descriptor)
        {
            var device = BassMicDevice.Create(descriptor.ChannelId, descriptor.DisplayName, _audioOutput);
            device?.SetMonitoringLevel(SettingsManager.Settings.VocalMonitoring.Value);
            return device;
        }
    }
}
