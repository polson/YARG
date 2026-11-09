#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemReplay : IDisposable
    {
        private const int PRERENDER_SHUTDOWN_TIMEOUT_MS = 1000;

        private readonly AudioManager               _manager;
        private readonly PitchShiftParametersStruct _pitchParams;
        private readonly Func<double>               _normalizationGain;
        private readonly List<BassStemSoundboard>   _soundboards = new();
        private          byte[]?                    _prerenderBytes;
        private          int[]                      _prerenderIndices = Array.Empty<int>();
        private          CancellationTokenSource?   _prerenderCts;
        private          Task                       _prerenderTask = Task.CompletedTask;

        internal BassStemFormat Format { get; }

        internal object Lock { get; } = new();

        public BassStemReplay(AudioManager manager, int stream, PitchShiftParametersStruct pitchParams, Func<double> normalizationGain)
        {
            Bass.ChannelGetInfo(stream, out ChannelInfo info);
            _manager = manager;
            _pitchParams = pitchParams;
            _normalizationGain = normalizationGain;
            Format = new BassStemFormat(frequency: info.Frequency, channels: info.Channels);
        }

        public BassStemSoundboard CreateSoundboard(Func<double> stemVolume)
        {
            var soundboard = new BassStemSoundboard(this, _manager, _pitchParams,
                () => stemVolume() * _normalizationGain());
            lock (Lock)
            {
                _soundboards.Add(soundboard);
            }

            return soundboard;
        }

        public void SetPrerenderSource(byte[] bytes, int[] indices)
        {
            _prerenderBytes = bytes;
            _prerenderIndices = indices;
        }

        public void StartPrerender()
        {
            var bytes = _prerenderBytes;
            if (bytes == null || _prerenderCts != null)
            {
                return;
            }

            var jobs = new List<(long StartFrame, int FrameCount, float[] Samples)>();
            var soundboards = new List<BassStemSoundboard>();
            lock (Lock)
            {
                foreach (var soundboard in _soundboards)
                {
                    int jobCount = jobs.Count;
                    soundboard.PrepareSeedJobs(jobs);
                    if (jobs.Count > jobCount)
                    {
                        soundboards.Add(soundboard);
                    }
                }
            }

            if (jobs.Count == 0)
            {
                YargLogger.LogWarning("No freestyle seeds were registered for stem pre-rendering.");
                return;
            }

            _prerenderBytes = null;
            var indices = _prerenderIndices;
            _prerenderCts = new CancellationTokenSource();
            var token = _prerenderCts.Token;
            _prerenderTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    if (!BassStemPrerender.Decode(bytes, indices, Format, jobs, token))
                    {
                        return;
                    }

                    foreach (var soundboard in soundboards)
                    {
                        soundboard.PublishSeeds(token);
                    }
                }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex, "Stem pre-render failed.");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void ResetAfterSeek()
        {
            lock (Lock)
            {
                foreach (var soundboard in _soundboards)
                {
                    soundboard.Clear();
                }
            }
        }

        public void Pump()
        {
            lock (Lock)
            {
                foreach (var soundboard in _soundboards)
                {
                    soundboard.Pump();
                }
            }
        }

        public void Dispose()
        {
            _prerenderBytes = null;
            if (_prerenderCts != null)
            {
                _prerenderCts.Cancel();
                if (!_prerenderTask.Wait(PRERENDER_SHUTDOWN_TIMEOUT_MS))
                {
                    YargLogger.LogError("Stem pre-render did not stop during audio teardown.");
                }

                _prerenderCts.Dispose();
                _prerenderCts = null;
            }

            BassStemSoundboard[] soundboards;
            lock (Lock)
            {
                soundboards = _soundboards.ToArray();
                _soundboards.Clear();
            }

            foreach (var soundboard in soundboards)
            {
                soundboard.Dispose();
            }
        }

        public static bool IsFreestyleStem(SongStem stem)
        {
            return AudioHelpers.PitchBendAllowedStems.Contains(stem) && FreestyleSolosRequested();
        }

        public static bool ShouldStretch()
        {
            return BassStretchRender.IsAvailable() &&
                SettingsManager.Settings.EffectsMode.Value == EffectsMode.Quality;
        }

        /// <summary>
        /// Whether freestyle solo audio is needed this session. A replay follows its recorded engine
        /// parameters, because the engine replays the solos the recording auto-played.
        /// </summary>
        private static bool FreestyleSolosRequested()
        {
            var state = GlobalVariables.State;
            bool replayRequests = (state.IsReplay || state.PlayingWithReplay) && state.ReplayFreestyleSolos;
            return replayRequests || SettingsManager.Settings.FreestyleSolos.Value;
        }
    }
}
