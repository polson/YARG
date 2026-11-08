#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using YARG.Core.Audio;
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoundboard : IDisposable
    {
        private const double VOICE_FADE_SECONDS        = 0.08;
        private const double FREESTYLE_RELEASE_SECONDS = 0.12;

        private readonly object               _lock;
        private readonly BassStemClipRecorder _recorder;
        private readonly BassStemSeedLibrary  _seeds;
        private readonly BassStemVoicePool    _voices;
        private          bool                 _disposed;

        internal BassStemSoundboard(BassStemReplay replay, AudioManager manager,
            PitchShiftParametersStruct pitchParams, Func<double> volume)
        {
            _lock = replay.Lock;
            _recorder = new BassStemClipRecorder(replay, replay.Format);
            var renderer = new BassStemSeedRenderer(replay.Format, BassStretchRender.Create,
                BassStemReplayPolicy.ShouldStretch);
            _seeds = new BassStemSeedLibrary(replay.Lock, renderer);
            _voices = new BassStemVoicePool(manager, replay.Format, pitchParams, volume);
        }

        private double ReleaseFadeSeconds => _seeds.IsActive ? FREESTYLE_RELEASE_SECONDS : VOICE_FADE_SECONDS;

        public void Record(int key, double noteSeconds, double durationSeconds, bool isSustain)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _recorder.Record(key, noteSeconds, durationSeconds, isSustain);
            }
        }

        public void RequestSeeds(List<BassStemSeedSet> seedSets)
        {
            lock (_lock)
            {
                _seeds.Request(seedSets);
            }
        }

        public void SelectSeedSet(double soloTime, double songTime)
        {
            lock (_lock)
            {
                _seeds.Select(soloTime, songTime);
            }
        }

        public void ClearSeedSet()
        {
            lock (_lock)
            {
                _seeds.ClearSelection();
            }
        }

        public bool HasSeedSet(double soloTime)
        {
            lock (_lock)
            {
                return !_disposed && _seeds.HasSeeds(soloTime);
            }
        }

        public void Play(int key, int semitones, double gain)
        {
            lock (_lock)
            {
                if (_disposed || !TryGetPlayableClip(key, out var clip))
                {
                    return;
                }

                _voices.FadePreviousVoices();
                _voices.Start(clip, key: key, semitones: semitones, gain: gain, loopSustains: _seeds.IsActive);
            }
        }

        public void PlayStrum(int fretMask, int semitones, double gain)
        {
            lock (_lock)
            {
                if (_disposed || !TryGetPlayableClip(fretMask, out _))
                {
                    return;
                }

                _voices.FadePreviousVoices();
                double fretGain = gain / Math.Sqrt(BassStemFretMask.Count(fretMask));
                for (int fret = 1; fret < GuitarEngine.OPEN_MASK; fret <<= 1)
                {
                    if ((fretMask & fret) == 0)
                    {
                        continue;
                    }

                    if (!TryGetPlayableClip(fret, out var clip))
                    {
                        continue;
                    }

                    _voices.Start(clip, key: fret, semitones: semitones, gain: fretGain,
                        loopSustains: _seeds.IsActive);
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

                _voices.Release(fretMask, ReleaseFadeSeconds);
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

                _voices.SetHeldFrets(fretMask, FREESTYLE_RELEASE_SECONDS);
            }
        }

        public void SetWhammyPitch(float percent)
        {
            float shift = GlobalAudioHandler.UseWhammyFx
                ? (float) Math.Pow(2, -GlobalAudioHandler.WhammyPitchShiftAmount * percent / 12.0)
                : 1f;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _voices.SetWhammyPitch(shift);
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

                _voices.StopAll(ReleaseFadeSeconds);
            }
        }

        internal void Clear()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _recorder.Clear();
                _voices.Clear();
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

                _recorder.Pump();
                _voices.Pump();
            }
        }

        internal void PrepareSeedJobs(List<BassStemSampleJob> jobs)
        {
            lock (_lock)
            {
                _seeds.PrepareJobs(jobs);
            }
        }

        internal void PublishSeeds(CancellationToken token)
        {
            _seeds.Publish(token);
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
                _voices.Dispose();
            }
        }

        private bool TryGetPlayableClip(int key, out BassStemClip clip)
        {
            return _seeds.IsActive
                ? BassStemClipPolicy.TryGetSeedClip(_seeds, key, out clip)
                : BassStemClipPolicy.TryGetRecordedClip(_recorder, key, out clip);
        }
    }
}
