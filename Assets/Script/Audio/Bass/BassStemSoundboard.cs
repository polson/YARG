#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using YARG.Core.Audio;
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoundboard : IFreestyleSoloAudio, IDisposable
    {
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
            _seeds = new BassStemSeedLibrary(renderer);
            _voices = new BassStemVoicePool(manager, replay.Format, pitchParams, volume);
        }

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
            _seeds.Request(seedSets);
        }

        public void SelectSeedSet(double soloTime, double songTime)
        {
            _seeds.Select(soloTime, songTime);
        }

        public void DeselectSeeds()
        {
            _seeds.ClearSelection();
        }

        public bool HasSeedSet(double soloTime)
        {
            return _seeds.HasSeeds(soloTime);
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
                _voices.Start(clip, key: key, semitones: semitones, gain: gain, loopSustains: _seeds.IsSelected);
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
                        loopSustains: _seeds.IsSelected);
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

                _voices.Release(fretMask);
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

                _voices.SetHeldFrets(fretMask);
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

                _voices.StopAll();
            }
        }

        internal void PrepareSeedJobs(List<BassStemSampleJob> jobs)
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
                _voices.Dispose();
            }
        }

        private bool TryGetPlayableClip(int key, out BassStemClip clip)
        {
            return _seeds.IsSelected
                ? BassStemClipPolicy.TryGetSeedClip(_seeds, key, out clip)
                : BassStemClipPolicy.TryGetRecordedClip(_recorder, key, out clip);
        }
    }
}
