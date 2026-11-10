#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoundboard : IDisposable
    {
        private const int    VOICE_COUNT                         = 12;
        private const int    FLOAT_BYTES                         = sizeof(float);
        private const double VOICE_FADE_SECONDS                  = 0.08;
        private const double FREESTYLE_RELEASE_SECONDS           = 0.08;
        private const double FREESTYLE_MIN_TRANSITION_SECONDS    = 0.015;
        private const double FREESTYLE_MAX_TRANSITION_SECONDS    = 0.03;
        private const double FREESTYLE_TRANSITION_INTERVAL_RATIO = 0.25;
        private const double LEGATO_TRANSITION_SECONDS           = 0.035;
        private const double LEGATO_FADE_IN_SECONDS              = 0.01;
        private const double LOOP_BUFFER_SECONDS                 = 0.25;
        private const float  WHAMMY_IDLE_PERCENT                 = 0.05f;
        private const float  WHAMMY_BEND_ENGAGE_PERCENT          = 0.5f;
        private const double VIBRATO_DELAY_SECONDS               = 0.1;
        private const double VIBRATO_RAMP_SECONDS                = 0.15;
        private const double VIBRATO_RATE_HZ                     = 4.5;
        private const double VIBRATO_DEPTH_CENTS                 = 90;
        private const double VIBRATO_RATE_SPREAD                 = 0.1;
        private const double VIBRATO_RATE_WANDER                 = 0.08;
        private const double VIBRATO_WANDER_HZ                   = 0.3;
        private const double VIBRATO_DEPTH_SPREAD                = 0.15;
        private const double DRIFT_DEPTH_CENTS                   = 6;

        private static readonly double[] DRIFT_RATES_HZ = { 0.37, 0.71, 1.13 };

        private sealed class Voice
        {
            public readonly PushStream Stream;
            public readonly int        PitchFx;
            public BassStemClip? Clip;
            public int        PushedFrames;
            public int        Key;
            public bool       IsSustain;
            public bool       IsLooping;
            public long       StartOrder;
            public long       StartTimestamp;
            public readonly double[] DriftPhases = new double[DRIFT_RATES_HZ.Length];
            public double     VibratoRateHz;
            public double     VibratoWanderPhase;
            public int        VibratoSeed;
            public int        WhammySemitones;

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

        private readonly object                       _lock;
        private readonly BassStemFormat               _format;
        private readonly PitchShiftParametersStruct   _pitchParams;
        private readonly Func<double>                 _volume;
        private readonly BassStemSeedLibrary          _seeds;
        private readonly Voice[]                      _voices = new Voice[VOICE_COUNT];
        private          int                          _heldFrets;
        private          float                        _whammyPercent;
        private readonly Random                       _random = new();
        private          long                         _playCount;
        private          long                         _lastVoiceStart = Stopwatch.GetTimestamp();
        private          bool                         _disposed;

        internal BassStemSoundboard(object stemLock, AudioManager manager, BassStemFormat format,
            PitchShiftParametersStruct pitchParams, Func<double> volume)
        {
            _lock = stemLock;
            _format = format;
            _pitchParams = pitchParams;
            _volume = volume;
            _seeds = new BassStemSeedLibrary(format);

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                var stream = manager.CreatePushStream(sampleRate: format.Frequency, channelCount: format.Channels);
                var pitchFx = BassHelpers.FXAddParameters(stream.Handle, EffectType.PitchShift, _pitchParams);
                _voices[index] = new Voice(stream: stream, pitchFx: pitchFx);
            }
        }

        private int LoopBufferFrames => _format.FramesFor(LOOP_BUFFER_SECONDS);

        public void SetChordTrack(BassChordTrack? chords)
        {
            _seeds.SetChordTrack(chords);
        }

        public void RequestSeeds(IReadOnlyList<SoloSection> phrases, List<GuitarNote> notes,
            Func<double, double> playbackTime, int lowestNote)
        {
            _seeds.Request(phrases, notes, playbackTime, lowestNote);
        }

        public void SelectSeedSet(double soloTime, double songTime)
        {
            _seeds.Select(soloTime, songTime);
        }

        public bool HasSeedSet(double soloTime)
        {
            return _seeds.HasSeeds(soloTime);
        }

        public void Play(int key)
        {
            lock (_lock)
            {
                if (_disposed || !_seeds.TryGetClip(key, out var clip))
                {
                    return;
                }

                FadePreviousVoices(FREESTYLE_MIN_TRANSITION_SECONDS);
                Start(clip, key: key, gain: 1, firstFrame: 0);
            }
        }

        public void PlayLegato(int key)
        {
            lock (_lock)
            {
                if (_disposed || !_seeds.TryGetClip(key, out var clip))
                {
                    return;
                }

                FadePreviousVoices(LEGATO_TRANSITION_SECONDS);
                int lastFrame = (clip.HasLoop ? clip.LoopEndFrame : clip.FrameCount) - 1;
                var voice = Start(clip, key: key, gain: 0, firstFrame: Math.Min(clip.AttackEndFrame, lastFrame));
                if (!Bass.ChannelSlideAttribute(voice.Stream.Handle, ChannelAttribute.Volume, (float) _volume(),
                    (int) (LEGATO_FADE_IN_SECONDS * 1000)))
                {
                    YargLogger.LogFormatError("Failed to fade in replay voice: {0}!", Bass.LastError);
                }
            }
        }

        public void PlayStrum(int fretMask)
        {
            lock (_lock)
            {
                if (_disposed || !_seeds.HasClip(fretMask))
                {
                    return;
                }

                FadePreviousVoices(FREESTYLE_MIN_TRANSITION_SECONDS);
                double fretGain = 1 / Math.Sqrt(FretCount(fretMask));
                for (int fret = 1; fret < GuitarEngine.OPEN_MASK; fret <<= 1)
                {
                    if ((fretMask & fret) == 0)
                    {
                        continue;
                    }

                    if (!_seeds.TryGetClip(fret, out var clip))
                    {
                        continue;
                    }

                    Start(clip, key: fret, gain: fretGain, firstFrame: 0);
                }
            }
        }

        public void Release(int fretMask)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    if (voice.IsPlainSustainFor(fretMask))
                    {
                        FadeOut(voice);
                    }
                }
            }
        }

        public void SetHeldFrets(int fretMask)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _heldFrets = fretMask;
                foreach (var voice in _voices)
                {
                    if (voice.IsLooping && voice.IsReleasedBy(fretMask))
                    {
                        FadeOut(voice);
                    }
                }
            }
        }

        public void SetWhammyPitch(float percent)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _whammyPercent = percent;
                foreach (var voice in _voices)
                {
                    if (voice.IsLooping)
                    {
                        SetPitch(voice);
                    }
                }
            }
        }

        public void StopAll()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    if (voice.IsAttached)
                    {
                        FadeOut(voice);
                    }
                }
            }
        }

        internal void PrepareSeedJobs(List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            _seeds.PrepareJobs(jobs);
        }

        internal void PublishSeeds(CancellationToken token)
        {
            _seeds.Publish(token);
        }

        internal void Clear()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    voice.Stream.Clear();
                    voice.Clip = null;
                    voice.Key = 0;
                    voice.IsSustain = false;
                    voice.IsLooping = false;
                }

                _heldFrets = 0;
                _whammyPercent = 0;
                _lastVoiceStart = Stopwatch.GetTimestamp();
            }
        }

        internal void Pump()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    Feed(voice);
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _seeds.Forget();
                foreach (var voice in _voices)
                {
                    voice.Stream.Dispose();
                }
            }
        }

        private static int FretCount(int fretMask)
        {
            int count = 0;
            for (int fret = 1; fret < GuitarEngine.OPEN_MASK; fret <<= 1)
            {
                if ((fretMask & fret) != 0)
                {
                    count++;
                }
            }

            return count;
        }

        private Voice Start(BassStemClip clip, int key, double gain, int firstFrame)
        {
            var voice = PickVoice(key);
            voice.Stream.Clear();
            voice.Stream.SetVolume(_volume() * gain);
            voice.Clip = clip;
            voice.PushedFrames = firstFrame;
            voice.Key = key;
            voice.IsLooping = clip.HasLoop && voice.IsFullyHeld(_heldFrets);
            voice.IsSustain = clip.IsSustain || voice.IsLooping;
            SetPitch(voice);
            voice.StartOrder = ++_playCount;
            voice.StartTimestamp = Stopwatch.GetTimestamp();
            for (int index = 0; index < voice.DriftPhases.Length; index++)
            {
                voice.DriftPhases[index] = _random.NextDouble() * 2 * Math.PI;
            }

            voice.VibratoRateHz = VIBRATO_RATE_HZ * (1 + VIBRATO_RATE_SPREAD * (2 * _random.NextDouble() - 1));
            voice.VibratoWanderPhase = _random.NextDouble() * 2 * Math.PI;
            voice.VibratoSeed = _random.Next();
            voice.WhammySemitones = clip.Note >= 0 ? _seeds.WhammyBend(clip) : 0;

            Feed(voice);
            return voice;
        }

        private void FadePreviousVoices(double minSeconds)
        {
            long now = Stopwatch.GetTimestamp();
            double interval = (now - _lastVoiceStart) / (double) Stopwatch.Frequency;
            double transition = Math.Clamp(interval * FREESTYLE_TRANSITION_INTERVAL_RATIO,
                minSeconds, Math.Max(minSeconds, FREESTYLE_MAX_TRANSITION_SECONDS));
            _lastVoiceStart = now;
            foreach (var previous in _voices)
            {
                if (previous.IsAttached || Bass.ChannelIsSliding(previous.Stream.Handle, ChannelAttribute.Volume))
                {
                    FadeOut(previous, fadeSeconds: transition);
                }
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

            if (voice.IsLooping)
            {
                FeedLoop(voice, clip);
                return;
            }

            int frames = clip.FrameCount - voice.PushedFrames;
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
                int frames = clip.NextLoopSegment(voice.PushedFrames, remaining, out bool fromTail, out int sourceFrame);
                Push(voice.Stream, fromTail ? clip.LoopTail : clip.Samples, firstFrame: sourceFrame, frameCount: frames);
                remaining -= frames;
                voice.PushedFrames += frames;
                if (voice.PushedFrames == clip.LoopEndFrame)
                {
                    voice.PushedFrames = clip.LoopStartFrame;
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
            pitch.fPitchShift = 1f;
            if (voice.IsLooping)
            {
                double semitones = 0;
                if (GlobalAudioHandler.UseWhammyFx)
                {
                    int bend = voice.WhammySemitones;
                    semitones = bend > 0
                        ? (_whammyPercent >= WHAMMY_BEND_ENGAGE_PERCENT ? bend : 0)
                        : -GlobalAudioHandler.WhammyPitchShiftAmount * _whammyPercent;
                }

                semitones += DriftCents(voice) / 100;
                if (_whammyPercent <= WHAMMY_IDLE_PERCENT)
                {
                    semitones += VibratoCents(voice) / 100;
                }

                pitch.fPitchShift = (float) Math.Pow(2, semitones / 12);
            }

            if (!BassHelpers.FXSetParameters(voice.PitchFx, pitch))
            {
                YargLogger.LogFormatError("Failed to set replay pitch: {0}!", Bass.LastError);
            }
        }

        private static double DriftCents(Voice voice)
        {
            double held = (Stopwatch.GetTimestamp() - voice.StartTimestamp) / (double) Stopwatch.Frequency;
            double cents = 0;
            for (int index = 0; index < DRIFT_RATES_HZ.Length; index++)
            {
                cents += Math.Sin(2 * Math.PI * DRIFT_RATES_HZ[index] * held + voice.DriftPhases[index]);
            }

            return cents * DRIFT_DEPTH_CENTS / DRIFT_RATES_HZ.Length;
        }

        private static double VibratoCents(Voice voice)
        {
            double held = (Stopwatch.GetTimestamp() - voice.StartTimestamp) / (double) Stopwatch.Frequency;
            double vibrating = held - VIBRATO_DELAY_SECONDS;
            if (vibrating <= 0)
            {
                return 0;
            }

            double wanderSpeed = 2 * Math.PI * VIBRATO_WANDER_HZ;
            double wander = VIBRATO_RATE_WANDER / wanderSpeed
                * (Math.Cos(voice.VibratoWanderPhase) - Math.Cos(wanderSpeed * vibrating + voice.VibratoWanderPhase));
            double cycles = voice.VibratoRateHz * (vibrating + wander);
            double depth = VIBRATO_DEPTH_CENTS * Math.Min(1, vibrating / VIBRATO_RAMP_SECONDS)
                * (1 + VIBRATO_DEPTH_SPREAD * CycleRandom(voice.VibratoSeed, (int) cycles));
            return depth * 0.5 * Math.Sin(2 * Math.PI * cycles);
        }

        private static double CycleRandom(int seed, int cycle)
        {
            uint hash = (uint) seed ^ (uint) cycle * 0x9E3779B9u;
            hash ^= hash >> 16;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 13;
            hash *= 0xC2B2AE35u;
            hash ^= hash >> 16;
            return hash / (double) uint.MaxValue * 2 - 1;
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
