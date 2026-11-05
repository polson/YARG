#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Audio.PitchDetection;
using YARG.Core.Audio;
using YARG.Core.Engine.Guitar;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassStemSoundboard : IDisposable
    {
        public readonly struct SeedNote
        {
            public readonly int    Key;
            public readonly double SongTime;
            public readonly double AudioSeconds;
            public readonly double DurationSeconds;
            public readonly double SustainStartSeconds;
            public readonly bool   IsSustain;

            public SeedNote(int key, double songTime, double audioSeconds, double durationSeconds,
                double sustainStartSeconds, bool isSustain)
            {
                Key = key;
                SongTime = songTime;
                AudioSeconds = audioSeconds;
                DurationSeconds = durationSeconds;
                SustainStartSeconds = sustainStartSeconds;
                IsSustain = isSustain;
            }
        }

        public sealed class SeedSet
        {
            public readonly double         StartTime;
            public readonly double         EndTime;
            public readonly List<SeedNote> Notes;
            public readonly int            FretCount;

            public SeedSet(double startTime, double endTime, List<SeedNote> notes, int fretCount)
            {
                StartTime = startTime;
                EndTime = endTime;
                Notes = notes;
                FretCount = fretCount;
            }
        }

        private const double MAX_CLIP_SECONDS = 3.0;
        private const double FADE_SECONDS     = 0.08;
        private const double FREESTYLE_RELEASE_SECONDS = 0.12;
        private const double FREESTYLE_ATTACK_SECONDS = 0.004;
        private const double FREESTYLE_MIN_TRANSITION_SECONDS = 0.015;
        private const double FREESTYLE_MAX_TRANSITION_SECONDS = 0.03;
        private const double FREESTYLE_TRANSITION_INTERVAL_RATIO = 0.25;
        private const double LOOP_FADE_SECONDS = 0.02;
        private const double FALLBACK_LOOP_SECONDS = 0.15;
        private const double LOOP_BUFFER_SECONDS = 0.25;
        private const double PITCH_ANALYSIS_SECONDS = 0.3;
        private const double PITCH_BLOCK_SECONDS = 0.02;
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
            public float[] LoopTail = Array.Empty<float>();
            public int     LoopStartFrame;
            public int     LoopEndFrame;
            public float   Pitch;
        }

        private readonly struct SeedClip
        {
            public readonly double SongTime;
            public readonly Clip   Clip;
            public readonly int    SustainStartFrame;

            public SeedClip(double songTime, Clip clip, int sustainStartFrame)
            {
                SongTime = songTime;
                Clip = clip;
                SustainStartFrame = sustainStartFrame;
            }
        }

        private sealed class SeedClips
        {
            public readonly double         StartTime;
            public readonly double         EndTime;
            public readonly int            FretCount;
            public readonly Dictionary<int, List<SeedClip>> Clips = new();
            public readonly List<Clip> SelectedClips = new();

            public SeedClips(double startTime, double endTime, int fretCount)
            {
                StartTime = startTime;
                EndTime = endTime;
                FretCount = fretCount;
            }
        }

        private sealed class Voice
        {
            public PushStream Stream = null!;
            public int        PitchFx;
            public float      Pitch;
            public Clip?      Clip;
            public int        Generation;
            public int        PushedFrames;
            public int        Key;
            public bool       IsSustain;
            public bool       IsLooping;
            public long       StartOrder;
        }

        private readonly BassStemReplay             _replay;
        private readonly PitchShiftParametersStruct _pitchParams;
        private readonly Func<double>               _volume;
        private readonly Random                     _random = new();
        private readonly Dictionary<int, Clip>      _clips  = new();
        private readonly Voice[]                    _voices = new Voice[VOICE_COUNT];
        private          List<SeedSet>              _seedRequests  = new();
        private          List<SeedClips>            _pendingSeeds  = new();
        private          List<SeedClips>            _seeds         = new();
        private          SeedClips?                 _activeSeeds;
        private          bool                       _useSoloSeeds;
        private readonly int                        _maxClipFrames;
        private readonly int                        _fadeFrames;
        private readonly int                        _attackFrames;
        private readonly int                        _loopFadeFrames;
        private readonly int                        _loopBufferFrames;
        private          int                        _heldFrets;
        private          float                      _whammyPitch = 1f;
        private          long                       _playCount;
        private          long                       _lastFreestyleStrum = Stopwatch.GetTimestamp();
        private          bool                       _disposed;

        internal BassStemSoundboard(BassStemReplay replay, AudioManager manager, PitchShiftParametersStruct pitchParams, Func<double> volume)
        {
            _replay = replay;
            _pitchParams = pitchParams;
            _volume = volume;
            _maxClipFrames = (int) Math.Round(MAX_CLIP_SECONDS * replay.Frequency);
            _fadeFrames = (int) Math.Round(FADE_SECONDS * replay.Frequency);
            _attackFrames = (int) Math.Round(FREESTYLE_ATTACK_SECONDS * replay.Frequency);
            _loopFadeFrames = (int) Math.Round(LOOP_FADE_SECONDS * replay.Frequency);
            _loopBufferFrames = (int) Math.Round(LOOP_BUFFER_SECONDS * replay.Frequency);

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
                return !_disposed && TryGetClip(key, out _);
            }
        }

        public void RequestSeeds(List<SeedSet> seedSets)
        {
            lock (_replay.Lock)
            {
                _seedRequests = seedSets;
            }
        }

        public void SelectSeedSet(double soloTime, double songTime)
        {
            lock (_replay.Lock)
            {
                _activeSeeds = null;
                _useSoloSeeds = !double.IsNaN(soloTime);
                foreach (var seeds in _seeds)
                {
                    if (soloTime >= seeds.StartTime && soloTime <= seeds.EndTime)
                    {
                        seeds.SelectedClips.Clear();
                        foreach (var clips in seeds.Clips.Values)
                        {
                            seeds.SelectedClips.Add(GetSeedClip(clips, songTime));
                        }
                        seeds.SelectedClips.Sort((left, right) => left.Pitch.CompareTo(right.Pitch));
                        _activeSeeds = seeds;
                        break;
                    }
                }
            }
        }

        internal void PrepareSeedJobs(List<BassStemPrerender.Job> jobs)
        {
            _pendingSeeds = new List<SeedClips>();
            foreach (var seedSet in _seedRequests)
            {
                var seeds = new SeedClips(seedSet.StartTime, seedSet.EndTime, seedSet.FretCount);
                foreach (var note in seedSet.Notes)
                {
                    long startFrame = (long) Math.Round(note.AudioSeconds * _replay.Frequency);
                    int frameCount = (int) Math.Min(Math.Round(note.DurationSeconds * _replay.Frequency), _maxClipFrames);
                    if (startFrame < 0 || frameCount <= 0)
                    {
                        continue;
                    }

                    var clip = new Clip
                    {
                        Samples = new float[frameCount * _replay.ChannelCount],
                        FrameCount = frameCount,
                        IsSustain = note.IsSustain,
                    };
                    int sustainStart = (int) Math.Round(note.SustainStartSeconds * _replay.Frequency);
                    sustainStart = Math.Min(sustainStart, frameCount - 1);
                    if (!seeds.Clips.TryGetValue(note.Key, out var clips))
                    {
                        clips = new List<SeedClip>();
                        seeds.Clips.Add(note.Key, clips);
                    }
                    clips.Add(new SeedClip(note.SongTime, clip, sustainStart));
                    jobs.Add(new BassStemPrerender.Job(startFrame, frameCount, clip.Samples));
                }

                foreach (var clips in seeds.Clips.Values)
                {
                    clips.Sort((left, right) => left.SongTime.CompareTo(right.SongTime));
                }
                seeds.SelectedClips.Capacity = seeds.Clips.Count;
                _pendingSeeds.Add(seeds);
            }

            _seedRequests = new List<SeedSet>();
        }

        internal void PublishSeeds()
        {
            foreach (var seeds in _pendingSeeds)
            {
                foreach (var clips in seeds.Clips.Values)
                {
                    foreach (var seed in clips)
                    {
                        seed.Clip.FilledFrames = seed.Clip.FrameCount;
                        PrepareLoop(seed.Clip, seed.SustainStartFrame);
                        seed.Clip.Pitch = EstimatePitch(seed.Clip, seed.SustainStartFrame);
                        ApplyStartFade(seed.Clip);
                    }
                }
            }

            lock (_replay.Lock)
            {
                _seeds = _pendingSeeds;
                _pendingSeeds = new List<SeedClips>();
                _activeSeeds = null;
            }
        }

        public bool Play(int key, int semitones, double gain)
        {
            lock (_replay.Lock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (!TryGetClip(key, out var clip) && !TryGetFallbackClip(key, out clip))
                {
                    return false;
                }

                if (_useSoloSeeds)
                {
                    long now = Stopwatch.GetTimestamp();
                    double interval = (now - _lastFreestyleStrum) / (double) Stopwatch.Frequency;
                    double transition = interval * FREESTYLE_TRANSITION_INTERVAL_RATIO;
                    transition = Math.Clamp(transition, FREESTYLE_MIN_TRANSITION_SECONDS, FREESTYLE_MAX_TRANSITION_SECONDS);
                    _lastFreestyleStrum = now;
                    foreach (var previous in _voices)
                    {
                        if (previous.Key != 0 || Bass.ChannelIsSliding(previous.Stream.Handle, ChannelAttribute.Volume))
                        {
                            FadeOut(previous, fadeSeconds: transition);
                        }
                    }
                }

                var voice = PickVoice(key);
                voice.Stream.Clear();

                voice.Stream.SetVolume(_volume() * gain);
                voice.Clip = clip;
                voice.Generation = clip.Generation;
                voice.PushedFrames = 0;
                voice.Key = key;
                bool hasLoop = clip.LoopEndFrame > clip.LoopStartFrame;
                voice.IsLooping = _activeSeeds != null && hasLoop && (key & _heldFrets) == key;
                voice.IsSustain = clip.IsSustain || voice.IsLooping;
                voice.Pitch = (float) Math.Pow(2, semitones / 12.0);
                SetPitch(voice);
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
                    if (voice.IsSustain && !voice.IsLooping && (voice.Key & fretMask) != 0)
                    {
                        FadeOut(voice, fadeSeconds: _useSoloSeeds ? FREESTYLE_RELEASE_SECONDS : FADE_SECONDS);
                    }
                }
            }
        }

        public void SetHeldFrets(int fretMask)
        {
            lock (_replay.Lock)
            {
                if (_disposed)
                {
                    return;
                }

                _heldFrets = fretMask;
                foreach (var voice in _voices)
                {
                    if (!voice.IsLooping)
                    {
                        continue;
                    }

                    bool released = voice.Key == GuitarEngine.OPEN_MASK
                        ? fretMask == 0
                        : (voice.Key & fretMask) != voice.Key;
                    if (released)
                    {
                        FadeOut(voice, fadeSeconds: FREESTYLE_RELEASE_SECONDS);
                    }
                }
            }
        }

        public void SetWhammyPitch(float percent)
        {
            float shift = GlobalAudioHandler.UseWhammyFx
                ? (float) Math.Pow(2, -GlobalAudioHandler.WhammyPitchShiftAmount * percent / 12.0)
                : 1f;
            lock (_replay.Lock)
            {
                if (_disposed || _whammyPitch == shift)
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
                        FadeOut(voice, fadeSeconds: _useSoloSeeds ? FREESTYLE_RELEASE_SECONDS : FADE_SECONDS);
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
                voice.IsLooping = false;
            }

            _heldFrets = 0;
            _whammyPitch = 1f;
            _lastFreestyleStrum = Stopwatch.GetTimestamp();
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

        private bool TryGetClip(int key, out Clip clip)
        {
            if (_useSoloSeeds)
            {
                clip = null!;
                if (_activeSeeds == null || _activeSeeds.SelectedClips.Count == 0)
                {
                    return false;
                }

                var clips = _activeSeeds.SelectedClips;
                if (key == GuitarEngine.OPEN_MASK)
                {
                    clip = clips[_random.Next(clips.Count)];
                    return true;
                }

                int fret = 0;
                for (int mask = key; mask > 1; mask >>= 1)
                {
                    fret++;
                }

                int index = fret * (clips.Count - 1) / (_activeSeeds.FretCount - 1);
                clip = clips[index];
                return true;
            }

            return _clips.TryGetValue(key, out clip) && clip.FrameCount > 0;
        }

        private bool TryGetFallbackClip(int key, out Clip clip)
        {
            clip = null!;
            if (_useSoloSeeds || (key & (key - 1)) == 0)
            {
                return false;
            }

            for (int fretMask = 1; fretMask <= key; fretMask <<= 1)
            {
                if ((key & fretMask) != 0 && TryGetClip(fretMask, out clip))
                {
                    return true;
                }
            }

            foreach (var recorded in _clips.Values)
            {
                if (recorded.FrameCount > 0)
                {
                    clip = recorded;
                    return true;
                }
            }

            return false;
        }

        private static Clip GetSeedClip(List<SeedClip> seeds, double songTime)
        {
            foreach (var seed in seeds)
            {
                if (seed.SongTime >= songTime)
                {
                    return seed.Clip;
                }
            }

            return seeds[seeds.Count - 1].Clip;
        }

        private float EstimatePitch(Clip clip, int sustainStartFrame)
        {
            int channels = _replay.ChannelCount;
            int frequency = _replay.Frequency;
            int analysisFrames = (int) Math.Round(PITCH_ANALYSIS_SECONDS * frequency);
            int firstFrame = Math.Max(sustainStartFrame, clip.LoopEndFrame - analysisFrames);
            int frameCount = clip.LoopEndFrame - firstFrame;
            int loudestChannel = 0;
            double highestEnergy = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                double energy = 0;
                for (int frame = firstFrame; frame < clip.LoopEndFrame; frame++)
                {
                    double sample = clip.Samples[frame * channels + channel];
                    energy += sample * sample;
                }
                if (energy > highestEnergy)
                {
                    highestEnergy = energy;
                    loudestChannel = channel;
                }
            }

            var samples = new float[frameCount];
            double sum = 0;
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sample = clip.Samples[(firstFrame + frame) * channels + loudestChannel];
                samples[frame] = sample;
                sum += sample;
            }

            var tracker = new PitchTracker(frequency);
            tracker.Reset();
            var pitches = new List<float>();
            int blockFrames = (int) Math.Round(PITCH_BLOCK_SECONDS * frequency);
            for (int frame = 0; frame < frameCount; frame += blockFrames)
            {
                int frames = Math.Min(blockFrames, frameCount - frame);
                var pitch = tracker.ProcessBuffer(samples.AsSpan(frame, frames));
                if (pitch.HasValue)
                {
                    pitches.Add(pitch.Value);
                }
            }

            if (pitches.Count > 0)
            {
                pitches.Sort();
                return pitches[pitches.Count / 2];
            }

            double mean = sum / frameCount;
            int crossings = 0;
            for (int frame = 1; frame < frameCount; frame++)
            {
                if (samples[frame - 1] <= mean && samples[frame] > mean)
                {
                    crossings++;
                }
            }
            return (float) crossings * frequency / frameCount;
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

                if (voice.Key != 0)
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

        private void ApplyStartFade(Clip clip)
        {
            int fadeFrames = Math.Min(_attackFrames, clip.FrameCount);
            int channels = _replay.ChannelCount;
            for (int frame = 0; frame < fadeFrames; frame++)
            {
                float gain = (float) frame / fadeFrames;
                for (int channel = 0; channel < channels; channel++)
                {
                    clip.Samples[frame * channels + channel] *= gain;
                }
            }
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

        private void PrepareLoop(Clip clip, int sustainStartFrame)
        {
            int channels = _replay.ChannelCount;
            int fadeFrames = Math.Min(_loopFadeFrames, clip.FrameCount / 4);
            if (!BassSustainLoop.TryFind(clip.Samples, frequency: _replay.Frequency, channels: channels,
                firstFrame: sustainStartFrame, fadeFrames: fadeFrames, out int bodyStart, out int loopEnd))
            {
                YargLogger.LogFormatInfo("[LoopDebug] fallback: clip {0} ms, sustain start {1} ms, fill {2} ms",
                    clip.FrameCount * 1000.0 / _replay.Frequency, sustainStartFrame * 1000.0 / _replay.Frequency,
                    clip.FilledFrames * 1000.0 / _replay.Frequency);
                int tailFrames = (int) Math.Round(FALLBACK_LOOP_SECONDS * _replay.Frequency);
                bodyStart = Math.Max(sustainStartFrame, clip.FrameCount / 2);
                bodyStart = Math.Max(bodyStart, clip.FrameCount - tailFrames);
                bodyStart = Math.Min(bodyStart, clip.FrameCount - fadeFrames * 2);
                loopEnd = clip.FrameCount;
            }

            clip.LoopStartFrame = bodyStart + fadeFrames;
            clip.LoopEndFrame = loopEnd;
            clip.LoopTail = new float[fadeFrames * channels];
            for (int frame = 0; frame < fadeFrames; frame++)
            {
                float blend = (float) (frame + 1) / fadeFrames;
                for (int channel = 0; channel < channels; channel++)
                {
                    float tail = clip.Samples[(loopEnd - fadeFrames + frame) * channels + channel];
                    float head = clip.Samples[(bodyStart + frame) * channels + channel];
                    clip.LoopTail[frame * channels + channel] = tail * (1 - blend) + head * blend;
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

            if (voice.IsLooping)
            {
                FeedLoop(voice, clip);
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

        private void FeedLoop(Voice voice, Clip clip)
        {
            int channels = _replay.ChannelCount;
            int queuedBytes = Bass.StreamPutData(voice.Stream.Handle, IntPtr.Zero, 0);
            if (queuedBytes < 0)
            {
                YargLogger.LogFormatError("Failed to query replay queue: {0}!", Bass.LastError);
                return;
            }

            int remaining = _loopBufferFrames - queuedBytes / (channels * FLOAT_BYTES);
            while (remaining > 0)
            {
                int endFrame = clip.LoopEndFrame;
                var tail = clip.LoopTail;
                int fadeStart = endFrame - tail.Length / channels;
                int frames;
                if (voice.PushedFrames < fadeStart)
                {
                    frames = Math.Min(remaining, fadeStart - voice.PushedFrames);
                    Push(voice.Stream, clip.Samples, voice.PushedFrames, frames);
                }
                else
                {
                    frames = Math.Min(remaining, endFrame - voice.PushedFrames);
                    Push(voice.Stream, tail, voice.PushedFrames - fadeStart, frames);
                }

                remaining -= frames;
                voice.PushedFrames += frames;
                if (voice.PushedFrames == endFrame)
                {
                    voice.PushedFrames = clip.LoopStartFrame;
                }
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
