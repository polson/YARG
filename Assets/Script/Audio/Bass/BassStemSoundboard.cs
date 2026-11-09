#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoundboard : IDisposable
    {
        private readonly object              _lock;
        private readonly BassStemSeedLibrary _seeds;
        private readonly BassStemVoicePool   _voices;
        private          bool                _disposed;

        internal BassStemSoundboard(BassStemReplay replay, AudioManager manager,
            PitchShiftParametersStruct pitchParams, Func<double> volume)
        {
            _lock = replay.Lock;
            _seeds = new BassStemSeedLibrary(replay.Format);
            _voices = new BassStemVoicePool(manager, replay.Format, pitchParams, volume);
        }

        public void RequestSeeds(IReadOnlyList<SoloSection> phrases, List<GuitarNote> notes,
            Func<double, double> playbackTime)
        {
            _seeds.Request(phrases, notes, playbackTime);
        }

        public void SelectSeedSet(double soloTime, double songTime)
        {
            _seeds.Select(soloTime, songTime);
        }

        public bool HasSeedSet(double soloTime)
        {
            return _seeds.HasSeeds(soloTime);
        }

        public void Play(int key, int semitones, double gain)
        {
            lock (_lock)
            {
                if (_disposed || !_seeds.TryGetClip(key, out var clip))
                {
                    return;
                }

                _voices.FadePreviousVoices();
                _voices.Start(clip, key: key, semitones: semitones, gain: gain);
            }
        }

        public void PlayStrum(int fretMask, int semitones, double gain)
        {
            lock (_lock)
            {
                if (_disposed || !_seeds.TryGetClip(fretMask, out _))
                {
                    return;
                }

                _voices.FadePreviousVoices();
                double fretGain = gain / Math.Sqrt(FretCount(fretMask));
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

                    _voices.Start(clip, key: fret, semitones: semitones, gain: fretGain);
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
    }
}
