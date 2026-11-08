#nullable enable
namespace YARG.Audio.BASS
{
    internal sealed class BassSoloStretchTarget
    {
        public readonly long BodyStartFrame;
        public readonly int  BodyFrames;
        public          float[]? Output;

        public BassSoloStretchTarget(long bodyStartFrame, int bodyFrames)
        {
            BodyStartFrame = bodyStartFrame;
            BodyFrames = bodyFrames;
        }
    }
}
