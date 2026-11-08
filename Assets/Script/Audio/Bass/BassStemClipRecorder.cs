#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemClipRecorder : IBassStemClipSource
    {
        private readonly BassStemReplay _replay;
        private readonly BassStemFormat _format;
        private readonly Dictionary<int, BassStemClip> _clips = new();

        public BassStemClipRecorder(BassStemReplay replay, BassStemFormat format)
        {
            _replay = replay;
            _format = format;
        }

        public void Record(int key, double noteSeconds, double durationSeconds, bool isSustain)
        {
            long startFrame = _replay.RingFrameAt(noteSeconds);
            int frameCount = BassStemClip.FramesFor(_format, durationSeconds);
            if (frameCount <= 0 || startFrame < _replay.OldestRingFrame)
            {
                return;
            }

            if (!_clips.TryGetValue(key, out var clip))
            {
                clip = new BassStemClip();
                _clips.Add(key, clip);
            }

            int sampleCount = frameCount * _format.Channels;
            if (clip.Samples.Length < sampleCount)
            {
                clip.Samples = new float[sampleCount];
            }

            clip.StartFrame = startFrame;
            clip.FrameCount = frameCount;
            clip.FilledFrames = 0;
            clip.IsSustain = isSustain;
            clip.Generation++;
            Fill(clip);
        }

        public void Pump()
        {
            foreach (var clip in _clips.Values)
            {
                if (clip.FilledFrames < clip.FrameCount)
                {
                    Fill(clip);
                }
            }
        }

        public void Clear()
        {
            foreach (var clip in _clips.Values)
            {
                clip.FrameCount = 0;
                clip.FilledFrames = 0;
                clip.Generation++;
            }
        }

        public bool TryGetClip(int key, out BassStemClip clip)
        {
            return _clips.TryGetValue(key, out clip) && clip.FrameCount > 0;
        }

        public bool TryGetFretClip(int key, out BassStemClip clip)
        {
            for (int fret = 1; fret <= key; fret <<= 1)
            {
                if ((key & fret) != 0 && TryGetClip(fret, out clip))
                {
                    return true;
                }
            }

            clip = null!;
            return false;
        }

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            foreach (var recorded in _clips.Values)
            {
                if (recorded.FrameCount > 0)
                {
                    clip = recorded;
                    return true;
                }
            }

            clip = null!;
            return false;
        }

        private void Fill(BassStemClip clip)
        {
            long nextFrame = clip.StartFrame + clip.FilledFrames;
            long endFrame = Math.Min(_replay.CapturedRingFrames, clip.StartFrame + clip.FrameCount);
            if (endFrame <= nextFrame)
            {
                return;
            }

            int frames = (int) (endFrame - nextFrame);
            _replay.CopyFrames(firstFrame: nextFrame, frameCount: frames, destination: clip.Samples,
                destinationFrame: clip.FilledFrames);
            clip.ApplyEndFade(firstFrame: clip.FilledFrames, frameCount: frames, format: _format);
            clip.FilledFrames += frames;
        }
    }
}
