#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ManagedBass;
using YARG.Audio.BASS.Effects;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Owns the push-stream voices and synth bus that sound freestyle solo notes over the muted stem.
    /// </summary>
    internal sealed class BassStemSoundboard : IDisposable
    {
        private const int    VOICE_COUNT                         = 12;
        private const int    RELEASE_KEY                         = 1 << 8;
        private const double FREESTYLE_MIN_TRANSITION_SECONDS    = 0.015;
        private const double FREESTYLE_MAX_TRANSITION_SECONDS    = 0.03;
        private const double FREESTYLE_TRANSITION_INTERVAL_RATIO = 0.25;
        private const double LEGATO_TRANSITION_SECONDS           = 0.035;
        private const double LEGATO_FADE_IN_SECONDS              = 0.01;
        private const double LOOP_BUFFER_SECONDS                 = 0.25;
        private const double PICK_VELOCITY_DB                    = 3;
        private const double PICK_BRIGHTNESS_SPREAD              = 0.25;
        private const double FAST_NOTE_SECONDS                   = 0.15;
        private const double FAST_SOFTEN_DB                      = 1.5;
        private const double FAST_DULLING                        = 0.3;
        private const double STRUM_MIN_SPREAD_SECONDS            = 0.008;
        private const double STRUM_MAX_SPREAD_SECONDS            = 0.015;
        private const int    SYNTH_AMP_PRIORITY                  = 1;
        private const float  SYNTH_REVERB_DAMP                   = 0.5f;
        private const float  MAX_SYNTH_GAIN                      = 16f;

        private static readonly double[] CHORD_NOTE_GAINS = { 1, 0.8, 0.65 };

        private readonly object                _lock = new();
        private readonly BassStemFormat        _format;
        private readonly Func<double>          _volume;
        private readonly BassSoloPhraseLibrary _library;
        private          BassSoloPhrase?       _phrase;
        private readonly BassStemVoice[]       _voices = new BassStemVoice[VOICE_COUNT];
        private          int                   _heldFrets;
        private          float                 _whammyPercent;
        private readonly Random                _random = new();
        private readonly List<BassStemClip>    _chordClips = new();
        private          long                  _playCount;
        private          long                  _lastVoiceStart = Stopwatch.GetTimestamp();
        private          int                   _lastLayerTake;
        private          bool                  _disposed;
        private readonly BassMixerBus?         _synthBus;
        private readonly BassLeadAmpDsp?       _synthAmp;
        private          BassLeadAmpTone       _synthTone = BassLeadAmpTone.Default;
        private readonly BassFreeverbDsp?      _synthReverb;

        internal BassStemSoundboard(AudioManager manager, BassStemFormat format, Func<double> volume,
            BassChordTrack? backingChords)
        {
            _format = format;
            _volume = volume;
            BassCabinetSet? cabinets = null;
            if (manager is BassAudioManager bassManager)
            {
                _synthBus = bassManager.CreateMixerBus(format.Frequency, format.Channels);
                if (_synthBus != null)
                {
                    cabinets = BassCabinetSet.Load();
                    _synthAmp = BassLeadAmpDsp.Attach(_synthBus.SourceHandle, 0, cabinets, SYNTH_AMP_PRIORITY);
                    if (_synthAmp != null)
                    {
                        _synthReverb = BassFreeverbDsp.Create(_synthBus.SourceHandle, 1,
                            BassLeadAmpTone.DEFAULT_REVERB_WET, BassLeadAmpTone.DEFAULT_REVERB_ROOM, SYNTH_REVERB_DAMP);
                    }
                    else
                    {
                        _synthBus.Dispose();
                        _synthBus = null;
                    }
                }
            }

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                _voices[index] = new BassStemVoice(CreateVoiceStream(manager), format, _random);
            }

            _library = new BassSoloPhraseLibrary(format, backingChords, cabinets);
        }

        private PushStream CreateVoiceStream(AudioManager manager)
        {
            if (_synthBus == null)
            {
                return manager.CreatePushStream(sampleRate: _format.Frequency, channelCount: _format.Channels);
            }

            var stream = BassPushStream.Create(_format.Frequency, _format.Channels)
                ?? throw new InvalidOperationException($"Failed to create BASS push stream: {Bass.LastError}");
            _synthBus.Take(stream.Handle);
            return stream;
        }

        private int LoopBufferFrames => _format.FramesFor(LOOP_BUFFER_SECONDS);

        public void RequestPhrases(List<GuitarNote> notes, Func<double, double> playbackTime)
        {
            _library.RequestPhrases(notes, playbackTime);
        }

        public bool TrySelectPhrase(double soloTime, double songTime)
        {
            var phrase = _library.SelectPhrase(soloTime, songTime);
            lock (_lock)
            {
                _phrase = phrase;
            }

            return phrase is { IsEmpty: false };
        }

        public void PlayNote(int fretMask)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_phrase!.TryPickClip(fretMask, out var clip))
                {
                    return;
                }

                var pick = NextPick();
                FadePreviousVoices(FREESTYLE_MIN_TRANSITION_SECONDS);
                var layered = Layered(clip, pick.Force);
                Start(layered, key: fretMask, gain: pick.Gain, firstFrame: 0, whammySemitones: WhammySemitones(layered),
                    brightness: pick.Brightness);
            }
        }

        public void PlayLegato(int fretMask)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_phrase!.TryPickClip(fretMask, out var clip))
                {
                    return;
                }

                FadePreviousVoices(LEGATO_TRANSITION_SECONDS);
                int lastFrame = (clip.HasLoop ? clip.LoopEndFrame : clip.FrameCount) - 1;
                Start(clip, key: fretMask, gain: 1, firstFrame: Math.Min(clip.AttackEndFrame, lastFrame),
                    whammySemitones: WhammySemitones(clip), fadeInSeconds: LEGATO_FADE_IN_SECONDS);
            }
        }

        public void PlayStrum(int fretMask)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_phrase!.IsEmpty)
                {
                    return;
                }

                var pick = NextPick();
                FadePreviousVoices(FREESTYLE_MIN_TRANSITION_SECONDS);
                if (BassSoloPhrase.FretCount(fretMask) > 1 && _phrase!.TryGetChordClips(fretMask, _chordClips))
                {
                    StrumChord(fretMask, pick.Gain, pick.Brightness);
                    return;
                }

                double fretGain = pick.Gain / Math.Sqrt(BassSoloPhrase.FretCount(fretMask));
                for (int fret = 1; fret < GuitarEngine.OPEN_MASK; fret <<= 1)
                {
                    if ((fretMask & fret) == 0)
                    {
                        continue;
                    }

                    if (!_phrase!.TryPickClip(fret, out var clip))
                    {
                        continue;
                    }

                    var layered = Layered(clip, pick.Force);
                    Start(layered, key: fret, gain: fretGain, firstFrame: 0, whammySemitones: WhammySemitones(layered),
                        brightness: pick.Brightness);
                }
            }
        }

        private void StrumChord(int fretMask, double gain, double brightness)
        {
            int whammySemitones = WhammySemitones(_chordClips[0]);
            if (_chordClips.Count == 1)
            {
                Start(_chordClips[0], key: fretMask, gain: gain, firstFrame: 0, whammySemitones: whammySemitones,
                    brightness: brightness);
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
            for (int index = 0; index < _chordClips.Count; index++)
            {
                while ((fretMask & fret) == 0)
                {
                    fret <<= 1;
                }

                Start(_chordClips[index], key: fret, gain: gain * CHORD_NOTE_GAINS[index] / Math.Sqrt(power),
                    firstFrame: 0, whammySemitones: whammySemitones, brightness: brightness,
                    delayFrames: _format.FramesFor(spread * index));
                fret <<= 1;
            }
        }

        public void ReleaseFrets(int fretMask)
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
                        Release(voice);
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
                        Release(voice);
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
                        voice.SetPitch(glide: true, whammyPercent: percent);
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
                        voice.FadeOut();
                    }
                }
            }
        }

        internal void PreparePhraseJobs(List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            if (_synthBus == null)
            {
                return;
            }

            _library.PrepareJobs(jobs);
        }

        internal void PublishPhrases(CancellationToken token)
        {
            _library.Publish(token);
        }

        internal void ResetVoices()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    voice.Reset();
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
                    voice.Feed(LoopBufferFrames, _whammyPercent);
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
                _library.Clear();
                foreach (var voice in _voices)
                {
                    voice.Dispose();
                }

                _synthReverb?.Dispose();
                _synthAmp?.Dispose();
                _synthBus?.Dispose();
            }
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
            return layers[layer].TakeAfter(_lastLayerTake, _random, out _lastLayerTake);
        }

        private void Release(BassStemVoice voice)
        {
            var releases = voice.Clip?.Releases;
            if (releases is { Length: > 0 })
            {
                var release = releases[_random.Next(releases.Length)];
                Start(release, key: RELEASE_KEY, gain: voice.Gain, firstFrame: 0,
                    whammySemitones: WhammySemitones(release));
            }

            voice.FadeOut();
        }

        private int WhammySemitones(BassStemClip clip)
        {
            return clip.Note >= 0 && _phrase != null ? _phrase.WhammySemitones(clip) : 0;
        }

        private void Start(BassStemClip clip, int key, double gain, int firstFrame, int whammySemitones,
            double fadeInSeconds = 0, double brightness = 1, int delayFrames = 0)
        {
            var voice = PickVoice(key);
            ApplyPhraseTone();
            voice.Begin(clip, key, gain, firstFrame, fadeInSeconds, brightness, delayFrames, whammySemitones, _heldFrets,
                _whammyPercent);
            voice.StartOrder = ++_playCount;
            voice.Feed(LoopBufferFrames, _whammyPercent);
        }

        private void ApplyPhraseTone()
        {
            var tone = _phrase?.Tone ?? BassLeadAmpTone.Default;
            if (tone != _synthTone && _synthAmp?.SetTone(tone) == true)
            {
                _synthTone = tone;
                _synthReverb?.SetParams(1, tone.ReverbWet, tone.ReverbRoom, SYNTH_REVERB_DAMP, 1);
            }

            double gain = _volume() * (_phrase?.SynthGain ?? 0);
            _synthAmp?.SetGain((float) Math.Clamp(gain, 0, MAX_SYNTH_GAIN));
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
                if (previous.IsAttached || previous.IsFading())
                {
                    previous.FadeOut(fadeSeconds: transition);
                }
            }
        }

        private BassStemVoice PickVoice(int key)
        {
            BassStemVoice? oldestIdle = null;
            BassStemVoice? quietestTail = null;
            float quietestVolume = float.MaxValue;
            var oldest = _voices[0];
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

                if (!voice.IsFading())
                {
                    if (oldestIdle == null || voice.StartOrder < oldestIdle.StartOrder)
                    {
                        oldestIdle = voice;
                    }

                    continue;
                }

                float volume = voice.Loudness();
                if (volume < quietestVolume)
                {
                    quietestTail = voice;
                    quietestVolume = volume;
                }
            }

            return oldestIdle ?? quietestTail ?? oldest;
        }
    }
}
