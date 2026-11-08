#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal interface IBassStretchRender : IDisposable
    {
        bool Begin(float[] input, int channels, long startFrame);
        bool Read(float[] input, int channels, long outputFrame, float[] output);
    }

    internal delegate IBassStretchRender? BassStretchRenderFactory(int channels, int frequency, float speed);
}
