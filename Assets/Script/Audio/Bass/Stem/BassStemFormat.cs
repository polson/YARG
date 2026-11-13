#nullable enable
using System;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// The sample rate and channel count shared by every stem clip and voice.
    /// </summary>
    internal readonly struct BassStemFormat
    {
        public readonly int Frequency;
        public readonly int Channels;

        public BassStemFormat(int frequency, int channels)
        {
            Frequency = frequency;
            Channels = channels;
        }

        public int FramesFor(double seconds)
        {
            return (int) Math.Round(seconds * Frequency);
        }

        public float[] Mono(float[] samples, int firstFrame, int frameCount)
        {
            var mono = new float[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < Channels; channel++)
                {
                    sum += samples[(firstFrame + frame) * Channels + channel];
                }

                mono[frame] = sum / Channels;
            }

            return mono;
        }
    }
}
