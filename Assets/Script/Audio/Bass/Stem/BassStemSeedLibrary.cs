#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Turns the engine's solo phrases into the seed sets the stem soundboard renders from.
    /// </summary>
    internal sealed class BassStemSeedLibrary
    {
        private const double STRETCH_REGION_PREROLL_SECONDS = 0.1;
        private const double STRETCH_MIN_BODY_SECONDS       = 0.02;
        private const double STRETCH_FADE_SECONDS           = 0.01;

        // Lock order: the stem lock (BassStemReplay.Lock) is always taken before this one.
        private readonly object                  _lock = new();
        private readonly BassStemFormat          _format;
        private readonly Random                  _random = new();
        private          List<BassStemSoloSeeds> _requests  = new();
        private          List<BassStemSoloSeeds> _pending   = new();
        private          List<BassStemSoloSeeds> _published = new();
        private          BassStemSoloSeeds?      _selected;
        private          double                  _requestedSoloTime;

        public BassStemSeedLibrary(BassStemFormat format)
        {
            _format = format;
        }

        public void Request(IReadOnlyList<SoloSection> phrases, List<GuitarNote> notes, Func<double, double> playbackTime)
        {
            var requests = new List<BassStemSoloSeeds>(phrases.Count);
            foreach (var phrase in phrases)
            {
                var seedNotes = new List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)>();
                foreach (var note in notes)
                {
                    if (note.Tick < phrase.StartTick || note.Tick > phrase.EndTick)
                    {
                        continue;
                    }

                    seedNotes.Add((note.NoteMask, note.Time, playbackTime(note.Time),
                        ClipDuration(note), HasSustain(note)));
                }

                if (seedNotes.Count == 0)
                {
                    continue;
                }

                requests.Add(new BassStemSoloSeeds(seedNotes[0].SongSeconds, seedNotes[^1].SongSeconds, seedNotes));
            }

            lock (_lock)
            {
                _requests = requests;
            }
        }

        public void Select(double soloTime, double songTime)
        {
            lock (_lock)
            {
                _requestedSoloTime = soloTime;
                _selected = Find(soloTime);
                _selected?.Select(songTime);
            }
        }

        public void Forget()
        {
            lock (_lock)
            {
                _requests = new List<BassStemSoloSeeds>();
                _pending = new List<BassStemSoloSeeds>();
                _published.Clear();
                _selected = null;
            }
        }

        public bool HasSeeds(double soloTime)
        {
            lock (_lock)
            {
                var seeds = Find(soloTime);
                return seeds != null && !seeds.IsEmpty;
            }
        }

        public bool TryGetClip(int key, out BassStemClip clip)
        {
            lock (_lock)
            {
                var selected = _selected;
                if (selected == null)
                {
                    clip = null!;
                    return false;
                }

                if (key == GuitarEngine.OPEN_MASK)
                {
                    return TryGetRandomFretClip(selected, out clip) || selected.TryGetAnyClip(out clip);
                }

                if (selected.TryGetClip(key, out clip))
                {
                    return true;
                }

                for (int fret = 1; fret <= key; fret <<= 1)
                {
                    if ((key & fret) != 0 && selected.TryGetClip(fret, out clip))
                    {
                        return true;
                    }
                }

                return selected.TryGetAnyClip(out clip);
            }
        }

        public void PrepareJobs(List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            lock (_lock)
            {
                _pending = BuildJobs(_requests, jobs);
            }
        }

        public void Publish(CancellationToken token)
        {
            List<BassStemSoloSeeds> pending;
            double requestedSoloTime;
            lock (_lock)
            {
                pending = _pending;
                _pending = new List<BassStemSoloSeeds>();
                requestedSoloTime = _requestedSoloTime;
            }

            var published = new List<BassStemSoloSeeds>(pending.Count);
            while (pending.Count > 0)
            {
                var job = TakeNext(pending, requestedSoloTime);
                if (!Prepare(job, token))
                {
                    return;
                }

                if (!Stretch(job, token))
                {
                    return;
                }

                published.Add(job);
            }

            lock (_lock)
            {
                _published.Clear();
                _published.AddRange(published);
                _selected = null;
            }
        }

        private List<BassStemSoloSeeds> BuildJobs(List<BassStemSoloSeeds> requests,
            List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            bool stretchAvailable = BassStemReplay.ShouldStretch();
            var renderJobs = new List<BassStemSoloSeeds>();
            foreach (var job in requests)
            {
                foreach (var note in job.Notes)
                {
                    AddNoteSeed(job, note, jobs);
                }

                if (stretchAvailable)
                {
                    AddRegionJob(job, jobs);
                }

                foreach (var keyed in job.ByKey.Values)
                {
                    keyed.Sort((left, right) => left.SongSeconds.CompareTo(right.SongSeconds));
                }

                renderJobs.Add(job);
            }

            return renderJobs;
        }

        private void AddNoteSeed(BassStemSoloSeeds job,
            (int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain) note,
            List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            long startFrame = (long) Math.Round(note.PlaybackSeconds * _format.Frequency);
            int frameCount = BassStemClip.FramesFor(_format, note.DurationSeconds);
            if (startFrame < 0 || frameCount <= 0)
            {
                return;
            }

            var clip = new BassStemClip
            {
                SongSeconds = note.SongSeconds,
                StartFrame = startFrame,
                Samples = new float[frameCount * _format.Channels],
                FrameCount = frameCount,
                IsSustain = note.IsSustain,
            };
            if (!job.ByKey.TryGetValue(note.Key, out var keyed))
            {
                keyed = new List<BassStemClip>();
                job.ByKey.Add(note.Key, keyed);
            }

            keyed.Add(clip);
            jobs.Add((startFrame, frameCount, clip.Samples));
        }

        private void AddRegionJob(BassStemSoloSeeds job, List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            long firstFrame = long.MaxValue;
            long endFrame = 0;
            foreach (var keyed in job.ByKey.Values)
            {
                foreach (var clip in keyed)
                {
                    firstFrame = Math.Min(firstFrame, clip.StartFrame);
                    endFrame = Math.Max(endFrame, clip.StartFrame + clip.FrameCount);
                }
            }

            if (endFrame <= firstFrame)
            {
                return;
            }

            job.RegionStartFrame = Math.Max(0, firstFrame - _format.FramesFor(STRETCH_REGION_PREROLL_SECONDS));
            int frameCount = (int) (endFrame - job.RegionStartFrame);
            job.Region = new float[frameCount * _format.Channels];
            jobs.Add((job.RegionStartFrame, frameCount, job.Region));
        }

        private bool Prepare(BassStemSoloSeeds job, CancellationToken token)
        {
            foreach (var pair in job.ByKey)
            {
                foreach (var clip in pair.Value)
                {
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    PrepareSeed(job, clip);
                }
            }

            return true;
        }

        private void PrepareSeed(BassStemSoloSeeds job, BassStemClip clip)
        {
            clip.TrimToCleanEnd(_format);
            var raw = clip.Samples;
            bool cleanLoop = clip.PrepareLoop(_format);
            if (!cleanLoop && BassSustainStretch.TryRender(clip.Samples, _format, out var samples, out int bodyStartFrame))
            {
                clip.Samples = samples;
                clip.FrameCount = samples.Length / _format.Channels;
                clip.LoopStretchedBody(bodyStartFrame, _format);
                if (job.HasRegion)
                {
                    job.Candidates.Add((clip, raw, BassSustainStretch.FindAttackEnd(raw, _format)));
                }
            }

            clip.ApplyStartFade(_format);
        }

        private bool Stretch(BassStemSoloSeeds job, CancellationToken token)
        {
            if (!job.HasRegion)
            {
                return true;
            }

            int minBodyFrames = _format.FramesFor(STRETCH_MIN_BODY_SECONDS);
            var bodies = new List<(long BodyStartFrame, int BodyFrames)>();
            var bodyIndexes = new int[job.Candidates.Count];
            for (int index = 0; index < job.Candidates.Count; index++)
            {
                var (clip, raw, attackFrames) = job.Candidates[index];
                int bodyFrames = raw.Length / _format.Channels - attackFrames;
                bodyIndexes[index] = -1;
                if (bodyFrames < minBodyFrames)
                {
                    continue;
                }

                bodyIndexes[index] = bodies.Count;
                bodies.Add((clip.StartFrame + attackFrames - job.RegionStartFrame, bodyFrames));
            }

            float[]?[] outputs = Array.Empty<float[]?>();
            if (bodies.Count > 0)
            {
                var stopwatch = Stopwatch.StartNew();
                outputs = BassSoloStretch.Render(job.Region, _format, bodies, token);
                YargLogger.LogFormatInfo("[SoloStretch] {0} sustains in {1} ms",
                    bodies.Count, stopwatch.ElapsedMilliseconds);
            }

            for (int index = 0; index < job.Candidates.Count; index++)
            {
                if (token.IsCancellationRequested)
                {
                    return false;
                }

                var (clip, raw, attackFrames) = job.Candidates[index];
                int body = bodyIndexes[index];
                if (body >= 0 && outputs[body] is float[] output)
                {
                    ApplyStretchedBody(clip, raw, attackFrames, output);
                }
            }

            job.Region = Array.Empty<float>();
            job.Candidates.Clear();
            return true;
        }

        private void ApplyStretchedBody(BassStemClip clip, float[] raw, int attackFrames, float[] body)
        {
            int channels = _format.Channels;
            int bodyFrames = body.Length / channels;
            int fadeFrames = Math.Min(_format.FramesFor(STRETCH_FADE_SECONDS), bodyFrames);
            var samples = new float[(attackFrames + bodyFrames) * channels];
            Array.Copy(raw, samples, attackFrames * channels);
            for (int frame = 0; frame < bodyFrames; frame++)
            {
                float blend = frame < fadeFrames
                    ? (float) (0.5 - 0.5 * Math.Cos(Math.PI * (frame + 1) / (fadeFrames + 1)))
                    : 1;
                for (int channel = 0; channel < channels; channel++)
                {
                    int output = (attackFrames + frame) * channels + channel;
                    float rawSample = output < raw.Length ? raw[output] : 0;
                    samples[output] = rawSample * (1 - blend) + body[frame * channels + channel] * blend;
                }
            }

            clip.Samples = samples;
            clip.FrameCount = attackFrames + bodyFrames;
            clip.LoopStretchedBody(attackFrames + fadeFrames, _format);
            clip.ApplyStartFade(_format);
        }

        private BassStemSoloSeeds? Find(double soloTime)
        {
            foreach (var seeds in _published)
            {
                if (seeds.Covers(soloTime))
                {
                    return seeds;
                }
            }

            return null;
        }

        /// <summary>
        /// The two derived properties of a chart note that the stem layer needs: how long its clip runs
        /// and whether it holds.
        /// </summary>
        private static double ClipDuration(GuitarNote note)
        {
            return note.NextNote != null ? note.NextNote.Time - note.Time : double.MaxValue;
        }

        private static bool HasSustain(GuitarNote note)
        {
            foreach (var child in note.AllNotes)
            {
                if (child.IsSustain)
                {
                    return true;
                }
            }

            return false;
        }

        private static BassStemSoloSeeds TakeNext(List<BassStemSoloSeeds> queue, double soloTime)
        {
            int index = Math.Max(queue.FindIndex(job => job.Covers(soloTime)), 0);
            var job = queue[index];
            queue.RemoveAt(index);
            return job;
        }

        private bool TryGetRandomFretClip(BassStemSoloSeeds seeds, out BassStemClip clip)
        {
            clip = null!;
            int availableFrets = 0;
            for (int fretMask = 1; fretMask < GuitarEngine.OPEN_MASK; fretMask <<= 1)
            {
                if (!seeds.TryGetClip(fretMask, out var fretClip))
                {
                    continue;
                }

                availableFrets++;
                if (_random.Next(availableFrets) == 0)
                {
                    clip = fretClip;
                }
            }

            return availableFrets > 0;
        }
    }
}
