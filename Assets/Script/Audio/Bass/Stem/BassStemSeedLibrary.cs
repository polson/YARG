#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
        private const double PITCH_MAX_MEASURE_SECONDS      = 0.4;
        private const double LEVEL_SECONDS                  = 0.3;
        private const double MIN_LEVEL_GAIN                 = 0.5;
        private const double MAX_LEVEL_GAIN                 = 3;
        private const float  PITCH_MIN_VOICED               = 0.7f;
        private const float  ANCHOR_MIN_VOICED              = 0.5f;
        private const float  PITCH_AGREE_CENTS              = 50f;
        private const float  PITCH_MAX_SPREAD_CENTS         = 25f;
        private const int    MAX_SHIFT_SEMITONES            = 3;
        private const int    MAX_LADDER_STEP_SEMITONES      = 3;
        private const double BEND_CHECK_SECONDS             = 0.15;
        private const float  BEND_MIN_CENTS                 = 30f;
        private const float  BEND_MAX_CENTS                 = 400f;

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
            lock (_lock)
            {
                pending = _pending;
                _pending = new List<BassStemSoloSeeds>();
                _published.Clear();
                _selected = null;
            }

            while (pending.Count > 0)
            {
                double requestedSoloTime;
                lock (_lock)
                {
                    requestedSoloTime = _requestedSoloTime;
                }

                var job = TakeNext(pending, requestedSoloTime);
                if (!Prepare(job, token))
                {
                    return;
                }

                lock (_lock)
                {
                    _published.Add(job);
                }
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
            var clips = new List<(BassStemClip Clip, bool SingleNote)>();
            foreach (var pair in job.ByKey)
            {
                bool singleNote = (pair.Key & (pair.Key - 1)) == 0;
                foreach (var clip in pair.Value)
                {
                    clips.Add((clip, singleNote));
                }
            }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            };
            var pitches = new (int Note, double Hz, float Voiced, double Spread, double Seconds, bool Bent)?[clips.Count];
            var levels = new double[clips.Count];
            Parallel.For(0, clips.Count, options,
                () => (Tracker: new PitchTracker(_format.Frequency), Readings: new List<float>()),
                (index, state, local) =>
                {
                    if (token.IsCancellationRequested)
                    {
                        state.Stop();
                        return local;
                    }

                    var (clip, singleNote) = clips[index];
                    clip.TrimToCleanEnd(_format);
                    clip.AttackEndFrame = BassSustainStretch.FindAttackEnd(clip.Samples, _format);
                    levels[index] = Level(clip);
                    if (singleNote)
                    {
                        pitches[index] = Measure(clip, local.Tracker, local.Readings);
                    }

                    return local;
                },
                _ => { });
            if (token.IsCancellationRequested)
            {
                return false;
            }

            var clean = new List<(BassStemClip Clip, int Note, float Score, bool Bent)>();
            var anchors = new List<(double SongSeconds, int Note)>();
            var pitchClassSeconds = new double[12];
            for (int index = 0; index < clips.Count; index++)
            {
                if (pitches[index] is not { } pitch)
                {
                    continue;
                }

                var clip = clips[index].Clip;
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
                    clip.PitchHz = pitch.Hz;
                    clean.Add((clip, pitch.Note, score, pitch.Bent));
                }
            }

            int root = PentatonicRoot(pitchClassSeconds);
            var heard = Array.FindAll(levels, level => level > 0);
            Array.Sort(heard);
            double soloLevel = heard.Length > 0 ? heard[heard.Length / 2] : 0;
            var rungs = PlanLadder(clean, root);
            var played = new List<BassStemClip>();
            foreach (var (clip, _) in clips)
            {
                if (rungs == null || rungs.Exists(rung => rung.Clip == clip))
                {
                    played.Add(clip);
                }
            }

            bool hasRegion = job.HasRegion;
            var candidates = new (BassStemClip Clip, float[] Raw, int AttackFrames)?[played.Count];
            Parallel.For(0, played.Count, options, (index, state) =>
            {
                if (token.IsCancellationRequested)
                {
                    state.Stop();
                    return;
                }

                candidates[index] = PrepareSeed(played[index], hasRegion);
            });
            if (token.IsCancellationRequested)
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (candidate is { } stretched)
                {
                    job.Candidates.Add(stretched);
                }
            }

            if (!Stretch(job, token))
            {
                return false;
            }

            if (rungs != null)
            {
                BuildLadder(job, rungs, anchors, root, soloLevel);
            }

            return true;
        }

        private (int Note, double Hz, float Voiced, double Spread, double Seconds, bool Bent)? Measure(BassStemClip clip, PitchTracker tracker,
            List<float> readings)
        {
            int channels = _format.Channels;
            int attackFrames = clip.AttackEndFrame;
            int bodyFrames = clip.FrameCount - attackFrames;
            var mono = new float[Math.Min(bodyFrames, _format.FramesFor(PITCH_MAX_MEASURE_SECONDS))];
            for (int frame = 0; frame < mono.Length; frame++)
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
            return (note, median, (float) agreeing / readings.Count, spread / agreeing, (double) bodyFrames / _format.Frequency,
                StartsWithBend(readings, median, (double) mono.Length / _format.Frequency));
        }

        private static bool StartsWithBend(List<float> readings, float settledHz, double measuredSeconds)
        {
            int startReadings = (int) Math.Ceiling(BEND_CHECK_SECONDS / measuredSeconds * readings.Count);
            for (int index = 0; index < Math.Min(startReadings, readings.Count); index++)
            {
                if (readings[index] <= 0f)
                {
                    continue;
                }

                double cents = Math.Abs(1200 * Math.Log(readings[index] / settledHz, 2));
                if (cents >= BEND_MIN_CENTS && cents <= BEND_MAX_CENTS)
                {
                    return true;
                }
            }

            return false;
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

        private static List<(int Note, BassStemClip Clip, int Shift)>? PlanLadder(
            List<(BassStemClip Clip, int Note, float Score, bool Bent)> clean, int root)
        {
            if (clean.Count == 0)
            {
                YargLogger.LogInfo("[SoloPitch] no clean pitches, keeping chart frets");
                return null;
            }

            int lowest = int.MaxValue;
            int highest = int.MinValue;
            foreach (var candidate in clean)
            {
                lowest = Math.Min(lowest, candidate.Note);
                highest = Math.Max(highest, candidate.Note);
            }

            var rungs = new List<(int Note, BassStemClip Clip, int Shift)>();
            var bentRungs = new List<bool>();
            for (int note = lowest - MAX_SHIFT_SEMITONES; note <= highest + MAX_SHIFT_SEMITONES; note++)
            {
                if (Array.IndexOf(PENTATONIC, PitchClass(note - root)) < 0)
                {
                    continue;
                }

                BassStemClip? nearest = null;
                int nearestShift = 0;
                float nearestScore = 0;
                bool nearestBent = false;
                foreach (var candidate in clean)
                {
                    int shift = note - candidate.Note;
                    int distance = Math.Abs(shift);
                    if (distance > MAX_SHIFT_SEMITONES)
                    {
                        continue;
                    }

                    bool better = nearest == null
                        || nearestBent && !candidate.Bent
                        || nearestBent == candidate.Bent && (distance < Math.Abs(nearestShift)
                            || distance == Math.Abs(nearestShift) && candidate.Score > nearestScore);
                    if (better)
                    {
                        nearest = candidate.Clip;
                        nearestShift = shift;
                        nearestScore = candidate.Score;
                        nearestBent = candidate.Bent;
                    }
                }

                if (nearest != null)
                {
                    rungs.Add((note, nearest, nearestShift));
                    bentRungs.Add(nearestBent);
                }
            }

            var (runStart, runLength) = LongestUnbrokenRun(rungs);
            if (runLength < rungs.Count)
            {
                YargLogger.LogFormatInfo("[SoloPitch] dropping ladder notes outside the unbroken run {0}..{1}",
                    rungs[runStart].Note, rungs[runStart + runLength - 1].Note);
                rungs = rungs.GetRange(runStart, runLength);
                bentRungs = bentRungs.GetRange(runStart, runLength);
            }

            YargLogger.LogFormatInfo<int, int, int, int>(
                "[SoloPitch] {0} of {1} clean pitches start with a bend, {2} of {3} ladder notes still use one",
                clean.FindAll(candidate => candidate.Bent).Count, clean.Count, bentRungs.FindAll(bent => bent).Count,
                rungs.Count);

            if (rungs.Count < BassStemSoloSeeds.FRET_COUNT)
            {
                YargLogger.LogFormatInfo("[SoloPitch] {0} clean pitches reach only {1} scale notes, keeping chart frets",
                    clean.Count, rungs.Count);
                return null;
            }

            return rungs;
        }

        private static (int Start, int Length) LongestUnbrokenRun(List<(int Note, BassStemClip Clip, int Shift)> rungs)
        {
            int bestStart = 0;
            int bestLength = 0;
            int start = 0;
            for (int index = 0; index <= rungs.Count; index++)
            {
                bool broken = index == rungs.Count
                    || index > start && rungs[index].Note - rungs[index - 1].Note > MAX_LADDER_STEP_SEMITONES;
                if (!broken)
                {
                    continue;
                }

                if (index - start > bestLength)
                {
                    bestStart = start;
                    bestLength = index - start;
                }

                start = index;
            }

            return (bestStart, bestLength);
        }

        private void BuildLadder(BassStemSoloSeeds job, List<(int Note, BassStemClip Clip, int Shift)> rungs,
            List<(double SongSeconds, int Note)> anchors, int root, double soloLevel)
        {
            int locked = 0;
            foreach (var rung in rungs)
            {
                var clip = rung.Clip.Shifted(rung.Shift, _format);
                if (clip.LockLoopToPeriod(_format.Frequency / clip.PitchHz, _format))
                {
                    locked++;
                }

                job.Ladder.Add(clip);
            }

            MatchLevels(job.Ladder, soloLevel);

            anchors.Sort((left, right) => left.SongSeconds.CompareTo(right.SongSeconds));
            int start = -1;
            foreach (var anchor in anchors)
            {
                int nearest = 0;
                for (int index = 1; index < rungs.Count; index++)
                {
                    if (Math.Abs(rungs[index].Note - anchor.Note) < Math.Abs(rungs[nearest].Note - anchor.Note))
                    {
                        nearest = index;
                    }
                }

                if (start >= 0 && nearest >= start && nearest < start + BassStemSoloSeeds.FRET_COUNT)
                {
                    continue;
                }

                start = Math.Max(0, Math.Min(nearest - BassStemSoloSeeds.FRET_COUNT / 2,
                    rungs.Count - BassStemSoloSeeds.FRET_COUNT));
                job.Windows.Add((anchor.SongSeconds, start));
            }

            YargLogger.LogFormatInfo("[SoloPitch] pentatonic root {0}, ladder MIDI notes {1}, {2} pitch-locked loops, {3} window moves",
                root, string.Join(", ", rungs.ConvertAll(rung => rung.Note)), locked, job.Windows.Count - 1);
        }

        private void MatchLevels(List<BassStemClip> ladder, double soloLevel)
        {
            foreach (var clip in ladder)
            {
                double level = Level(clip);
                if (level <= 0 || soloLevel <= 0)
                {
                    continue;
                }

                float gain = (float) Math.Clamp(soloLevel / level, MIN_LEVEL_GAIN, MAX_LEVEL_GAIN);
                for (int sample = 0; sample < clip.Samples.Length; sample++)
                {
                    clip.Samples[sample] *= gain;
                }

                for (int sample = 0; sample < clip.LoopTail.Length; sample++)
                {
                    clip.LoopTail[sample] *= gain;
                }
            }
        }

        private double Level(BassStemClip clip)
        {
            int channels = _format.Channels;
            int frames = Math.Min(clip.FrameCount, _format.FramesFor(LEVEL_SECONDS));
            double energy = 0;
            for (int sample = 0; sample < frames * channels; sample++)
            {
                energy += (double) clip.Samples[sample] * clip.Samples[sample];
            }

            return frames > 0 ? Math.Sqrt(energy / (frames * channels)) : 0;
        }

        private static int PitchClass(int note)
        {
            return (note % 12 + 12) % 12;
        }

        private (BassStemClip Clip, float[] Raw, int AttackFrames)? PrepareSeed(BassStemClip clip, bool hasRegion)
        {
            var raw = clip.Samples;
            (BassStemClip Clip, float[] Raw, int AttackFrames)? candidate = null;
            bool cleanLoop = clip.PrepareLoop(_format);
            if (!cleanLoop && BassSustainStretch.TryRender(clip.Samples, _format, out var samples, out int bodyStartFrame))
            {
                clip.Samples = samples;
                clip.FrameCount = samples.Length / _format.Channels;
                clip.LoopStretchedBody(bodyStartFrame, _format);
                if (hasRegion)
                {
                    candidate = (clip, raw, clip.AttackEndFrame);
                }
            }

            clip.ApplyStartFade(_format);
            return candidate;
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
