using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    ///     Calculates a normalization gain for songs by analyzing RMS levels.
    ///     Streams are cloned and mixed into a decode-only mixer for background analysis.
    ///     Gain is adjusted incrementally toward the target RMS using clamped relative updates,
    ///     ensuring smooth transitions rather than abrupt volume changes.
    /// </summary>
    public class BassNormalizer : IDisposable
    {
        // Target RMS to normalize to, typically results in around -14 LUFS
        private const float TARGET_RMS = 0.12f;

        // Low initial gain so it typically ramps up instead of ramps down
        private const float INITIAL_GAIN = 0.3f;

        // Maximum allowed gain to prevent excessive loudness
        private const float MAX_GAIN = 1.5f;

        // The length in ms of the sliding window for RMS calculation
        private const int WINDOW_MS = 100;

        //Maximum per-window gain update, but ensuring that we can still hit max gain in a 2 minute long song
        private const float MAX_GAIN_STEP  = (MAX_GAIN - INITIAL_GAIN) / (TWO_MINUTES_MS / WINDOW_MS);
        private const float TWO_MINUTES_MS = 2 * 60 * 1000f;

        private const    int                     GAIN_CALC_SHUTDOWN_TIMEOUT_MS = 1000;
        private readonly Action<float>           _applyGain;
        private readonly List<int>               _handles = new();
        private readonly List<Stream>            _streams = new();
        private          float                   _gain    = INITIAL_GAIN;
        private          CancellationTokenSource _gainCalcCts;
        private          Task                    _gainCalcTask = Task.CompletedTask;

        private int _mixer;

        public BassNormalizer(Action<float> applyGain)
        {
            _applyGain = applyGain;
        }

        public float Gain => Volatile.Read(ref _gain);

        public void Dispose()
        {
            // BASS calls cannot be interrupted mid-call. Do not free handles while the worker is still using them.
            if (!StopGainCalculation())
            {
                return;
            }

            // Free dependent split/source streams before the mixer they were added to.
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                int handle = _handles[i];
                if (!Bass.StreamFree(handle))
                {
                    if (Bass.LastError != Errors.Handle)
                    {
                        YargLogger.LogFormatError("Failed to free stream (THIS WILL LEAK MEMORY!): {0}!",
                            Bass.LastError);
                    }
                }
            }

            foreach (var stream in _streams)
            {
                stream.Dispose();
            }

            _mixer = 0;
            _streams.Clear();
            _handles.Clear();
        }

        /// <summary>
        ///     Adds a stream to the normalization mixer and restarts the background gain calculation.
        ///     Restarting updates with each added stream provides a head start on normalization before playback begins,
        ///     which is especially useful for modes like Practice where the mixer does not play immediately.
        /// </summary>
        public bool AddStream(Stream stream, params StemMixer.StemInfo[] stemInfos)
        {
            if (!StopGainCalculation())
            {
                YargLogger.LogError("Previous gain calculation did not stop; refusing to start another one.");
                return false;
            }

            if (!CloneStreamToMemory(stream, out var clonedStream))
            {
                YargLogger.LogError("Failed to clone stream!");
                return false;
            }

            try
            {
                if (_mixer == 0)
                {
                    _mixer = BassX.Mix.CreateMixer(44100, 2, BassFlags.Decode, GlobalAudioHandler.MAX_THREADS);
                    _handles.Add(_mixer);
                }

                int sourceStream = BassX.Stream.CreateSource(clonedStream);
                _handles.Add(sourceStream);

                foreach (var stemInfo in stemInfos)
                {
                    float[,] volumeMatrix = BassStemMixer.BuildVolumeMatrix(stemInfo);
                    if (volumeMatrix != null)
                    {
                        int[] channelMap = stemInfo.Indices.Append(-1).ToArray();
                        int streamSplit = BassX.Mix.CreateSplit(sourceStream, BassFlags.Decode, channelMap);
                        _handles.Add(streamSplit);

                        BassX.Mix.AddChannel(_mixer, streamSplit, BassFlags.MixerChanMatrix);
                        BassX.Mix.SetMatrix(streamSplit, volumeMatrix);
                    }
                    else
                    {
                        BassX.Mix.AddChannel(_mixer, sourceStream, BassFlags.Default);
                    }
                }

                StartGainCalculation();
                return true;
            }
            catch (BassX.BassOperationException exception)
            {
                YargLogger.LogError(exception.Message);
                return false;
            }
        }

        private bool CloneStreamToMemory(Stream original, out MemoryStream clonedStream)
        {
            clonedStream = null;
            if (!original.CanRead || !original.CanSeek)
            {
                return false;
            }

            long originalPosition = original.Position;
            try
            {
                original.Position = 0;
                clonedStream = new MemoryStream();
                original.CopyTo(clonedStream);
                clonedStream.Position = originalPosition;
                _streams.Add(clonedStream);
                return true;
            }
            catch
            {
                clonedStream?.Dispose();
                clonedStream = null;
                return false;
            }
            finally
            {
                original.Position = originalPosition;
            }
        }

        private void StartGainCalculation()
        {
            _gainCalcCts = new CancellationTokenSource();
            var token = _gainCalcCts.Token;

            _gainCalcTask = Task.Factory.StartNew(() => RunGainCalculation(token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void RunGainCalculation(CancellationToken token)
        {
            try
            {
                CalculateRms(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Expected shutdown.
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Gain calculation failed.");
            }
        }

        private bool StopGainCalculation()
        {
            if (_gainCalcCts == null)
            {
                return true;
            }

            _gainCalcCts.Cancel();

            if (!_gainCalcTask.Wait(GAIN_CALC_SHUTDOWN_TIMEOUT_MS))
            {
                YargLogger.LogError(
                    "Gain calculation did not stop during audio teardown; leaving its BASS handles intact.");
                return false;
            }

            _gainCalcCts.Dispose();
            _gainCalcCts = null;
            _gainCalcTask = Task.CompletedTask;
            return true;
        }

        private void CalculateRms(CancellationToken token)
        {
            double cumulativeSumSquares = 0.0;
            long totalSamples = 0;
            Bass.ChannelSetPosition(_mixer, 0);
            var info = Bass.ChannelGetInfo(_mixer);
            float windowSeconds = WINDOW_MS / 1000f;
            long samplesPerWindow = (long) (windowSeconds * info.Frequency);
            float[] level = new float[1];

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                bool didGetLevel = Bass.ChannelGetLevel(_mixer, level, windowSeconds,
                    LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS);

                if (!didGetLevel)
                {
                    break;
                }

                float chunkedRms = level[0];
                if (chunkedRms > 0)
                {
                    double sumSquares = chunkedRms * chunkedRms * samplesPerWindow;
                    cumulativeSumSquares += sumSquares;
                    totalSamples += samplesPerWindow;

                    double rms = Math.Sqrt(cumulativeSumSquares / totalSamples);
                    float targetGain = (float) Math.Min(MAX_GAIN, TARGET_RMS / rms);
                    float gain = Gain;
                    float delta = Math.Clamp(targetGain - gain, -MAX_GAIN_STEP, MAX_GAIN_STEP);
                    gain += delta;
                    Volatile.Write(ref _gain, gain);
                    _applyGain?.Invoke(gain);
                }
            }
        }
    }
}
