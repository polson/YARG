#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemLoop
    {
        public static readonly BassStemLoop NONE = new(startFrame: 0, endFrame: 0, fadeFrames: 0,
            tail: Array.Empty<float>());

        public readonly int     StartFrame;
        public readonly int     EndFrame;
        public readonly int     FadeFrames;
        public readonly float[] Tail;

        public BassStemLoop(int startFrame, int endFrame, int fadeFrames, float[] tail)
        {
            StartFrame = startFrame;
            EndFrame = endFrame;
            FadeFrames = fadeFrames;
            Tail = tail;
        }

        public bool HasBody => EndFrame > StartFrame;

        public int NextSegment(int pushedFrames, int remaining, out bool fromTail, out int sourceFrame)
        {
            int fadeStart = EndFrame - FadeFrames;
            if (pushedFrames < fadeStart)
            {
                fromTail = false;
                sourceFrame = pushedFrames;
                return Math.Min(remaining, fadeStart - pushedFrames);
            }

            fromTail = true;
            sourceFrame = pushedFrames - fadeStart;
            return Math.Min(remaining, EndFrame - pushedFrames);
        }
    }
}
