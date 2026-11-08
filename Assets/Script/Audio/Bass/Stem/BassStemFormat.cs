#nullable enable
using System;

namespace YARG.Audio.BASS
{
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
    }
}
