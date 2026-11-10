#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using YARG.Audio.PitchDetection;
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
        private const double PITCH_MIN_BODY_SECONDS         = 0.15;
        private const float  PITCH_MIN_VOICED               = 0.7f;
        private const float  ANCHOR_MIN_VOICED              = 0.5f;
        private const float  PITCH_AGREE_CENTS              = 50f;
        private const float  PITCH_MAX_SPREAD_CENTS         = 25f;
        private const int    MAX_SHIFT_SEMITONES            = 3;

        private static readonly int[] PENTATONIC = { 0, 2, 4, 7, 9 };

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
                var pitches = Prepare(job, token);
                if (pitches == null)
                {
                    return;
                }

                if (!Stretch(job, token))
                {
                    return;
                }

                BuildLadder(job, pitches.Value.Clean, pitches.Value.Anchors, pitches.Value.Root);
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

        private (List<(BassStemClip Clip, int Note, float Score)> Clean, List<(double SongSeconds, int Note)> Anchors, int Root)?
            Prepare(BassStemSoloSeeds job, CancellationToken token)
        {
            var tracker = new PitchTracker(_format.Frequency);
            var readings = new List<float>();
            var clean = new List<(BassStemClip Clip, int Note, float Score)>();
            var anchors = new List<(double SongSeconds, int Note)>();
            var pitchClassSeconds = new double[12];
            foreach (var pair in job.ByKey)
            {
                bool singleNote = (pair.Key & (pair.Key - 1)) == 0;
                foreach (var clip in pair.Value)
                {
                    if (token.IsCancellationRequested)
                    {
                        return null;
                    }

                    clip.TrimToCleanEnd(_format);
                    clip.AttackEndFrame = BassSustainStretch.FindAttackEnd(clip.Samples, _format);
                    if (singleNote && Measure(clip, tracker, readings) is { } pitch)
                    {
                        pitchClassSeconds[PitchClass(pitch.Note)] += pitch.Voiced * pitch.Seconds;
                        if (pitch.Voiced >= ANCHOR_MIN_VOICED)
                        {
                            anchors.Add((clip.SongSeconds, pitch.Note));
                        }

                        if (pitch.Seconds >= PITCH_MIN_BODY_SECONDS && pitch.Voiced >= PITCH_MIN_VOICED
                            && pitch.Spread <= PITCH_MAX_SPREAD_CENTS)
                        {
                            float score = (float) (pitch.Voiced * Math.Min(pitch.Seconds, 1.0)
                                / (1 + pitch.Spread / PITCH_MAX_SPREAD_CENTS));
                            clean.Add((clip, pitch.Note, score));
                        }
                    }

                    PrepareSeed(job, clip);
                }
            }

            return (clean, anchors, PentatonicRoot(pitchClassSeconds));
        }

        private (int Note, float Voiced, double Spread, double Seconds)? Measure(BassStemClip clip, PitchTracker tracker,
            List<float> readings)
        {
            int channels = _format.Channels;
            int attackFrames = clip.AttackEndFrame;
            int bodyFrames = clip.FrameCount - attackFrames;
            var mono = new float[bodyFrames];
            for (int frame = 0; frame < bodyFrames; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += clip.Samples[(attackFrames + frame) * channels + channel];
                }

                mono[frame] = sum / channels;
            }

            tracker.Reset();
            readings.Clear();
            tracker.ProcessBuffer(mono, readings);

            var voiced = readings.FindAll(pitch => pitch > 0f);
            if (voiced.Count == 0)
            {
                return null;
            }

            voiced.Sort();
            float median = voiced[voiced.Count / 2];
            int agreeing = 0;
            double spread = 0;
            foreach (float pitch in voiced)
            {
                double cents = Math.Abs(1200 * Math.Log(pitch / median, 2));
                if (cents <= PITCH_AGREE_CENTS)
                {
                    agreeing++;
                    spread += cents;
                }
            }

            int note = (int) Math.Round(69 + 12 * Math.Log(median / 440.0, 2));
            return (note, (float) agreeing / readings.Count, spread / agreeing, (double) bodyFrames / _format.Frequency);
        }

        private static int PentatonicRoot(double[] pitchClassSeconds)
        {
            int bestRoot = 0;
            double bestSeconds = -1;
            for (int root = 0; root < 12; root++)
            {
                double seconds = 0;
                foreach (int step in PENTATONIC)
                {
                    seconds += pitchClassSeconds[(root + step) % 12];
                }

                if (seconds > bestSeconds)
                {
                    bestSeconds = seconds;
                    bestRoot = root;
                }
            }

            return bestRoot;
        }

        private void BuildLadder(BassStemSoloSeeds job, List<(BassStemClip Clip, int Note, float Score)> clean,
            List<(double SongSeconds, int Note)> anchors, int root)
        {
            if (clean.Count == 0)
            {
                YargLogger.LogInfo("[SoloPitch] no clean pitches, keeping chart frets");
                return;
            }

            int lowest = int.MaxValue;
            int highest = int.MinValue;
            foreach (var candidate in clean)
            {
                lowest = Math.Min(lowest, candidate.Note);
                highest = Math.Max(highest, candidate.Note);
            }

            var notes = new List<int>();
            for (int note = lowest - MAX_SHIFT_SEMITONES; note <= highest + MAX_SHIFT_SEMITONES; note++)
            {
                if (Array.IndexOf(PENTATONIC, PitchClass(note - root)) < 0)
                {
                    continue;
                }

                BassStemClip? nearest = null;
                int nearestShift = 0;
                float nearestScore = 0;
                foreach (var candidate in clean)
                {
                    int shift = note - candidate.Note;
                    int distance = Math.Abs(shift);
                    if (distance > MAX_SHIFT_SEMITONES)
                    {
                        continue;
                    }

                    if (nearest == null || distance < Math.Abs(nearestShift)
                        || distance == Math.Abs(nearestShift) && candidate.Score > nearestScore)
                    {
                        nearest = candidate.Clip;
                        nearestShift = shift;
                        nearestScore = candidate.Score;
                    }
                }

                if (nearest != null)
                {
                    notes.Add(note);
                    job.Ladder.Add(nearest.Shifted(nearestShift, _format));
                }
            }

            if (notes.Count < BassStemSoloSeeds.FRET_COUNT)
            {
                YargLogger.LogFormatInfo("[SoloPitch] {0} clean pitches reach only {1} scale notes, keeping chart frets",
                    clean.Count, notes.Count);
                job.Ladder.Clear();
                return;
            }

            anchors.Sort((left, right) => left.SongSeconds.CompareTo(right.SongSeconds));
            int start = -1;
            foreach (var anchor in anchors)
            {
                int nearest = 0;
                for (int index = 1; index < notes.Count; index++)
                {
                    if (Math.Abs(notes[index] - anchor.Note) < Math.Abs(notes[nearest] - anchor.Note))
                    {
                        nearest = index;
                    }
                }

                if (start >= 0 && nearest >= start && nearest < start + BassStemSoloSeeds.FRET_COUNT)
                {
                    continue;
                }

                start = Math.Max(0, Math.Min(nearest - BassStemSoloSeeds.FRET_COUNT / 2, notes.Count - BassStemSoloSeeds.FRET_COUNT));
                job.Windows.Add((anchor.SongSeconds, start));
            }

            YargLogger.LogFormatInfo<int, string, int>("[SoloPitch] pentatonic root {0}, ladder MIDI notes {1}, {2} window moves",
                root, string.Join(", ", notes), job.Windows.Count - 1);
        }

        private static int PitchClass(int note)
        {
            return (note % 12 + 12) % 12;
        }

        private void PrepareSeed(BassStemSoloSeeds job, BassStemClip clip)
        {
            var raw = clip.Samples;
            bool cleanLoop = clip.PrepareLoop(_format);
            if (!cleanLoop && BassSustainStretch.TryRender(clip.Samples, _format, out var samples, out int bodyStartFrame))
            {
                clip.Samples = samples;
                clip.FrameCount = samples.Length / _format.Channels;
                clip.LoopStretchedBody(bodyStartFrame, _format);
                if (job.HasRegion)
                {
                    job.Candidates.Add((clip, raw, clip.AttackEndFrame));
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
