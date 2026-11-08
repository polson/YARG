#nullable enable
namespace YARG.Audio.BASS
{
    internal sealed class BassStemSampleJob
    {
        public readonly long    StartFrame;
        public readonly int     FrameCount;
        public readonly float[] Samples;

        public BassStemSampleJob(long startFrame, int frameCount, float[] samples)
        {
            StartFrame = startFrame;
            FrameCount = frameCount;
            Samples = samples;
        }
    }
}
