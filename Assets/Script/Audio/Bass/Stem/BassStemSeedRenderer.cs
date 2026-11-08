#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSeedRenderer
    {
        private const double STRETCH_REGION_PREROLL_SECONDS = 0.1;
        private const double STRETCH_BLEED_GUARD_SECONDS    = 0.03;
        private const double STRETCH_MIN_BODY_SECONDS       = 0.02;
        private const double STRETCH_FADE_SECONDS           = 0.01;

        private readonly BassStemFormat           _format;
        private readonly BassStretchRenderFactory _createRender;
        private readonly Func<bool>               _stretchAvailable;

        public BassStemSeedRenderer(BassStemFormat format, BassStretchRenderFactory createRender,
            Func<bool> stretchAvailable)
        {
            _format = format;
            _createRender = createRender;
            _stretchAvailable = stretchAvailable;
        }

        public List<BassStemSeedJob> BuildJobs(List<BassStemSeedSet> seedSets, List<BassStemSampleJob> jobs)
        {
            bool stretchAvailable = _stretchAvailable();
            var renderJobs = new List<BassStemSeedJob>();
            foreach (var seedSet in seedSets)
            {
                var seeds = new BassStemSoloSeeds(seedSet.StartTime, seedSet.EndTime);
                var job = new BassStemSeedJob(seeds);
                foreach (var note in seedSet.Notes)
                {
                    AddNoteSeed(seeds, note, jobs);
                }

                if (stretchAvailable)
                {
                    AddRegionJob(job, jobs);
                }

                foreach (var keyed in seeds.ByKey.Values)
                {
                    keyed.Sort((left, right) => left.SongSeconds.CompareTo(right.SongSeconds));
                }

                renderJobs.Add(job);
            }

            return renderJobs;
        }

        public bool Prepare(BassStemSeedJob job, CancellationToken token)
        {
            foreach (var pair in job.Seeds.ByKey)
            {
                foreach (var seed in pair.Value)
                {
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    PrepareSeed(job, key: pair.Key, seed: seed);
                }
            }

            return true;
        }

        public bool Stretch(BassStemSeedJob job, CancellationToken token)
        {
            if (!job.HasRegion)
            {
                return true;
            }

            var targets = BuildTargets(job);
            if (targets.Count > 0)
            {
                var stopwatch = Stopwatch.StartNew();
                BassSoloStretch.Render(job.Region, _format, targets, token, _createRender);
                YargLogger.LogFormatInfo("[SoloStretch] {0} sustains in {1} ms",
                    targets.Count, stopwatch.ElapsedMilliseconds);
            }

            foreach (var candidate in job.Candidates)
            {
                if (token.IsCancellationRequested)
                {
                    return false;
                }

                var body = candidate.Target?.Output;
                if (body != null)
                {
                    candidate.Seed.Clip = BuildStretchedClip(candidate, body);
                }
            }

            job.Region = Array.Empty<float>();
            job.Candidates.Clear();
            return true;
        }

        private List<BassSoloStretchTarget> BuildTargets(BassStemSeedJob job)
        {
            int guardFrames = _format.FramesFor(STRETCH_BLEED_GUARD_SECONDS);
            int minBodyFrames = _format.FramesFor(STRETCH_MIN_BODY_SECONDS);
            var targets = new List<BassSoloStretchTarget>();
            foreach (var candidate in job.Candidates)
            {
                candidate.AttackFrames = BassSustainStretch.FindAttackEnd(candidate.Raw, _format);
                int bodyFrames = candidate.Raw.Length / _format.Channels - guardFrames - candidate.AttackFrames;
                if (bodyFrames < minBodyFrames)
                {
                    continue;
                }

                long bodyStart = candidate.Seed.StartFrame + candidate.AttackFrames - job.RegionStartFrame;
                candidate.Target = new BassSoloStretchTarget(bodyStartFrame: bodyStart, bodyFrames: bodyFrames);
                targets.Add(candidate.Target);
            }

            return targets;
        }

        private void AddNoteSeed(BassStemSoloSeeds seeds, BassStemSeedNote note, List<BassStemSampleJob> jobs)
        {
            long startFrame = (long) Math.Round(note.PlaybackSeconds * _format.Frequency);
            int frameCount = BassStemClip.FramesFor(_format, note.DurationSeconds);
            if (startFrame < 0 || frameCount <= 0)
            {
                return;
            }

            var clip = new BassStemClip
            {
                Samples = new float[frameCount * _format.Channels],
                FrameCount = frameCount,
                IsSustain = note.IsSustain,
            };
            int sustainStart = Math.Min(_format.FramesFor(note.SustainStartSeconds), frameCount - 1);
            if (!seeds.ByKey.TryGetValue(note.Key, out var keyed))
            {
                keyed = new List<BassStemSeed>();
                seeds.ByKey.Add(note.Key, keyed);
            }

            keyed.Add(new BassStemSeed(songSeconds: note.SongSeconds, clip: clip, sustainStartFrame: sustainStart,
                startFrame: startFrame));
            jobs.Add(new BassStemSampleJob(startFrame: startFrame, frameCount: frameCount, samples: clip.Samples));
        }

        private void AddRegionJob(BassStemSeedJob job, List<BassStemSampleJob> jobs)
        {
            long firstFrame = long.MaxValue;
            long endFrame = 0;
            foreach (var keyed in job.Seeds.ByKey.Values)
            {
                foreach (var seed in keyed)
                {
                    firstFrame = Math.Min(firstFrame, seed.StartFrame);
                    endFrame = Math.Max(endFrame, seed.StartFrame + seed.Clip.FrameCount);
                }
            }

            if (endFrame <= firstFrame)
            {
                return;
            }

            job.RegionStartFrame = Math.Max(0, firstFrame - _format.FramesFor(STRETCH_REGION_PREROLL_SECONDS));
            int frameCount = (int) (endFrame - job.RegionStartFrame);
            job.Region = new float[frameCount * _format.Channels];
            jobs.Add(new BassStemSampleJob(startFrame: job.RegionStartFrame, frameCount: frameCount,
                samples: job.Region));
        }

        private void PrepareSeed(BassStemSeedJob job, int key, BassStemSeed seed)
        {
            var clip = seed.Clip;
            clip.FilledFrames = clip.FrameCount;
            var raw = clip.Samples;
            bool cleanLoop = clip.PrepareLoop(seed.SustainStartFrame, _format);
            if (!cleanLoop && BassSustainStretch.TryRender(clip.Samples, _format, out var samples, out int bodyStartFrame))
            {
                clip.Samples = samples;
                clip.FrameCount = samples.Length / _format.Channels;
                clip.FilledFrames = clip.FrameCount;
                clip.LoopStretchedBody(bodyStartFrame, _format);
                if (job.HasRegion)
                {
                    job.Candidates.Add(new BassStemStretchCandidate(key: key, seed: seed, raw: raw));
                }
            }

            clip.ApplyStartFade(_format);
        }

        private BassStemClip BuildStretchedClip(BassStemStretchCandidate candidate, float[] body)
        {
            int channels = _format.Channels;
            int attackFrames = candidate.AttackFrames;
            int bodyFrames = body.Length / channels;
            int fadeFrames = Math.Min(_format.FramesFor(STRETCH_FADE_SECONDS), bodyFrames);
            var samples = new float[(attackFrames + bodyFrames) * channels];
            Array.Copy(candidate.Raw, samples, attackFrames * channels);
            for (int frame = 0; frame < bodyFrames; frame++)
            {
                float blend = frame < fadeFrames
                    ? (float) (0.5 - 0.5 * Math.Cos(Math.PI * (frame + 1) / (fadeFrames + 1)))
                    : 1;
                for (int channel = 0; channel < channels; channel++)
                {
                    int output = (attackFrames + frame) * channels + channel;
                    float raw = output < candidate.Raw.Length ? candidate.Raw[output] : 0;
                    samples[output] = raw * (1 - blend) + body[frame * channels + channel] * blend;
                }
            }

            var clip = new BassStemClip
            {
                Samples = samples,
                FrameCount = attackFrames + bodyFrames,
                FilledFrames = attackFrames + bodyFrames,
                IsSustain = candidate.Seed.Clip.IsSustain,
            };
            clip.LoopStretchedBody(attackFrames + fadeFrames, _format);
            clip.ApplyStartFade(_format);
            return clip;
        }
    }
}
