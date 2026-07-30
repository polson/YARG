using System;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Provides consistent failure handling for BASS operations.
    /// </summary>
    public static class BassX
    {
        /// <summary>
        /// Mixer operations. Required operations throw on failure. Best-effort operations log failures
        /// and return false.
        /// Best-effort operations log failures and return false.
        /// </summary>
        public static class Mix
        {
            // Undocumented BASS attribute for setting a mixer's maximum processing thread count.
            private const ChannelAttribute PROCESSING_THREADS_ATTRIBUTE = (ChannelAttribute) 86017;

            public static int CreateMixer(int frequency, int channels, BassFlags flags)
            {
                return Require(
                    BassMix.CreateMixerStream(frequency, channels, flags),
                    $"create {channels}-channel mixer at {frequency} Hz");
            }

            public static int CreateMixer(int frequency, int channels, BassFlags flags, int processingThreads)
            {
                int mixer = CreateMixer(frequency, channels, flags);
                SetProcessingThreads(mixer, processingThreads);
                return mixer;
            }

            public static int CreateSplit(int source, BassFlags flags, int[] channelMap)
            {
                return Require(
                    BassMix.CreateSplitStream(source, flags, channelMap),
                    $"create split stream from channel {source}");
            }

            public static void AddChannel(int mixer, int channel, BassFlags flags)
            {
                Require(
                    BassMix.MixerAddChannel(mixer, channel, flags),
                    $"add channel {channel} to mixer {mixer}");
            }

            public static void AddChannel(int mixer, int channel, BassFlags flags, long start, long length)
            {
                Require(
                    BassMix.MixerAddChannel(mixer, channel, flags, start, length),
                    $"add channel {channel} to mixer {mixer}");
            }

            public static bool RemoveChannel(int channel)
            {
                return Check(BassMix.MixerRemoveChannel(channel), $"remove channel {channel} from mixer");
            }

            public static void SetMatrix(int channel, float[,] matrix)
            {
                Require(
                    BassMix.ChannelSetMatrix(channel, matrix),
                    $"set matrix for channel {channel}");
            }

            public static bool SetProcessingThreads(int mixer, int count)
            {
                return Check(
                    Bass.ChannelSetAttribute(mixer, PROCESSING_THREADS_ATTRIBUTE, count),
                    $"set processing threads for mixer {mixer}");
            }

            public static bool SetPosition(int channel, long position, PositionFlags mode)
            {
                return Check(
                    BassMix.ChannelSetPosition(channel, position, mode),
                    $"set mixer channel {channel} position to {position}");
            }
        }

        /// <summary>
        /// Required BASS_FX operations. Each operation throws on failure.
        /// </summary>
        public static class Fx
        {
            public static int CreateTempo(int source, BassFlags flags)
            {
                return Require(BassFx.TempoCreate(source, flags), $"create tempo stream from channel {source}");
            }
        }

        public static class Channel
        {
            public static bool GetAttribute(int channel, ChannelAttribute attribute, out float value)
            {
                return Check(
                    Bass.ChannelGetAttribute(channel, attribute, out value),
                    $"get {attribute} attribute for channel {channel}");
            }

            public static bool SetDevice(int channel, int device)
            {
                return Check(
                    Bass.ChannelSetDevice(channel, device),
                    $"set channel {channel} device to {device}");
            }
        }

        /// <summary>
        /// Required stream operations. Each operation throws on failure.
        /// </summary>
        public static class Stream
        {
            public static int CreateSource(System.IO.Stream stream)
            {
                return Require(CreateSourceUnchecked(stream), "create source stream");
            }

            internal static int CreateSourceUnchecked(System.IO.Stream stream)
            {
                // Last flag is BASS_SAMPLE_NOREORDER, which is not yet included in BassFlags.
                // https://www.un4seen.com/forum/?topic=20148.msg140872#msg140872
                const BassFlags flags = BassFlags.Prescan | BassFlags.Decode | BassFlags.AsyncFile | (BassFlags) 64;

                return Bass.CreateStream(StreamSystem.NoBuffer, flags, new BassStreamProcedures(stream));
            }

            public static bool Free(int stream, string description = "stream")
            {
                return Check(Bass.StreamFree(stream), $"free {description} {stream}");
            }
        }

        /// <summary>
        /// Requires a boolean BASS operation to succeed.
        /// </summary>
        public static void Require(bool success, string operation)
        {
            if (!success)
            {
                throw CreateException(operation);
            }
        }

        /// <summary>
        /// Requires a BASS handle creation operation to return a non-zero handle.
        /// </summary>
        public static int Require(int handle, string operation)
        {
            if (handle == 0)
            {
                throw CreateException(operation);
            }

            return handle;
        }

        /// <summary>
        /// Performs a non-fatal BASS operation, logging failure without aborting the enclosing operation.
        /// </summary>
        public static bool Check(bool success, string operation)
        {
            if (success)
            {
                return true;
            }

            Errors error = Bass.LastError;
            YargLogger.LogFormatError("Failed to {0}: {1}", operation, error);
            return false;
        }

        private static BassOperationException CreateException(string operation)
        {
            // Bass.LastError must be captured before another BASS call can overwrite it.
            return new BassOperationException(operation, Bass.LastError);
        }

        internal sealed class BassOperationException : Exception
        {
            public BassOperationException(string operation, Errors error)
                : base($"Failed to {operation}: {error}")
            {
            }
        }
    }
}
