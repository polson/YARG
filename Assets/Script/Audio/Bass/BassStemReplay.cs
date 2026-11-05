#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemReplay : IDisposable
    {
        private const double RECENT_SECONDS       = 3.0;
        private const int    CAPTURE_DSP_PRIORITY = 1;
        private const int    FLOAT_BYTES          = sizeof(float);
        private const int    PRERENDER_SHUTDOWN_TIMEOUT_MS = 1000;

        private readonly int                        _stream;
        private readonly int                        _captureDsp;
        private readonly DSPProcedure               _captureProc;
        private readonly float[]                    _ring;
        private readonly int                        _ringFrames;
        private readonly AudioManager               _manager;
        private readonly PitchShiftParametersStruct _pitchParams;
        private readonly Func<double>               _normalizationGain;
        private readonly List<BassStemSoundboard>   _soundboards = new();
        private          long                       _capturedFrames;
        private          double                     _originSeconds;
        private          byte[]?                    _prerenderBytes;
        private          int[]                      _prerenderIndices = Array.Empty<int>();
        private          CancellationTokenSource?   _prerenderCts;
        private          Task                       _prerenderTask = Task.CompletedTask;

        internal object Lock { get; } = new();
        internal int Frequency { get; }
        internal int ChannelCount { get; }

        public BassStemReplay(AudioManager manager, int stream, PitchShiftParametersStruct pitchParams, Func<double> normalizationGain)
        {
            Bass.ChannelGetInfo(stream, out ChannelInfo info);
            _manager = manager;
            _stream = stream;
            _pitchParams = pitchParams;
            _normalizationGain = normalizationGain;
            Frequency = info.Frequency;
            ChannelCount = info.Channels;
            _ringFrames = (int) (RECENT_SECONDS * Frequency);
            _ring = new float[_ringFrames * ChannelCount];

            _captureProc = OnCapture;
            _captureDsp = Bass.ChannelSetDSP(stream, _captureProc, IntPtr.Zero, CAPTURE_DSP_PRIORITY);
            if (_captureDsp == 0)
            {
                YargLogger.LogFormatError("Failed to capture stem audio for replay: {0}!", Bass.LastError);
            }
        }

        public BassStemSoundboard CreateSoundboard(Func<double> stemVolume)
        {
            var soundboard = new BassStemSoundboard(this, _manager, _pitchParams, () => stemVolume() * _normalizationGain());
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
            _prerenderBytes = null;
            if (bytes == null || _prerenderCts != null)
            {
                return;
            }

            var jobs = new List<BassStemPrerender.Job>();
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
                return;
            }

            var indices = _prerenderIndices;
            _prerenderCts = new CancellationTokenSource();
            var token = _prerenderCts.Token;
            _prerenderTask = Task.Factory.StartNew(() =>
            {
                try
                {
                    if (!BassStemPrerender.Decode(bytes, indices, Frequency, ChannelCount, jobs, token))
                    {
                        return;
                    }

                    foreach (var soundboard in soundboards)
                    {
                        soundboard.PublishSeeds();
                    }
                }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex, "Stem pre-render failed.");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void ResetAfterSeek(double seconds)
        {
            lock (Lock)
            {
                _capturedFrames = 0;
                _originSeconds = seconds;
                foreach (var soundboard in _soundboards)
                {
                    soundboard.Clear();
                }
            }
        }

        internal long CapturedFrames => _capturedFrames;

        internal long OldestFrame => Math.Max(0, _capturedFrames - _ringFrames);

        internal long SecondsToFrame(double seconds)
        {
            return (long) Math.Round((seconds - _originSeconds) * Frequency);
        }

        internal void CopyFrames(long firstFrame, int frameCount, float[] destination, int destinationFrame)
        {
            int ringFrame = (int) (firstFrame % _ringFrames);
            int firstCount = Math.Min(frameCount, _ringFrames - ringFrame);
            Array.Copy(_ring, ringFrame * ChannelCount, destination, destinationFrame * ChannelCount, firstCount * ChannelCount);
            Array.Copy(_ring, 0, destination, (destinationFrame + firstCount) * ChannelCount, (frameCount - firstCount) * ChannelCount);
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

            if (!Bass.ChannelRemoveDSP(_stream, _captureDsp) && Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to remove stem capture: {0}!", Bass.LastError);
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

        private void OnCapture(int _, int __, IntPtr buffer, int length, IntPtr ___)
        {
            int sampleCount = length / FLOAT_BYTES;
            lock (Lock)
            {
                int ringStart = (int) (_capturedFrames % _ringFrames) * ChannelCount;
                int firstCount = Math.Min(sampleCount, _ring.Length - ringStart);
                Marshal.Copy(buffer, _ring, ringStart, firstCount);
                Marshal.Copy(IntPtr.Add(buffer, firstCount * FLOAT_BYTES), _ring, 0, sampleCount - firstCount);
                _capturedFrames += sampleCount / ChannelCount;

                foreach (var soundboard in _soundboards)
                {
                    soundboard.OnCaptured();
                }
            }
        }
    }
}
