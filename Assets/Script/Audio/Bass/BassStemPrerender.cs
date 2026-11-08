#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ManagedBass;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal static class BassStemPrerender
    {
        private const int CHUNK_FRAMES = 16384;
        private const int FLOAT_BYTES  = sizeof(float);

        public static byte[]? CopyStream(Stream original)
        {
            if (!original.CanRead || !original.CanSeek)
            {
                return null;
            }

            long originalPosition = original.Position;
            try
            {
                using var copy = new MemoryStream();
                original.CopyTo(copy);
                return copy.ToArray();
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Failed to copy stem audio for pre-rendering.");
                return null;
            }
            finally
            {
                original.Position = originalPosition;
            }
        }

        public static bool Decode(byte[] bytes, int[] indices, BassStemFormat format, List<BassStemSampleJob> jobs,
            CancellationToken token)
        {
            using var stream = new MemoryStream(bytes, false);
            int handle = BassX.CreateSourceUnchecked(stream);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to open stem audio for pre-rendering: {0}!", Bass.LastError);
                return false;
            }

            try
            {
                Bass.ChannelGetInfo(handle, out ChannelInfo info);
                int sourceChannels = info.Channels;
                int[] channelMap = indices.Length > 0 ? indices : CreateIdentityMap(sourceChannels);
                if (info.Frequency != format.Frequency || channelMap.Length != format.Channels)
                {
                    YargLogger.LogFormatError("Pre-rendered stem format ({0} Hz, {1} channels) does not match playback ({2} Hz, {3} channels)!",
                        info.Frequency, channelMap.Length, format.Frequency, format.Channels);
                    return false;
                }

                jobs.Sort((left, right) => left.StartFrame.CompareTo(right.StartFrame));

                long endFrame = 0;
                foreach (var job in jobs)
                {
                    endFrame = Math.Max(endFrame, job.StartFrame + job.FrameCount);
                }

                var buffer = new float[CHUNK_FRAMES * sourceChannels];
                long position = 0;
                int firstJob = 0;
                while (position < endFrame)
                {
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    int byteCount = buffer.Length * FLOAT_BYTES;
                    int bytesRead = Bass.ChannelGetData(handle, buffer, byteCount | (int) DataFlags.Float);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    int frames = bytesRead / FLOAT_BYTES / sourceChannels;
                    long chunkEnd = position + frames;

                    while (firstJob < jobs.Count && jobs[firstJob].StartFrame + jobs[firstJob].FrameCount <= position)
                    {
                        firstJob++;
                    }

                    for (int index = firstJob; index < jobs.Count && jobs[index].StartFrame < chunkEnd; index++)
                    {
                        CopyChunk(jobs[index], buffer, position, frames, sourceChannels, channelMap);
                    }

                    position = chunkEnd;
                }

                return true;
            }
            finally
            {
                if (!Bass.StreamFree(handle) && Bass.LastError != Errors.Handle)
                {
                    YargLogger.LogFormatError("Failed to free pre-render stream: {0}!", Bass.LastError);
                }
            }
        }

        private static void CopyChunk(BassStemSampleJob job, float[] buffer, long chunkStart, int chunkFrames, int sourceChannels,
            int[] channelMap)
        {
            long firstFrame = Math.Max(chunkStart, job.StartFrame);
            long lastFrame = Math.Min(chunkStart + chunkFrames, job.StartFrame + job.FrameCount);
            for (long frame = firstFrame; frame < lastFrame; frame++)
            {
                int source = (int) (frame - chunkStart) * sourceChannels;
                int destination = (int) (frame - job.StartFrame) * channelMap.Length;
                for (int channel = 0; channel < channelMap.Length; channel++)
                {
                    job.Samples[destination + channel] = buffer[source + channelMap[channel]];
                }
            }
        }

        private static int[] CreateIdentityMap(int channelCount)
        {
            var map = new int[channelCount];
            for (int channel = 0; channel < channelCount; channel++)
            {
                map[channel] = channel;
            }

            return map;
        }
    }
}
