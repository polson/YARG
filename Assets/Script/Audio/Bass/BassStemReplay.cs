#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemReplay : IDisposable
    {
        private const int PRERENDER_SHUTDOWN_TIMEOUT_MS = 1000;
        private const int CHUNK_FRAMES                 = 16384;
        private const int FLOAT_BYTES                  = sizeof(float);

        private readonly AudioManager               _manager;
        private readonly Func<double>               _normalizationGain;
        private readonly List<BassStemSoundboard>   _soundboards = new();
        private readonly List<BassChordTrack>       _chordTracks = new();
        private readonly object                     _lock = new();
        private readonly BassStemFormat             _format;
        private          byte[]?                    _prerenderBytes;
        private          int[]                      _prerenderIndices = Array.Empty<int>();
        private          CancellationTokenSource?   _prerenderCts;
        private          Task                       _prerenderTask = Task.CompletedTask;

        public BassStemReplay(AudioManager manager, int stream, Func<double> normalizationGain)
        {
            Bass.ChannelGetInfo(stream, out ChannelInfo info);
            _manager = manager;
            _normalizationGain = normalizationGain;
            _format = new BassStemFormat(frequency: info.Frequency, channels: info.Channels);
        }

        public BassStemSoundboard CreateSoundboard(Func<double> stemVolume, BassChordTrack? chords)
        {
            var soundboard = new BassStemSoundboard(_manager, _format, () => stemVolume() * _normalizationGain(), chords);
            lock (_lock)
            {
                _soundboards.Add(soundboard);
            }

            return soundboard;
        }

        public BassChordTrack CreateChordTrack()
        {
            var chords = new BassChordTrack(_format);
            lock (_lock)
            {
                _chordTracks.Add(chords);
            }

            return chords;
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
            var chordTracks = new List<BassChordTrack>();
            lock (_lock)
            {
                foreach (var chords in _chordTracks)
                {
                    int jobCount = jobs.Count;
                    chords.PrepareJobs(jobs);
                    if (jobs.Count > jobCount)
                    {
                        chordTracks.Add(chords);
                    }
                }

                foreach (var soundboard in _soundboards)
                {
                    int jobCount = jobs.Count;
                    soundboard.PreparePhraseJobs(jobs);
                    if (jobs.Count > jobCount)
                    {
                        soundboards.Add(soundboard);
                    }
                }
            }

            if (jobs.Count == 0)
            {
                YargLogger.LogWarning("No freestyle phrases were registered for stem pre-rendering.");
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
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    if (!Decode(bytes, indices, _format, jobs, token))
                    {
                        return;
                    }

                    YargLogger.LogFormatInfo("[SoloPrep] decoded in {0} ms", stopwatch.ElapsedMilliseconds);
                    foreach (var chords in chordTracks)
                    {
                        var chordStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        chords.Analyze(token);
                        YargLogger.LogFormatInfo("[SoloChords] bass roots tracked in {0} ms", chordStopwatch.ElapsedMilliseconds);
                    }

                    foreach (var soundboard in soundboards)
                    {
                        soundboard.PublishPhrases(token);
                    }

                    YargLogger.LogFormatInfo("[SoloPrep] ready in {0} ms", stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    YargLogger.LogException(ex, "Stem pre-render failed.");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void ResetAfterSeek()
        {
            lock (_lock)
            {
                foreach (var soundboard in _soundboards)
                {
                    soundboard.ResetVoices();
                }
            }
        }

        public void Pump()
        {
            lock (_lock)
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
            lock (_lock)
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

        public static byte[]? CopyStream(Stream original)
        {
            if (!original.CanRead || !original.CanSeek)
            {
                return null;
            }

            long originalPosition = original.Position;
            try
            {
                using var copy = new MemoryStream();
                original.CopyTo(copy);
                return copy.ToArray();
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Failed to copy stem audio for pre-rendering.");
                return null;
            }
            finally
            {
                original.Position = originalPosition;
            }
        }

        private static bool Decode(byte[] bytes, int[] indices, BassStemFormat format,
            List<(long StartFrame, int FrameCount, float[] Samples)> jobs, CancellationToken token)
        {
            using var stream = new MemoryStream(bytes, false);
            int handle = BassX.CreateSourceUnchecked(stream);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to open stem audio for pre-rendering: {0}!", Bass.LastError);
                return false;
            }

            try
            {
                Bass.ChannelGetInfo(handle, out ChannelInfo info);
                int sourceChannels = info.Channels;
                int[] channelMap = indices.Length > 0 ? indices : CreateIdentityMap(sourceChannels);
                if (info.Frequency != format.Frequency || channelMap.Length != format.Channels)
                {
                    YargLogger.LogFormatError("Pre-rendered stem format ({0} Hz, {1} channels) does not match playback ({2} Hz, {3} channels)!",
                        info.Frequency, channelMap.Length, format.Frequency, format.Channels);
                    return false;
                }

                jobs.Sort((left, right) => left.StartFrame.CompareTo(right.StartFrame));

                long endFrame = 0;
                foreach (var job in jobs)
                {
                    endFrame = Math.Max(endFrame, job.StartFrame + job.FrameCount);
                }

                var buffer = new float[CHUNK_FRAMES * sourceChannels];
                long position = SeekTo(handle, jobs.Count > 0 ? jobs[0].StartFrame : 0, format);
                int firstJob = 0;
                while (position < endFrame)
                {
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    int byteCount = buffer.Length * FLOAT_BYTES;
                    int bytesRead = Bass.ChannelGetData(handle, buffer, byteCount | (int) DataFlags.Float);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    int frames = bytesRead / FLOAT_BYTES / sourceChannels;
                    long chunkEnd = position + frames;

                    while (firstJob < jobs.Count && jobs[firstJob].StartFrame + jobs[firstJob].FrameCount <= position)
                    {
                        firstJob++;
                    }

                    for (int index = firstJob; index < jobs.Count && jobs[index].StartFrame < chunkEnd; index++)
                    {
                        CopyChunk(jobs[index], buffer, position, frames, sourceChannels, channelMap);
                    }

                    position = chunkEnd;
                }

                return true;
            }
            finally
            {
                if (!Bass.StreamFree(handle) && Bass.LastError != Errors.Handle)
                {
                    YargLogger.LogFormatError("Failed to free pre-render stream: {0}!", Bass.LastError);
                }
            }
        }

        private static long SeekTo(int handle, long frame, BassStemFormat format)
        {
            if (frame <= 0)
            {
                return 0;
            }

            long bytes = Bass.ChannelSeconds2Bytes(handle, (double) frame / format.Frequency);
            if (!Bass.ChannelSetPosition(handle, bytes))
            {
                YargLogger.LogFormatWarning("Failed to seek stem pre-render, decoding from the start: {0}!", Bass.LastError);
                return 0;
            }

            return (long) Math.Round(Bass.ChannelBytes2Seconds(handle, Bass.ChannelGetPosition(handle)) * format.Frequency);
        }

        private static void CopyChunk((long StartFrame, int FrameCount, float[] Samples) job, float[] buffer,
            long chunkStart, int chunkFrames, int sourceChannels, int[] channelMap)
        {
            long firstFrame = Math.Max(chunkStart, job.StartFrame);
            long lastFrame = Math.Min(chunkStart + chunkFrames, job.StartFrame + job.FrameCount);
            for (long frame = firstFrame; frame < lastFrame; frame++)
            {
                int source = (int) (frame - chunkStart) * sourceChannels;
                int destination = (int) (frame - job.StartFrame) * channelMap.Length;
                for (int channel = 0; channel < channelMap.Length; channel++)
                {
                    job.Samples[destination + channel] = buffer[source + channelMap[channel]];
                }
            }
        }

        private static int[] CreateIdentityMap(int channelCount)
        {
            var map = new int[channelCount];
            for (int channel = 0; channel < channelCount; channel++)
            {
                map[channel] = channel;
            }

            return map;
        }
    }
}
