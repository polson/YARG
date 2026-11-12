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
        private const int    RELEASE_KEY                         = 1 << 8;
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
        private const float  WHAMMY_FULL_BEND_PERCENT            = 0.9f;
        private const double PITCH_GLIDE_SECONDS                 = 0.02;
        private const float  CHORD_OVERDRIVE_DRIVE               = 15f;
        private const double VIBRATO_DELAY_SECONDS               = 0.3;
        private const double VIBRATO_RAMP_SECONDS                = 0.15;
        private const double VIBRATO_RATE_HZ                     = 4.5;
        private const double VIBRATO_DEPTH_CENTS                 = 90;
        private const double VIBRATO_RATE_SPREAD                 = 0.1;
        private const double VIBRATO_RATE_WANDER                 = 0.08;
        private const double VIBRATO_WANDER_HZ                   = 0.3;
        private const double VIBRATO_DEPTH_SPREAD                = 0.15;
        private const double DRIFT_DEPTH_CENTS                   = 6;
        private const double ATTACK_SHARP_CENTS                  = 10;
        private const double ATTACK_SETTLE_SECONDS               = 0.03;
        private const double TUNING_SPREAD_CENTS                 = 5;
        private const double PICK_VELOCITY_DB                    = 3;
        private const double PICK_BRIGHTNESS_SPREAD              = 0.25;
        private const double FAST_NOTE_SECONDS                   = 0.15;
        private const double FAST_SOFTEN_DB                      = 1.5;
        private const double FAST_DULLING                        = 0.3;
        private const double PICK_SHAPE_SECONDS                  = 0.02;
        private const double PICK_HIGH_PASS_HZ                   = 2000;
        private const double SCRAPE_LEVEL                        = 0.03;
        private const double SCRAPE_DECAY_SECONDS                = 0.004;
        private const double MAX_DECAY_DB                        = 15;
        private const double DECAY_STEP_DB                       = 0.25;
        private const double DECAY_SLIDE_SECONDS                 = 0.05;
        private const double STRUM_MIN_SPREAD_SECONDS            = 0.008;
        private const double STRUM_MAX_SPREAD_SECONDS            = 0.015;
        private const int    SYNTH_AMP_PRIORITY                  = 1;
        private const float  SYNTH_REVERB_DAMP                   = 0.5f;
        private const float  MAX_SYNTH_GAIN                      = 16f;

        private static readonly double[] CHORD_NOTE_GAINS = { 1, 0.8, 0.65 };

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
            public BassMixerBus? Bus;
            public bool       IsSynth;
            public double     PlaybackRate = 1;
            public float      Frequency;
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
            public double     DecayDbPerSecond;
            public double     DecayStartSeconds;
            public double     DecayDb;
            public double     AttackCents;
            public double     TuningCents;
            public float[]    PickBuffer = Array.Empty<float>();

            public Voice(PushStream stream, PushStream bentStream)
            {
                Stream = stream;
                BentStream = bentStream;
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
        private readonly Random                       _random = new();
        private readonly List<BassStemClip>           _chordClips = new();
        private          float[]                      _silence = Array.Empty<float>();
        private          long                         _playCount;
        private          long                         _lastVoiceStart = Stopwatch.GetTimestamp();
        private          int                          _lastLayerTake;
        private          bool                         _disposed;
        private readonly BassMixerBus?                _cleanBus;
        private readonly BassMixerBus?                _ampBus;
        private readonly BassOverdriveDsp?            _amp;
        private readonly BassMixerBus?                _synthBus;
        private readonly BassLeadAmpDsp?              _synthAmp;
        private          BassLeadAmpTone              _synthTone = BassLeadAmpTone.Default;
        private readonly BassFreeverbDsp?             _synthReverb;

        internal BassStemSoundboard(object stemLock, AudioManager manager, BassStemFormat format,
            Func<double> volume)
        {
            _lock = stemLock;
            _format = format;
            _volume = volume;
            _seeds = new BassStemSeedLibrary(format);

            BassCabinetSet? cabinets = null;
            if (manager is BassAudioManager bassManager)
            {
                _cleanBus = bassManager.CreateMixerBus(format.Frequency, format.Channels);
                _ampBus = bassManager.CreateMixerBus(format.Frequency, format.Channels);
                if (_cleanBus == null || _ampBus == null)
                {
                    _cleanBus?.Dispose();
                    _ampBus?.Dispose();
                    _cleanBus = null;
                    _ampBus = null;
                }
                else
                {
                    _amp = BassOverdriveDsp.Attach(_ampBus.SourceHandle, 1, CHORD_OVERDRIVE_DRIVE);
                    if (BassSynthGuitar.Enabled)
                    {
                        _synthBus = bassManager.CreateMixerBus(format.Frequency, format.Channels);
                        cabinets = BassGuitarSamples.LoadCabinets();
                        _synthAmp = _synthBus != null
                            ? BassLeadAmpDsp.Attach(_synthBus.SourceHandle, 0, cabinets, SYNTH_AMP_PRIORITY)
                            : null;
                        if (_synthBus != null && _synthAmp != null)
                        {
                            _synthReverb = BassFreeverbDsp.Create(_synthBus.SourceHandle, 1,
                                BassLeadAmpTone.DEFAULT_REVERB_WET, BassLeadAmpTone.DEFAULT_REVERB_ROOM, SYNTH_REVERB_DAMP);
                        }
                        else
                        {
                            _synthBus?.Dispose();
                            _synthBus = null;
                        }
                    }
                }
            }

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                _voices[index] = new Voice(stream: CreateVoiceStream(manager), bentStream: CreateVoiceStream(manager));
                _voices[index].Bus = _cleanBus;
            }

            if (_synthBus != null)
            {
                _seeds.UseSynth(() => BassGuitarSamples.Load(format), cabinets);
            }
        }

        private PushStream CreateVoiceStream(AudioManager manager)
        {
            if (_cleanBus == null)
            {
                return manager.CreatePushStream(sampleRate: _format.Frequency, channelCount: _format.Channels);
            }

            var stream = BassPushStream.Create(_format.Frequency, _format.Channels)
                ?? throw new InvalidOperationException($"Failed to create BASS push stream: {Bass.LastError}");
            _cleanBus.Take(stream.Handle);
            return stream;
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

                var pick = NextPick();
                FadePreviousVoices(FREESTYLE_MIN_TRANSITION_SECONDS);
                Start(Layered(clip, pick.Force), key: key, gain: pick.Gain, firstFrame: 0, brightness: pick.Brightness);
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
                Start(clip, key: key, gain: 1, firstFrame: Math.Min(clip.AttackEndFrame, lastFrame),
                    fadeInSeconds: LEGATO_FADE_IN_SECONDS);
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

                var pick = NextPick();
                FadePreviousVoices(FREESTYLE_MIN_TRANSITION_SECONDS);
                if (FretCount(fretMask) > 1 && _seeds.TryGetChordClips(fretMask, _chordClips))
                {
                    StrumChord(fretMask, pick.Gain, pick.Brightness);
                    return;
                }

                double fretGain = pick.Gain / Math.Sqrt(FretCount(fretMask));
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

                    Start(Layered(clip, pick.Force), key: fret, gain: fretGain, firstFrame: 0, brightness: pick.Brightness);
                }
            }
        }

        private void StrumChord(int fretMask, double gain, double brightness)
        {
            if (_chordClips.Count == 1)
            {
                Start(_chordClips[0], key: fretMask, gain: gain, firstFrame: 0, brightness: brightness);
                return;
            }

            double power = 0;
            for (int index = 0; index < _chordClips.Count; index++)
            {
                power += CHORD_NOTE_GAINS[index] * CHORD_NOTE_GAINS[index];
            }

            double spread = STRUM_MIN_SPREAD_SECONDS
                + _random.NextDouble() * (STRUM_MAX_SPREAD_SECONDS - STRUM_MIN_SPREAD_SECONDS);
            int fret = 1;
            Voice? root = null;
            for (int index = 0; index < _chordClips.Count; index++)
            {
                while ((fretMask & fret) == 0)
                {
                    fret <<= 1;
                }

                var voice = Start(_chordClips[index], key: fret, gain: gain * CHORD_NOTE_GAINS[index] / Math.Sqrt(power),
                    firstFrame: 0, brightness: brightness, delayFrames: _format.FramesFor(spread * index));
                fret <<= 1;
                if (root == null)
                {
                    root = voice;
                }
                else if (voice.WhammySemitones != root.WhammySemitones)
                {
                    voice.BentClip = null;
                    voice.WhammySemitones = root.WhammySemitones;
                    SetVoiceVolume(voice, VoiceVolume(voice));
                    SetPitch(voice, glide: false);
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
                        PlayRelease(voice);
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
                        PlayRelease(voice);
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
                        SlideVoiceVolume(voice, VoiceVolume(voice), PITCH_GLIDE_SECONDS);
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
                    voice.Stream.Dispose();
                    voice.BentStream.Dispose();
                }

                _amp?.Dispose();
                _synthReverb?.Dispose();
                _synthAmp?.Dispose();
                _cleanBus?.Dispose();
                _ampBus?.Dispose();
                _synthBus?.Dispose();
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

        private (double Gain, double Brightness, double Force) NextPick()
        {
            double interval = (Stopwatch.GetTimestamp() - _lastVoiceStart) / (double) Stopwatch.Frequency;
            double fastness = Math.Max(0, 1 - interval / FAST_NOTE_SECONDS);
            double force = _random.NextDouble() - _random.NextDouble();
            double decibels = PICK_VELOCITY_DB * force - FAST_SOFTEN_DB * fastness;
            double brightness = 1 + PICK_BRIGHTNESS_SPREAD * force - FAST_DULLING * fastness;
            return (Math.Pow(10, decibels / 20), brightness, force);
        }

        private BassStemClip Layered(BassStemClip clip, double force)
        {
            var layers = clip.Layers;
            if (layers.Length == 0)
            {
                return clip;
            }

            int layer = Math.Clamp((int) ((force + 1) / 2 * layers.Length), 0, layers.Length - 1);
            var chosen = layers[layer];
            int count = chosen.Takes.Length + 1;
            _lastLayerTake = count > 1 ? (_lastLayerTake + 1 + _random.Next(count - 1)) % count : 0;
            return _lastLayerTake == 0 ? chosen : chosen.Takes[_lastLayerTake - 1];
        }

        private void PlayRelease(Voice voice)
        {
            var releases = voice.Clip?.Releases;
            if (!voice.IsSynth || releases == null || releases.Length == 0)
            {
                return;
            }

            Start(releases[_random.Next(releases.Length)], key: RELEASE_KEY, gain: voice.Gain, firstFrame: 0);
        }

        private Voice Start(BassStemClip clip, int key, double gain, int firstFrame, double fadeInSeconds = 0,
            bool distorted = false, double brightness = 1, int delayFrames = 0)
        {
            var voice = PickVoice(key);
            Route(voice, clip.IsSynth, distorted);
            voice.Stream.Clear();
            voice.BentStream.Clear();
            if (delayFrames > 0)
            {
                PushSilence(voice, delayFrames);
            }
            voice.Clip = clip;
            voice.PushedFrames = firstFrame;
            voice.Key = key;
            voice.IsLooping = clip.HasLoop && voice.IsFullyHeld(_heldFrets);
            voice.IsSustain = clip.IsSustain || voice.IsLooping;
            voice.BentClip = voice.IsLooping ? clip.Bent : null;
            voice.BentPushedFrames = firstFrame;
            voice.WhammySemitones = clip.Bent != null ? clip.BendSemitones : clip.Note >= 0 ? _seeds.WhammyBend(clip) : 0;
            voice.Gain = gain;
            voice.IsSynth = clip.IsSynth && _synthBus != null;
            voice.PlaybackRate = clip.PlaybackRate;
            if (voice.IsSynth)
            {
                var tone = _seeds.Tone;
                if (tone != _synthTone && _synthAmp?.SetTone(tone) == true)
                {
                    _synthTone = tone;
                    _synthReverb?.SetParams(1, tone.ReverbWet, tone.ReverbRoom, SYNTH_REVERB_DAMP, 1);
                }

                _synthAmp?.SetGain((float) Math.Clamp(_volume() * _seeds.SynthGain, 0, MAX_SYNTH_GAIN));
            }

            voice.DecayDbPerSecond = clip.DecayDbPerSecond;
            voice.DecayStartSeconds = Math.Max(0, clip.LoopStartFrame - firstFrame) / (double) _format.Frequency;
            voice.DecayDb = 0;
            if (fadeInSeconds > 0)
            {
                SetVoiceVolume(voice, 0);
                SlideVoiceVolume(voice, VoiceVolume(voice), fadeInSeconds);
            }
            else
            {
                SetVoiceVolume(voice, VoiceVolume(voice));
            }

            voice.StartOrder = ++_playCount;
            voice.StartTimestamp = Stopwatch.GetTimestamp();
            for (int index = 0; index < voice.DriftPhases.Length; index++)
            {
                voice.DriftPhases[index] = _random.NextDouble() * 2 * Math.PI;
            }

            voice.VibratoRateHz = VIBRATO_RATE_HZ * (1 + VIBRATO_RATE_SPREAD * (2 * _random.NextDouble() - 1));
            voice.VibratoWanderPhase = _random.NextDouble() * 2 * Math.PI;
            voice.VibratoSeed = _random.Next();
            voice.AttackCents = voice.IsSynth && clip.IsSustain && firstFrame == 0
                ? ATTACK_SHARP_CENTS * gain * (0.5 + _random.NextDouble())
                : 0;
            voice.TuningCents = voice.IsSynth
                ? TUNING_SPREAD_CENTS * (_random.NextDouble() - _random.NextDouble())
                : 0;
            SetPitch(voice, glide: false);

            bool picked = voice.IsSynth && clip.IsSustain;
            if (firstFrame == 0 && (brightness != 1 || picked))
            {
                ShapePick(voice, clip, brightness, picked ? SCRAPE_LEVEL * gain : 0);
            }

            Feed(voice);
            return voice;
        }

        private void PushSilence(Voice voice, int frames)
        {
            int samples = frames * _format.Channels;
            if (_silence.Length < samples)
            {
                _silence = new float[samples];
            }

            Push(voice.Stream, _silence, firstFrame: 0, frameCount: frames);
            Push(voice.BentStream, _silence, firstFrame: 0, frameCount: frames);
        }

        private void ShapePick(Voice voice, BassStemClip clip, double brightness, double scrape)
        {
            int channels = _format.Channels;
            int lastFrame = clip.HasLoop ? clip.LoopEndFrame - clip.LoopFadeFrames : clip.FrameCount;
            int frames = Math.Min(_format.FramesFor(PICK_SHAPE_SECONDS), lastFrame);
            if (frames <= 0)
            {
                return;
            }

            if (voice.PickBuffer.Length < frames * channels)
            {
                voice.PickBuffer = new float[frames * channels];
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
                    voice.PickBuffer[index] = clip.Samples[index] + scraped;
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
                    voice.PickBuffer[index] += extra * fade * (sample - low);
                }
            }

            Push(voice.Stream, voice.PickBuffer, firstFrame: 0, frameCount: frames);
            voice.PushedFrames = frames;
        }

        private void Route(Voice voice, bool synth, bool distorted)
        {
            var bus = synth && _synthBus != null ? _synthBus : distorted ? _ampBus : _cleanBus;
            if (bus == null || voice.Bus == bus)
            {
                return;
            }

            voice.Bus = bus;
            bus.Take(voice.Stream.Handle);
            bus.Take(voice.BentStream.Handle);
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
                Decay(voice);
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

        private void Decay(Voice voice)
        {
            double held = (Stopwatch.GetTimestamp() - voice.StartTimestamp) / (double) Stopwatch.Frequency;
            double decaying = held - voice.DecayStartSeconds;
            if (decaying <= 0)
            {
                return;
            }

            double decibels = Math.Min(voice.DecayDbPerSecond * decaying, MAX_DECAY_DB);
            if (decibels - voice.DecayDb < DECAY_STEP_DB)
            {
                return;
            }

            voice.DecayDb = decibels;
            SlideVoiceVolume(voice, VoiceVolume(voice), DECAY_SLIDE_SECONDS);
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
            float freq = (float) (_format.Frequency * voice.PlaybackRate);
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

                semitones += (DriftCents(voice) + HumanCents(voice)) / 100;
                if (IsWhammyIdle)
                {
                    semitones += VibratoCents(voice) / 100;
                }
                freq = (float) (_format.Frequency * voice.PlaybackRate * Math.Pow(2, semitones / 12));
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

        private bool IsWhammyIdle => _whammyPercent <= WHAMMY_IDLE_PERCENT;

        private double BendProgress => GlobalAudioHandler.UseWhammyFx
            ? Math.Min(1, _whammyPercent / WHAMMY_FULL_BEND_PERCENT)
            : 0;

        private (double Plain, double Bent) Shares(Voice voice)
        {
            double bent = voice.BentClip != null && voice.WhammySemitones > 0 ? BendProgress : 0;
            return (1 - bent, bent);
        }

        private double VoiceVolume(Voice voice)
        {
            double level = voice.IsSynth ? 1 : _volume();
            return level * voice.Gain * Math.Pow(10, -voice.DecayDb / 20);
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

        private static double HumanCents(Voice voice)
        {
            double held = (Stopwatch.GetTimestamp() - voice.StartTimestamp) / (double) Stopwatch.Frequency;
            return voice.TuningCents + voice.AttackCents * Math.Exp(-held / ATTACK_SETTLE_SECONDS);
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
