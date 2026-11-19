#nullable enable
using System;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassMixerBus : IBassDecodeSource
    {
        private bool _disposed;

        private BassMixerBus(int handle)
        {
            SourceHandle = handle;
        }

        public int SourceHandle { get; }

        public event Action<IBassDecodeSource>? Disposed;

        internal static BassMixerBus? Create(int sampleRate, int channelCount)
        {
            int handle = BassMix.CreateMixerStream(sampleRate, channelCount,
                BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to create mixer bus ({0} Hz, {1} channels): {2}",
                    sampleRate, channelCount, Bass.LastError);
                return null;
            }

            return new BassMixerBus(handle);
        }

        public bool Take(int sourceHandle)
        {
            if (BassMix.ChannelGetMixer(sourceHandle) == SourceHandle)
            {
                return true;
            }

            BassMix.MixerRemoveChannel(sourceHandle);
            if (BassMix.MixerAddChannel(SourceHandle, sourceHandle, BassFlags.Default))
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to add source to mixer bus: {0}", Bass.LastError);
            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!Bass.StreamFree(SourceHandle) && Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to free mixer bus {0}: {1}", SourceHandle, Bass.LastError);
            }

            Disposed?.Invoke(this);
        }
    }
}
