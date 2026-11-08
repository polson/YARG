#nullable enable
using System;
using System.Diagnostics;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Core.Audio;
using YARG.Core.Engine.Guitar;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemVoicePool : IDisposable
    {
        private const int    VOICE_COUNT                         = 12;
        private const int    FLOAT_BYTES                         = sizeof(float);
        private const double VOICE_FADE_SECONDS                  = 0.08;
        private const double FREESTYLE_RELEASE_SECONDS           = 0.12;
        private const double FREESTYLE_MIN_TRANSITION_SECONDS    = 0.015;
        private const double FREESTYLE_MAX_TRANSITION_SECONDS    = 0.03;
        private const double FREESTYLE_TRANSITION_INTERVAL_RATIO = 0.25;
        private const double LOOP_BUFFER_SECONDS                 = 0.25;

        private sealed class Voice
        {
            public readonly PushStream Stream;
            public readonly int        PitchFx;
            public float      Pitch;
            public BassStemClip? Clip;
            public int        Generation;
            public int        PushedFrames;
            public int        Key;
            public bool       IsSustain;
            public bool       IsLooping;
            public long       StartOrder;

            public Voice(PushStream stream, int pitchFx)
            {
                Stream = stream;
                PitchFx = pitchFx;
            }

            public bool IsAttached => Key != 0;

            public bool IsPlainSustainFor(int fretMask)
            {
                return IsSustain && !IsLooping && (Key & fretMask) != 0;
            }

            public bool IsFullyHeld(int heldMask)
            {
                return (Key & heldMask) == Key;
            }

            public bool IsReleasedBy(int heldMask)
            {
                return Key == GuitarEngine.OPEN_MASK ? heldMask == 0 : (Key & heldMask) != Key;
            }
        }

        private readonly BassStemFormat               _format;
        private readonly PitchShiftParametersStruct   _pitchParams;
        private readonly Func<double>                 _volume;
        private readonly Voice[]                      _voices = new Voice[VOICE_COUNT];
        private          int                          _heldFrets;
        private          float                        _whammyPitch = 1f;
        private          long                         _playCount;
        private          long                         _lastVoiceStart = Stopwatch.GetTimestamp();

        public BassStemVoicePool(AudioManager manager, BassStemFormat format,
            PitchShiftParametersStruct pitchParams, Func<double> volume)
        {
            _format = format;
            _pitchParams = pitchParams;
            _volume = volume;

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                var stream = manager.CreatePushStream(sampleRate: format.Frequency, channelCount: format.Channels);
                var pitchFx = BassHelpers.FXAddParameters(stream.Handle, EffectType.PitchShift, pitchParams);
                _voices[index] = new Voice(stream: stream, pitchFx: pitchFx);
            }
        }

        private int LoopBufferFrames => _format.FramesFor(LOOP_BUFFER_SECONDS);

        public void Start(BassStemClip clip, int key, int semitones, double gain, bool loopSustains)
        {
            var voice = PickVoice(key);
            voice.Stream.Clear();
            voice.Stream.SetVolume(_volume() * gain);
            voice.Clip = clip;
            voice.Generation = clip.Generation;
            voice.PushedFrames = 0;
            voice.Key = key;
            voice.IsLooping = loopSustains && clip.HasLoop && voice.IsFullyHeld(_heldFrets);
            voice.IsSustain = clip.IsSustain || voice.IsLooping;
            voice.Pitch = (float) Math.Pow(2, semitones / 12.0);
            SetPitch(voice);
            voice.StartOrder = ++_playCount;
            Feed(voice);
        }

        public void FadePreviousVoices()
        {
            long now = Stopwatch.GetTimestamp();
            double interval = (now - _lastVoiceStart) / (double) Stopwatch.Frequency;
            double transition = Math.Clamp(interval * FREESTYLE_TRANSITION_INTERVAL_RATIO,
                FREESTYLE_MIN_TRANSITION_SECONDS, FREESTYLE_MAX_TRANSITION_SECONDS);
            _lastVoiceStart = now;
            foreach (var previous in _voices)
            {
                if (previous.IsAttached || Bass.ChannelIsSliding(previous.Stream.Handle, ChannelAttribute.Volume))
                {
                    FadeOut(previous, fadeSeconds: transition);
                }
            }
        }

        public void Release(int fretMask)
        {
            foreach (var voice in _voices)
            {
                if (voice.IsPlainSustainFor(fretMask))
                {
                    FadeOut(voice);
                }
            }
        }

        public void SetHeldFrets(int fretMask)
        {
            _heldFrets = fretMask;
            foreach (var voice in _voices)
            {
                if (voice.IsLooping && voice.IsReleasedBy(fretMask))
                {
                    FadeOut(voice);
                }
            }
        }

        public void SetWhammyPitch(float shift)
        {
            if (_whammyPitch == shift)
            {
                return;
            }

            _whammyPitch = shift;
            foreach (var voice in _voices)
            {
                if (voice.IsLooping)
                {
                    SetPitch(voice);
                }
            }
        }

        public void StopAll()
        {
            foreach (var voice in _voices)
            {
                if (voice.IsAttached)
                {
                    FadeOut(voice);
                }
            }
        }

        public void Clear()
        {
            foreach (var voice in _voices)
            {
                voice.Stream.Clear();
                voice.Clip = null;
                voice.Key = 0;
                voice.IsSustain = false;
                voice.IsLooping = false;
            }

            _heldFrets = 0;
            _whammyPitch = 1f;
            _lastVoiceStart = Stopwatch.GetTimestamp();
        }

        public void Pump()
        {
            foreach (var voice in _voices)
            {
                Feed(voice);
            }
        }

        public void Dispose()
        {
            foreach (var voice in _voices)
            {
                voice.Stream.Dispose();
            }
        }

        private Voice PickVoice(int key)
        {
            Voice? oldestIdle = null;
            Voice? quietestTail = null;
            float quietestVolume = float.MaxValue;
            Voice oldest = _voices[0];
            foreach (var voice in _voices)
            {
                if (voice.Key == key)
                {
                    return voice;
                }

                if (voice.StartOrder < oldest.StartOrder)
                {
                    oldest = voice;
                }

                if (voice.IsAttached)
                {
                    continue;
                }

                if (!Bass.ChannelIsSliding(voice.Stream.Handle, ChannelAttribute.Volume))
                {
                    if (oldestIdle == null || voice.StartOrder < oldestIdle.StartOrder)
                    {
                        oldestIdle = voice;
                    }

                    continue;
                }

                bool hasVolume = Bass.ChannelGetAttribute(voice.Stream.Handle, ChannelAttribute.Volume, out float volume);
                if (hasVolume && volume < quietestVolume)
                {
                    quietestTail = voice;
                    quietestVolume = volume;
                }
            }

            return oldestIdle ?? quietestTail ?? oldest;
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

            if (voice.IsLooping)
            {
                FeedLoop(voice, clip);
                return;
            }

            int frames = clip.FilledFrames - voice.PushedFrames;
            if (frames > 0)
            {
                Push(voice.Stream, clip.Samples, firstFrame: voice.PushedFrames, frameCount: frames);
                voice.PushedFrames += frames;
            }

            if (voice.PushedFrames >= clip.FrameCount)
            {
                voice.Clip = null;
            }
        }

        private void FeedLoop(Voice voice, BassStemClip clip)
        {
            int queuedBytes = Bass.StreamPutData(voice.Stream.Handle, IntPtr.Zero, 0);
            if (queuedBytes < 0)
            {
                YargLogger.LogFormatError("Failed to query replay queue: {0}!", Bass.LastError);
                return;
            }

            int queuedFrames = queuedBytes / (_format.Channels * FLOAT_BYTES);
            int remaining = LoopBufferFrames - queuedFrames;
            while (remaining > 0)
            {
                var loop = clip.Loop;
                int frames = loop.NextSegment(voice.PushedFrames, remaining, out bool fromTail, out int sourceFrame);
                Push(voice.Stream, fromTail ? loop.Tail : clip.Samples, firstFrame: sourceFrame, frameCount: frames);
                remaining -= frames;
                voice.PushedFrames += frames;
                if (voice.PushedFrames == loop.EndFrame)
                {
                    voice.PushedFrames = loop.StartFrame;
                }
            }
        }

        private unsafe void Push(PushStream stream, float[] samples, int firstFrame, int frameCount)
        {
            int channels = _format.Channels;
            fixed (float* pointer = &samples[firstFrame * channels])
            {
                stream.Push((IntPtr) pointer, frameCount * channels * FLOAT_BYTES);
            }
        }

        private void SetPitch(Voice voice)
        {
            var pitch = _pitchParams;
            pitch.fPitchShift = voice.Pitch;
            if (voice.IsLooping)
            {
                pitch.fPitchShift *= _whammyPitch;
            }

            if (!BassHelpers.FXSetParameters(voice.PitchFx, pitch))
            {
                YargLogger.LogFormatError("Failed to set replay pitch: {0}!", Bass.LastError);
            }
        }

        private void FadeOut(Voice voice)
        {
            FadeOut(voice, fadeSeconds: voice.IsLooping ? FREESTYLE_RELEASE_SECONDS : VOICE_FADE_SECONDS);
        }

        private void FadeOut(Voice voice, double fadeSeconds)
        {
            voice.Clip = null;
            voice.Key = 0;
            voice.IsSustain = false;
            voice.IsLooping = false;
            if (!Bass.ChannelSlideAttribute(voice.Stream.Handle, ChannelAttribute.Volume, 0f, (int) (fadeSeconds * 1000)))
            {
                YargLogger.LogFormatError("Failed to fade replay voice: {0}!", Bass.LastError);
            }
        }
    }
}
