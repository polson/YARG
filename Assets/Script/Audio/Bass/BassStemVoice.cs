#nullable enable
using System;
using System.Diagnostics;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Engine.Guitar;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// One push stream and the clip, pitch and envelope state it is playing.
    /// </summary>
    internal sealed class BassStemVoice
    {
        private const int    FLOAT_BYTES                  = sizeof(float);
        private const double VOICE_FADE_SECONDS           = 0.08;
        private const double FREESTYLE_RELEASE_SECONDS    = 0.08;
        private const double PITCH_GLIDE_SECONDS          = 0.02;
        private const double VIBRATO_DELAY_SECONDS        = 0.3;
        private const double VIBRATO_RAMP_SECONDS         = 0.15;
        private const double VIBRATO_RATE_HZ              = 4.5;
        private const double VIBRATO_DEPTH_CENTS          = 90;
        private const double VIBRATO_RATE_SPREAD          = 0.1;
        private const double VIBRATO_RATE_WANDER          = 0.08;
        private const double VIBRATO_WANDER_HZ            = 0.3;
        private const double VIBRATO_DEPTH_SPREAD         = 0.15;
        private const double DRIFT_DEPTH_CENTS            = 6;
        private const double ATTACK_SHARP_CENTS           = 10;
        private const double ATTACK_SETTLE_SECONDS        = 0.03;
        private const double TUNING_SPREAD_CENTS          = 5;
        private const double PICK_SHAPE_SECONDS           = 0.02;
        private const double PICK_HIGH_PASS_HZ            = 2000;
        private const double SCRAPE_LEVEL                 = 0.03;
        private const double SCRAPE_DECAY_SECONDS         = 0.004;
        private const double MAX_DECAY_DB                 = 15;
        private const double DECAY_STEP_DB                = 0.25;
        private const double DECAY_SLIDE_SECONDS          = 0.05;
        private const float  WHAMMY_IDLE_PERCENT          = 0.05f;
        private const float  WHAMMY_FULL_BEND_PERCENT     = 0.9f;

        private static readonly double[] DRIFT_RATES_HZ = { 0.37, 0.71, 1.13 };

        private readonly PushStream     _stream;
        private readonly BassStemFormat _format;
        private readonly Random         _random;
        private readonly double[]       _driftPhases = new double[DRIFT_RATES_HZ.Length];
        private          float[]        _silence = Array.Empty<float>();
        private          float[]        _pickBuffer = Array.Empty<float>();

        public BassStemClip? Clip       { get; private set; }
        public double        Gain       { get; private set; }
        public int           Key        { get; private set; }
        public bool          IsLooping  { get; private set; }
        public long          StartOrder { get; set; }

        private int    _whammySemitones;
        private int    _pushedFrames;
        private double _playbackRate = 1;
        private float  _frequency;
        private long   _startTimestamp;
        private bool   _isSustain;
        private double _vibratoRateHz;
        private double _vibratoWanderPhase;
        private int    _vibratoSeed;
        private double _decayDbPerSecond;
        private double _decayStartSeconds;
        private double _decayDb;
        private double _attackCents;
        private double _tuningCents;

        public BassStemVoice(PushStream stream, BassStemFormat format, Random random)
        {
            _stream = stream;
            _format = format;
            _random = random;
        }

        public bool IsAttached => Key != 0;

        public bool IsPlainSustainFor(int fretMask)
        {
            return _isSustain && !IsLooping && (Key & fretMask) != 0;
        }

        public bool IsFullyHeld(int heldMask)
        {
            return (Key & heldMask) == Key;
        }

        public bool IsReleasedBy(int heldMask)
        {
            if (Key == GuitarEngine.OPEN_MASK)
            {
                return heldMask == 0;
            }

            return (Key & heldMask) != Key;
        }

        public void Begin(BassStemClip clip, int key, double gain, int firstFrame, double fadeInSeconds,
            double brightness, int delayFrames, int whammySemitones, int heldFrets, float whammyPercent)
        {
            _stream.Clear();
            if (delayFrames > 0)
            {
                PushSilence(delayFrames);
            }

            Clip = clip;
            _pushedFrames = firstFrame;
            Key = key;
            IsLooping = clip.HasLoop && IsFullyHeld(heldFrets);
            _isSustain = clip.IsSustain || IsLooping;
            _whammySemitones = whammySemitones;

            Gain = gain;
            _playbackRate = clip.PlaybackRate;
            _decayDbPerSecond = clip.DecayDbPerSecond;
            _decayStartSeconds = Math.Max(0, clip.LoopStartFrame - firstFrame) / (double) _format.Frequency;
            _decayDb = 0;
            if (fadeInSeconds > 0)
            {
                _stream.SetVolume(0);
                SlideVolume(Volume(), fadeInSeconds);
            }
            else
            {
                _stream.SetVolume((float) Volume());
            }

            _startTimestamp = Stopwatch.GetTimestamp();
            for (int index = 0; index < _driftPhases.Length; index++)
            {
                _driftPhases[index] = _random.NextDouble() * 2 * Math.PI;
            }

            _vibratoRateHz = VIBRATO_RATE_HZ * (1 + VIBRATO_RATE_SPREAD * (2 * _random.NextDouble() - 1));
            _vibratoWanderPhase = _random.NextDouble() * 2 * Math.PI;
            _vibratoSeed = _random.Next();
            _attackCents = clip.IsSustain && firstFrame == 0
                ? ATTACK_SHARP_CENTS * gain * (0.5 + _random.NextDouble())
                : 0;
            _tuningCents = TUNING_SPREAD_CENTS * (_random.NextDouble() - _random.NextDouble());
            SetPitch(glide: false, whammyPercent);

            bool picked = clip.IsSustain;
            if (firstFrame == 0 && (brightness != 1 || picked))
            {
                ShapePick(clip, brightness, picked ? SCRAPE_LEVEL * gain : 0);
            }
        }

        public void Feed(int loopBufferFrames, float whammyPercent)
        {
            var clip = Clip;
            if (clip == null)
            {
                return;
            }

            if (IsLooping)
            {
                SetPitch(glide: true, whammyPercent);
                Decay();
                FeedLoop(clip, loopBufferFrames);
                return;
            }

            int frames = clip.FrameCount - _pushedFrames;
            if (frames > 0)
            {
                Push(clip.Samples, firstFrame: _pushedFrames, frameCount: frames);
                _pushedFrames += frames;
            }

            if (_pushedFrames >= clip.FrameCount)
            {
                Clip = null;
            }
        }

        public void SetPitch(bool glide, float whammyPercent)
        {
            double semitones = IsLooping ? HeldSemitones(whammyPercent) : 0;
            float freq = (float) (_format.Frequency * _playbackRate * Math.Pow(2, semitones / 12));
            if (glide && freq == _frequency)
            {
                return;
            }

            _frequency = freq;
            SetFrequency(_stream, freq, glide ? PITCH_GLIDE_SECONDS : 0);
        }

        public void FadeOut()
        {
            FadeOut(fadeSeconds: IsLooping ? FREESTYLE_RELEASE_SECONDS : VOICE_FADE_SECONDS);
        }

        public void FadeOut(double fadeSeconds)
        {
            Clip = null;
            Key = 0;
            _isSustain = false;
            IsLooping = false;
            SlideVolume(0, fadeSeconds);
        }

        public void Reset()
        {
            _stream.Clear();
            Clip = null;
            Key = 0;
            _isSustain = false;
            IsLooping = false;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        public float Loudness()
        {
            Bass.ChannelGetAttribute(_stream.Handle, ChannelAttribute.Volume, out float volume);
            return volume;
        }

        public bool IsFading()
        {
            return Bass.ChannelIsSliding(_stream.Handle, ChannelAttribute.Volume);
        }

        private double HeldSeconds => (Stopwatch.GetTimestamp() - _startTimestamp) / (double) Stopwatch.Frequency;

        private double HeldSemitones(float whammyPercent)
        {
            double semitones = WhammySemitones(whammyPercent) + (DriftCents() + HumanCents()) / 100;
            if (IsWhammyIdle(whammyPercent))
            {
                semitones += VibratoCents() / 100;
            }

            return semitones;
        }

        private double WhammySemitones(float whammyPercent)
        {
            if (!GlobalAudioHandler.UseWhammyFx)
            {
                return 0;
            }

            if (_whammySemitones > 0)
            {
                double bendProgress = Math.Min(1, whammyPercent / WHAMMY_FULL_BEND_PERCENT);
                return _whammySemitones * bendProgress;
            }

            return -GlobalAudioHandler.WhammyPitchShiftAmount * whammyPercent;
        }

        private void Decay()
        {
            double decaying = HeldSeconds - _decayStartSeconds;
            if (decaying <= 0)
            {
                return;
            }

            double decibels = Math.Min(_decayDbPerSecond * decaying, MAX_DECAY_DB);
            if (decibels - _decayDb < DECAY_STEP_DB)
            {
                return;
            }

            _decayDb = decibels;
            SlideVolume(Volume(), DECAY_SLIDE_SECONDS);
        }

        private void FeedLoop(BassStemClip clip, int loopBufferFrames)
        {
            int queuedBytes = Bass.StreamPutData(_stream.Handle, IntPtr.Zero, 0);
            if (queuedBytes < 0)
            {
                YargLogger.LogFormatError("Failed to query replay queue: {0}!", Bass.LastError);
                return;
            }

            int queuedFrames = queuedBytes / (_format.Channels * FLOAT_BYTES);
            int remaining = loopBufferFrames - queuedFrames;
            while (remaining > 0)
            {
                int frames = clip.NextLoopSegment(_pushedFrames, remaining, out bool fromTail, out int sourceFrame);
                Push(fromTail ? clip.LoopTail : clip.Samples, firstFrame: sourceFrame, frameCount: frames);
                remaining -= frames;
                _pushedFrames += frames;
                if (_pushedFrames == clip.LoopEndFrame)
                {
                    _pushedFrames = clip.LoopStartFrame;
                }
            }
        }

        private unsafe void Push(float[] samples, int firstFrame, int frameCount)
        {
            int channels = _format.Channels;
            fixed (float* pointer = &samples[firstFrame * channels])
            {
                _stream.Push((IntPtr) pointer, frameCount * channels * FLOAT_BYTES);
            }
        }

        private void PushSilence(int frames)
        {
            int samples = frames * _format.Channels;
            if (_silence.Length < samples)
            {
                _silence = new float[samples];
            }

            Push(_silence, firstFrame: 0, frameCount: frames);
        }

        private void ShapePick(BassStemClip clip, double brightness, double scrape)
        {
            int channels = _format.Channels;
            int lastFrame = clip.HasLoop ? clip.LoopEndFrame - clip.LoopFadeFrames : clip.FrameCount;
            int frames = Math.Min(_format.FramesFor(PICK_SHAPE_SECONDS), lastFrame);
            if (frames <= 0)
            {
                return;
            }

            if (_pickBuffer.Length < frames * channels)
            {
                _pickBuffer = new float[frames * channels];
            }

            float smoothing = (float) Math.Exp(-2 * Math.PI * PICK_HIGH_PASS_HZ / _format.Frequency);
            float extra = (float) (brightness - 1);
            float scrapeDecay = (float) Math.Exp(-1 / (SCRAPE_DECAY_SECONDS * _format.Frequency));
            int fadeFrames = frames / 2;
            float scrapeLow = 0;
            float scrapeLevel = (float) scrape;
            for (int frame = 0; frame < frames; frame++)
            {
                float noise = scrape > 0 ? (float) (_random.NextDouble() * 2 - 1) : 0;
                scrapeLow = smoothing * scrapeLow + (1 - smoothing) * noise;
                float scraped = scrapeLevel * (noise - scrapeLow);
                scrapeLevel *= scrapeDecay;
                for (int channel = 0; channel < channels; channel++)
                {
                    int index = frame * channels + channel;
                    _pickBuffer[index] = clip.Samples[index] + scraped;
                }
            }

            for (int channel = 0; channel < channels; channel++)
            {
                float low = clip.Samples[channel];
                for (int frame = 0; frame < frames; frame++)
                {
                    int index = frame * channels + channel;
                    float sample = clip.Samples[index];
                    low = smoothing * low + (1 - smoothing) * sample;
                    int fromEnd = frames - frame;
                    float fade = fromEnd < fadeFrames ? (float) (0.5 - 0.5 * Math.Cos(Math.PI * fromEnd / fadeFrames)) : 1;
                    _pickBuffer[index] += extra * fade * (sample - low);
                }
            }

            Push(_pickBuffer, firstFrame: 0, frameCount: frames);
            _pushedFrames = frames;
        }

        private static void SetFrequency(PushStream stream, float freq, double glideSeconds)
        {
            bool set;
            if (glideSeconds > 0)
            {
                int glideMilliseconds = (int) (glideSeconds * 1000);
                set = Bass.ChannelSlideAttribute(stream.Handle, ChannelAttribute.Frequency, freq, glideMilliseconds);
            }
            else
            {
                set = Bass.ChannelSetAttribute(stream.Handle, ChannelAttribute.Frequency, freq);
            }
            if (!set)
            {
                YargLogger.LogFormatError("Failed to set replay frequency: {0}!", Bass.LastError);
            }
        }

        private void SlideVolume(double volume, double seconds)
        {
            if (!Bass.ChannelSlideAttribute(_stream.Handle, ChannelAttribute.Volume, (float) volume, (int) (seconds * 1000)))
            {
                YargLogger.LogFormatError("Failed to fade replay voice: {0}!", Bass.LastError);
            }
        }

        private double Volume()
        {
            return Gain * Math.Pow(10, -_decayDb / 20);
        }

        private double DriftCents()
        {
            double held = HeldSeconds;
            double cents = 0;
            for (int index = 0; index < DRIFT_RATES_HZ.Length; index++)
            {
                cents += Math.Sin(2 * Math.PI * DRIFT_RATES_HZ[index] * held + _driftPhases[index]);
            }

            return cents * DRIFT_DEPTH_CENTS / DRIFT_RATES_HZ.Length;
        }

        private double HumanCents()
        {
            return _tuningCents + _attackCents * Math.Exp(-HeldSeconds / ATTACK_SETTLE_SECONDS);
        }

        private double VibratoCents()
        {
            double vibrating = HeldSeconds - VIBRATO_DELAY_SECONDS;
            if (vibrating <= 0)
            {
                return 0;
            }

            double wanderSpeed = 2 * Math.PI * VIBRATO_WANDER_HZ;
            double wander = VIBRATO_RATE_WANDER / wanderSpeed
                * (Math.Cos(_vibratoWanderPhase) - Math.Cos(wanderSpeed * vibrating + _vibratoWanderPhase));
            double cycles = _vibratoRateHz * (vibrating + wander);
            double depth = VIBRATO_DEPTH_CENTS * Math.Min(1, vibrating / VIBRATO_RAMP_SECONDS)
                * (1 + VIBRATO_DEPTH_SPREAD * CycleRandom(_vibratoSeed, (int) cycles));
            return depth * 0.5 * (1 - Math.Cos(2 * Math.PI * cycles));
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

        private static bool IsWhammyIdle(float whammyPercent)
        {
            return whammyPercent <= WHAMMY_IDLE_PERCENT;
        }
    }
}
