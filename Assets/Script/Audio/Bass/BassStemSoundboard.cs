#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ManagedBass;
using YARG.Audio.BASS.Effects;
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
        private const float  WHAMMY_FULL_BEND_PERCENT            = 0.9f;
        private const double PITCH_GLIDE_SECONDS                 = 0.02;
        private const float  CHORD_OVERDRIVE_MIX                 = 1f;
        private const int    MAX_SLIDE_SEMITONES                 = 5;
        private const double SLIDE_CHANCE                        = 0.35;
        private const double SLIDE_SECONDS_PER_SEMITONE          = 0.015;
        private const double MIN_SLIDE_SECONDS                   = 0.03;
        private const double MAX_SLIDE_SECONDS                   = 0.08;
        private const double PHRASE_START_SECONDS                = 0.3;
        private const double FAST_NOTE_SECONDS                   = 0.12;
        private const double BEND_IN_CHANCE                      = 0.3;
        private const int    BEND_IN_SEMITONES                   = 2;
        private const double BEND_IN_SECONDS                     = 0.12;
        private const double GRACE_CHANCE                        = 0.2;
        private const double GRACE_SECONDS                       = 0.035;
        private const float  CHORD_OVERDRIVE_DRIVE               = 15f;
        private const double DRIFT_DEPTH_CENTS                   = 6;

        private static readonly double[] DRIFT_RATES_HZ = { 0.37, 0.71, 1.13 };

        private sealed class Voice
        {
            public readonly PushStream Stream;
            public readonly PushStream BentStream;
            public BassStemClip? Clip;
            public BassStemClip? BentClip;
            public int        PushedFrames;
            public int        BentPushedFrames;
            public double     Gain;
            public double     BentShare;
            public BassOverdriveDsp? Overdrive;
            public BassOverdriveDsp? BentOverdrive;
            public bool       IsDistorted;
            public float      Frequency;
            public int        Key;
            public bool       IsSustain;
            public bool       IsLooping;
            public long       StartOrder;
            public long       StartTimestamp;
            public readonly double[] DriftPhases = new double[DRIFT_RATES_HZ.Length];
            public int        WhammySemitones;

            public Voice(PushStream stream, PushStream bentStream)
            {
                Stream = stream;
                BentStream = bentStream;
                Overdrive = BassOverdriveDsp.Attach(stream.Handle, 0, CHORD_OVERDRIVE_DRIVE);
                BentOverdrive = BassOverdriveDsp.Attach(bentStream.Handle, 0, CHORD_OVERDRIVE_DRIVE);
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
        private readonly Func<double>                 _volume;
        private readonly BassStemSeedLibrary          _seeds;
        private readonly Voice[]                      _voices = new Voice[VOICE_COUNT];
        private          int                          _heldFrets;
        private          float                        _whammyPercent;
        private          int                          _lastNote = -1;
        private          long                         _lastNoteTimestamp;
        private readonly Random                       _random = new();
        private          long                         _playCount;
        private          long                         _lastVoiceStart = Stopwatch.GetTimestamp();
        private          bool                         _disposed;

        internal BassStemSoundboard(object stemLock, AudioManager manager, BassStemFormat format,
            Func<double> volume)
        {
            _lock = stemLock;
            _format = format;
            _volume = volume;
            _seeds = new BassStemSeedLibrary(format);

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                var stream = manager.CreatePushStream(sampleRate: format.Frequency, channelCount: format.Channels);
                var bentStream = manager.CreatePushStream(sampleRate: format.Frequency, channelCount: format.Channels);
                _voices[index] = new Voice(stream: stream, bentStream: bentStream);
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
                int ornament = PickOrnament(clip, out double ornamentSeconds);
                Start(clip, key: key, gain: 1, firstFrame: 0, slideSemitones: ornament, slideSeconds: ornamentSeconds);
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
                int slide = _lastNote >= 0 && clip.Note >= 0 ? _lastNote - clip.Note : 0;
                if (Math.Abs(slide) > MAX_SLIDE_SEMITONES || _random.NextDouble() >= SLIDE_CHANCE)
                {
                    slide = 0;
                }

                double slideSeconds = Math.Clamp(Math.Abs(slide) * SLIDE_SECONDS_PER_SEMITONE,
                    MIN_SLIDE_SECONDS, MAX_SLIDE_SECONDS);
                Start(clip, key: key, gain: 1, firstFrame: Math.Min(clip.AttackEndFrame, lastFrame),
                    fadeInSeconds: LEGATO_FADE_IN_SECONDS, slideSemitones: slide, slideSeconds: slideSeconds);
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
                int fretCount = FretCount(fretMask);
                double fretGain = 1 / Math.Sqrt(fretCount);
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

                    int ornament = 0;
                    double ornamentSeconds = 0;
                    if (fretCount == 1)
                    {
                        ornament = PickOrnament(clip, out ornamentSeconds);
                    }

                    Start(clip, key: fret, gain: fretGain, firstFrame: 0, distorted: fretCount > 1,
                        slideSemitones: ornament, slideSeconds: ornamentSeconds);
                }

                if (fretCount > 1)
                {
                    _lastNote = -1;
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
                    if (!voice.IsLooping)
                    {
                        continue;
                    }

                    SetPitch(voice, glide: true);
                    if (voice.BentClip != null && Shares(voice).Bent != voice.BentShare)
                    {
                        SlideVoiceVolume(voice, _volume() * voice.Gain, PITCH_GLIDE_SECONDS);
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
                    voice.BentStream.Clear();
                    voice.Clip = null;
                    voice.BentClip = null;
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
                    voice.Overdrive?.Dispose();
                    voice.BentOverdrive?.Dispose();
                    voice.Stream.Dispose();
                    voice.BentStream.Dispose();
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

        private Voice Start(BassStemClip clip, int key, double gain, int firstFrame, double fadeInSeconds = 0,
            bool distorted = false, int slideSemitones = 0, double slideSeconds = 0)
        {
            var voice = PickVoice(key);
            SetDistorted(voice, distorted);
            voice.Stream.Clear();
            voice.BentStream.Clear();
            voice.Clip = clip;
            voice.PushedFrames = firstFrame;
            voice.Key = key;
            voice.IsLooping = clip.HasLoop && voice.IsFullyHeld(_heldFrets);
            voice.IsSustain = clip.IsSustain || voice.IsLooping;
            voice.BentClip = voice.IsLooping ? clip.Bent : null;
            voice.BentPushedFrames = firstFrame;
            voice.WhammySemitones = clip.Bent != null ? clip.BendSemitones : clip.Note >= 0 ? _seeds.WhammyBend(clip) : 0;
            voice.Gain = gain;
            if (fadeInSeconds > 0)
            {
                SetVoiceVolume(voice, 0);
                SlideVoiceVolume(voice, _volume() * gain, fadeInSeconds);
            }
            else
            {
                SetVoiceVolume(voice, _volume() * gain);
            }

            SetPitch(voice, glide: false);
            if (slideSemitones != 0 && slideSeconds > 0)
            {
                float target = voice.Frequency;
                SetVoiceFrequency(voice, (float) (target * Math.Pow(2, slideSemitones / 12.0)), 0);
                SetVoiceFrequency(voice, target, slideSeconds);
            }

            _lastNote = clip.Note;
            _lastNoteTimestamp = Stopwatch.GetTimestamp();
            voice.StartOrder = ++_playCount;
            voice.StartTimestamp = Stopwatch.GetTimestamp();
            for (int index = 0; index < voice.DriftPhases.Length; index++)
            {
                voice.DriftPhases[index] = _random.NextDouble() * 2 * Math.PI;
            }

            Feed(voice);
            return voice;
        }

        private int PickOrnament(BassStemClip clip, out double seconds)
        {
            seconds = 0;
            if (clip.Note < 0)
            {
                return 0;
            }

            double sinceLast = (Stopwatch.GetTimestamp() - _lastNoteTimestamp) / (double) Stopwatch.Frequency;
            if (sinceLast >= PHRASE_START_SECONDS && _random.NextDouble() < BEND_IN_CHANCE)
            {
                seconds = BEND_IN_SECONDS;
                return -BEND_IN_SEMITONES;
            }

            if (sinceLast >= FAST_NOTE_SECONDS && _random.NextDouble() < GRACE_CHANCE)
            {
                seconds = GRACE_SECONDS;
                return -_random.Next(1, 3);
            }

            return 0;
        }

        private static void SetDistorted(Voice voice, bool distorted)
        {
            if (voice.IsDistorted == distorted)
            {
                return;
            }

            voice.IsDistorted = distorted;
            float mix = distorted ? CHORD_OVERDRIVE_MIX : 0;
            voice.Overdrive?.Set(mix, CHORD_OVERDRIVE_DRIVE);
            voice.BentOverdrive?.Set(mix, CHORD_OVERDRIVE_DRIVE);
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
                if (previous.IsAttached || IsFading(previous))
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

                if (!IsFading(voice))
                {
                    if (oldestIdle == null || voice.StartOrder < oldestIdle.StartOrder)
                    {
                        oldestIdle = voice;
                    }

                    continue;
                }

                float volume = Loudness(voice);
                if (volume < quietestVolume)
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
                SetPitch(voice, glide: true);
                FeedLoop(voice.Stream, clip, ref voice.PushedFrames);
                if (voice.BentClip != null)
                {
                    FeedLoop(voice.BentStream, voice.BentClip, ref voice.BentPushedFrames);
                }

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

        private void FeedLoop(PushStream stream, BassStemClip clip, ref int pushedFrames)
        {
            int queuedBytes = Bass.StreamPutData(stream.Handle, IntPtr.Zero, 0);
            if (queuedBytes < 0)
            {
                YargLogger.LogFormatError("Failed to query replay queue: {0}!", Bass.LastError);
                return;
            }

            int queuedFrames = queuedBytes / (_format.Channels * FLOAT_BYTES);
            int remaining = LoopBufferFrames - queuedFrames;
            while (remaining > 0)
            {
                int frames = clip.NextLoopSegment(pushedFrames, remaining, out bool fromTail, out int sourceFrame);
                Push(stream, fromTail ? clip.LoopTail : clip.Samples, firstFrame: sourceFrame, frameCount: frames);
                remaining -= frames;
                pushedFrames += frames;
                if (pushedFrames == clip.LoopEndFrame)
                {
                    pushedFrames = clip.LoopStartFrame;
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

        private void SetPitch(Voice voice, bool glide)
        {
            float freq = _format.Frequency;
            if (voice.IsLooping)
            {
                double semitones = 0;
                if (GlobalAudioHandler.UseWhammyFx)
                {
                    int bend = voice.WhammySemitones;
                    semitones = bend > 0
                        ? bend * BendProgress
                        : -GlobalAudioHandler.WhammyPitchShiftAmount * _whammyPercent;
                }

                semitones += DriftCents(voice) / 100;
                freq = (float) (_format.Frequency * Math.Pow(2, semitones / 12));
            }

            if (glide && freq == voice.Frequency)
            {
                return;
            }

            voice.Frequency = freq;
            SetVoiceFrequency(voice, freq, glide ? PITCH_GLIDE_SECONDS : 0);
        }

        private static void SetVoiceFrequency(Voice voice, float freq, double glideSeconds)
        {
            SetFrequency(voice.Stream, freq, glideSeconds);
            SetFrequency(voice.BentStream, (float) (freq * Math.Pow(2, -voice.WhammySemitones / 12.0)), glideSeconds);
        }

        private static void SetFrequency(PushStream stream, float freq, double glideSeconds)
        {
            bool set = glideSeconds > 0
                ? Bass.ChannelSlideAttribute(stream.Handle, ChannelAttribute.Frequency, freq, (int) (glideSeconds * 1000))
                : Bass.ChannelSetAttribute(stream.Handle, ChannelAttribute.Frequency, freq);
            if (!set)
            {
                YargLogger.LogFormatError("Failed to set replay frequency: {0}!", Bass.LastError);
            }
        }

        private double BendProgress => GlobalAudioHandler.UseWhammyFx
            ? Math.Min(1, _whammyPercent / WHAMMY_FULL_BEND_PERCENT)
            : 0;

        private (double Plain, double Bent) Shares(Voice voice)
        {
            double bent = voice.BentClip != null && voice.WhammySemitones > 0 ? BendProgress : 0;
            return (1 - bent, bent);
        }

        private void SetVoiceVolume(Voice voice, double volume)
        {
            var shares = Shares(voice);
            voice.BentShare = shares.Bent;
            voice.Stream.SetVolume(volume * shares.Plain);
            voice.BentStream.SetVolume(volume * shares.Bent);
        }

        private void SlideVoiceVolume(Voice voice, double volume, double seconds)
        {
            var shares = Shares(voice);
            voice.BentShare = shares.Bent;
            SlideVolume(voice.Stream, volume * shares.Plain, seconds);
            SlideVolume(voice.BentStream, volume * shares.Bent, seconds);
        }

        private static void SlideVolume(PushStream stream, double volume, double seconds)
        {
            if (!Bass.ChannelSlideAttribute(stream.Handle, ChannelAttribute.Volume, (float) volume, (int) (seconds * 1000)))
            {
                YargLogger.LogFormatError("Failed to fade replay voice: {0}!", Bass.LastError);
            }
        }

        private static bool IsFading(Voice voice)
        {
            return Bass.ChannelIsSliding(voice.Stream.Handle, ChannelAttribute.Volume)
                || Bass.ChannelIsSliding(voice.BentStream.Handle, ChannelAttribute.Volume);
        }

        private static float Loudness(Voice voice)
        {
            Bass.ChannelGetAttribute(voice.Stream.Handle, ChannelAttribute.Volume, out float volume);
            Bass.ChannelGetAttribute(voice.BentStream.Handle, ChannelAttribute.Volume, out float bentVolume);
            return volume + bentVolume;
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

        private void FadeOut(Voice voice)
        {
            FadeOut(voice, fadeSeconds: voice.IsLooping ? FREESTYLE_RELEASE_SECONDS : VOICE_FADE_SECONDS);
        }

        private void FadeOut(Voice voice, double fadeSeconds)
        {
            voice.Clip = null;
            voice.BentClip = null;
            voice.Key = 0;
            voice.IsSustain = false;
            voice.IsLooping = false;
            SlideVolume(voice.Stream, 0, fadeSeconds);
            SlideVolume(voice.BentStream, 0, fadeSeconds);
        }
    }
}
