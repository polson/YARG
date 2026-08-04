#nullable enable
using System.Collections.Generic;
using YARG.Core.Audio;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// BASS-side transport. Each implementation owns its output device context and backend,
    /// plus the inputs that belong to its driver world. The manager never type-tests a
    /// transport; everything transport-specific lives behind this surface.
    /// </summary>
    internal abstract class BassAudioTransport : AudioTransport
    {
        /// <summary>Initialized backend, valid after <see cref="Activate"/>.</summary>
        public abstract IBassOutputBackend Backend { get; }

        /// <summary>Creates a mic device for a transport-local display name.</summary>
        public abstract MicDevice? CreateInputByName(string name);

        public BassOutputDevice BassMixerDevice => (BassOutputDevice) MixerDevice;
        public int BassDeviceId => BassMixerDevice.DeviceId;

        /// <summary>
        /// Resolves a device display name to a transport instance. No native state is touched;
        /// activation happens later. Classification of the name lives here, in one place.
        /// </summary>
        public static BassAudioTransport? Create(string name, BassAudioOutput audioOutput)
        {
            if (BassAsioAudioTransport.IsAsioName(name))
            {
                return BassAsioAudioTransport.Create(name);
            }
            return BassSharedAudioTransport.Create(name, audioOutput);
        }

        /// <summary>The driver family a device display name belongs to.</summary>
        public static AudioOutputBackend GetBackend(string name) =>
            BassAsioAudioTransport.IsAsioName(name)
                ? AudioOutputBackend.Asio
                : AudioOutputBackend.WindowsAudio;

        /// <summary>Creates a mic device for a transport-local channel id + display name.</summary>
        public MicDevice? CreateInputByChannel(int channelId, string name)
        {
            foreach (var descriptor in GetInputs())
            {
                if (descriptor.ChannelId == channelId && descriptor.DisplayName == name)
                {
                    return CreateInput(descriptor);
                }
            }

            return null;
        }
    }
}
