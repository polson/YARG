#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassStemSoundboard : IDisposable
    {
        private const double MAX_CLIP_SECONDS = 3.0;
        private const double FADE_SECONDS     = 0.08;
        private const int    VOICE_COUNT      = 4;
        private const int    FLOAT_BYTES      = sizeof(float);

        private sealed class Clip
        {
            public float[] Samples = Array.Empty<float>();
            public long    StartFrame;
            public int     FrameCount;
            public int     FilledFrames;
            public bool    IsSustain;
            public int     Generation;
        }

        private sealed class Voice
        {
            public PushStream Stream = null!;
            public int        PitchFx;
            public Clip?      Clip;
            public int        Generation;
            public int        PushedFrames;
            public int        Key;
            public bool       IsSustain;
            public long       StartOrder;
        }

        private readonly BassStemReplay             _replay;
        private readonly PitchShiftParametersStruct _pitchParams;
        private readonly Func<double>               _volume;
        private readonly Dictionary<int, Clip>      _clips  = new();
        private readonly Voice[]                    _voices = new Voice[VOICE_COUNT];
        private readonly int                        _maxClipFrames;
        private readonly int                        _fadeFrames;
        private          long                       _playCount;
        private          bool                       _disposed;

        internal BassStemSoundboard(BassStemReplay replay, AudioManager manager, PitchShiftParametersStruct pitchParams, Func<double> volume)
        {
            _replay = replay;
            _pitchParams = pitchParams;
            _volume = volume;
            _maxClipFrames = (int) Math.Round(MAX_CLIP_SECONDS * replay.Frequency);
            _fadeFrames = (int) Math.Round(FADE_SECONDS * replay.Frequency);

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                var stream = manager.CreatePushStream(replay.Frequency, replay.ChannelCount);
                _voices[index] = new Voice
                {
                    Stream = stream,
                    PitchFx = BassHelpers.FXAddParameters(stream.Handle, EffectType.PitchShift, pitchParams),
                };
            }
        }

        public void Record(int key, double noteSeconds, double durationSeconds, bool isSustain)
        {
            lock (_replay.Lock)
            {
                if (_disposed)
                {
                    return;
                }

                long startFrame = _replay.SecondsToFrame(noteSeconds);
                int frameCount = (int) Math.Min(Math.Round(durationSeconds * _replay.Frequency), _maxClipFrames);
                if (frameCount <= 0 || startFrame < _replay.OldestFrame)
                {
                    return;
                }

                if (!_clips.TryGetValue(key, out var clip))
                {
                    clip = new Clip();
                    _clips.Add(key, clip);
                }

                int sampleCount = frameCount * _replay.ChannelCount;
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
        }

        public bool HasClip(int key)
        {
            lock (_replay.Lock)
            {
                return !_disposed && _clips.TryGetValue(key, out var clip) && clip.FrameCount > 0;
            }
        }

        public bool Play(int key, int semitones, double gain)
        {
            lock (_replay.Lock)
            {
                if (_disposed || !_clips.TryGetValue(key, out var clip) || clip.FrameCount == 0)
                {
                    return false;
                }

                var voice = PickVoice(key);
                voice.Stream.Clear();

                var pitch = _pitchParams;
                pitch.fPitchShift = (float) Math.Pow(2, semitones / 12.0);
                if (!BassHelpers.FXSetParameters(voice.PitchFx, pitch))
                {
                    YargLogger.LogFormatError("Failed to set replay pitch: {0}!", Bass.LastError);
                }

                voice.Stream.SetVolume(_volume() * gain);
                voice.Clip = clip;
                voice.Generation = clip.Generation;
                voice.PushedFrames = 0;
                voice.Key = key;
                voice.IsSustain = clip.IsSustain;
                voice.StartOrder = ++_playCount;
                Feed(voice);
                return true;
            }
        }

        public void Release(int fretMask)
        {
            lock (_replay.Lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    if (voice.IsSustain && (voice.Key & fretMask) != 0)
                    {
                        FadeOut(voice);
                    }
                }
            }
        }

        public void StopAll()
        {
            lock (_replay.Lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    if (voice.Key != 0)
                    {
                        FadeOut(voice);
                    }
                }
            }
        }

        internal void Clear()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var clip in _clips.Values)
            {
                clip.FrameCount = 0;
                clip.FilledFrames = 0;
                clip.Generation++;
            }

            foreach (var voice in _voices)
            {
                voice.Stream.Clear();
                voice.Clip = null;
                voice.Key = 0;
                voice.IsSustain = false;
            }
        }

        internal void OnCaptured()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var clip in _clips.Values)
            {
                if (clip.FilledFrames < clip.FrameCount)
                {
                    Fill(clip);
                }
            }

            foreach (var voice in _voices)
            {
                Feed(voice);
            }
        }

        public void Dispose()
        {
            lock (_replay.Lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            foreach (var voice in _voices)
            {
                voice.Stream.Dispose();
            }
        }

        private Voice PickVoice(int key)
        {
            Voice? oldestIdle = null;
            Voice oldest = _voices[0];
            foreach (var voice in _voices)
            {
                if (voice.Key == key)
                {
                    return voice;
                }

                if (voice.Key == 0 && (oldestIdle == null || voice.StartOrder < oldestIdle.StartOrder))
                {
                    oldestIdle = voice;
                }

                if (voice.StartOrder < oldest.StartOrder)
                {
                    oldest = voice;
                }
            }

            return oldestIdle ?? oldest;
        }

        private void Fill(Clip clip)
        {
            long nextFrame = clip.StartFrame + clip.FilledFrames;
            long endFrame = Math.Min(_replay.CapturedFrames, clip.StartFrame + clip.FrameCount);
            if (endFrame <= nextFrame)
            {
                return;
            }

            int frames = (int) (endFrame - nextFrame);
            _replay.CopyFrames(nextFrame, frames, clip.Samples, clip.FilledFrames);
            ApplyEndFade(clip, clip.FilledFrames, frames);
            clip.FilledFrames += frames;
        }

        private void ApplyEndFade(Clip clip, int firstFrame, int frameCount)
        {
            int fadeFrames = Math.Min(_fadeFrames, clip.FrameCount);
            int fadeStart = clip.FrameCount - fadeFrames;
            int channels = _replay.ChannelCount;
            for (int frame = Math.Max(firstFrame, fadeStart); frame < firstFrame + frameCount; frame++)
            {
                float gain = (float) (clip.FrameCount - frame) / fadeFrames;
                for (int channel = 0; channel < channels; channel++)
                {
                    clip.Samples[frame * channels + channel] *= gain;
                }
            }
        }

        private void Feed(Voice voice)
        {
            var clip = voice.Clip;
            if (clip == null)
            {
                return;
            }

            if (clip.Generation != voice.Generation)
            {
                voice.Clip = null;
                return;
            }

            int frames = clip.FilledFrames - voice.PushedFrames;
            if (frames > 0)
            {
                Push(voice.Stream, clip.Samples, voice.PushedFrames, frames);
                voice.PushedFrames += frames;
            }

            if (voice.PushedFrames >= clip.FrameCount)
            {
                voice.Clip = null;
            }
        }

        private unsafe void Push(PushStream stream, float[] samples, int firstFrame, int frameCount)
        {
            int channels = _replay.ChannelCount;
            fixed (float* pointer = &samples[firstFrame * channels])
            {
                stream.Push((IntPtr) pointer, frameCount * channels * FLOAT_BYTES);
            }
        }

        private void FadeOut(Voice voice)
        {
            voice.Clip = null;
            voice.Key = 0;
            voice.IsSustain = false;
            if (!Bass.ChannelSlideAttribute(voice.Stream.Handle, ChannelAttribute.Volume, 0f, (int) (FADE_SECONDS * 1000)))
            {
                YargLogger.LogFormatError("Failed to fade replay voice: {0}!", Bass.LastError);
            }
        }
    }
}
