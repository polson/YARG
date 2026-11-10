#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace YARG.Audio.BASS
{
    internal sealed class BassChordTrack
    {
        private const int    TARGET_RATE    = 5512;
        private const double MIN_FREQUENCY  = 30;
        private const double MAX_FREQUENCY  = 400;
        private const double WINDOW_SECONDS = 0.06;
        private const double HOP_SECONDS    = 0.05;
        private const double PAD_SECONDS    = 0.5;
        private const double HOLD_SECONDS   = 1.0;
        private const int    VOTE_RADIUS    = 6;
        private const int    MIN_VOTES      = 3;
        private const int    FIFTH          = 7;

        private sealed class Region
        {
            public double  StartSeconds;
            public double  EndSeconds;
            public long    StartFrame;
            public float[] Samples = Array.Empty<float>();
            public int[]   Roots   = Array.Empty<int>();
        }

        private readonly object         _lock = new();
        private readonly BassStemFormat _format;
        private          List<Region>   _requests  = new();
        private          List<Region>   _published = new();

        public BassChordTrack(BassStemFormat format)
        {
            _format = format;
        }

        public void Request(IReadOnlyList<(double StartSeconds, double EndSeconds)> ranges, Func<double, double> playbackTime)
        {
            var requests = new List<Region>(ranges.Count);
            foreach (var (startSeconds, endSeconds) in ranges)
            {
                double start = startSeconds - PAD_SECONDS;
                requests.Add(new Region
                {
                    StartSeconds = start,
                    EndSeconds = endSeconds + PAD_SECONDS,
                    StartFrame = (long) Math.Round(playbackTime(start) * _format.Frequency),
                });
            }

            lock (_lock)
            {
                _requests = requests;
            }
        }

        public void PrepareJobs(List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            lock (_lock)
            {
                foreach (var region in _requests)
                {
                    int frameCount = _format.FramesFor(region.EndSeconds - region.StartSeconds);
                    if (region.StartFrame < 0 || frameCount <= 0)
                    {
                        continue;
                    }

                    region.Samples = new float[frameCount * _format.Channels];
                    jobs.Add((region.StartFrame, frameCount, region.Samples));
                }
            }
        }

        public void Analyze(CancellationToken token)
        {
            List<Region> requests;
            lock (_lock)
            {
                requests = _requests;
                _requests = new List<Region>();
            }

            foreach (var region in requests)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (region.Samples.Length == 0)
                {
                    continue;
                }

                region.Roots = Roots(region.Samples, _format);
                region.Samples = Array.Empty<float>();
                lock (_lock)
                {
                    _published.Add(region);
                }
            }
        }

        public int RootAt(double songSeconds)
        {
            lock (_lock)
            {
                foreach (var region in _published)
                {
                    if (songSeconds < region.StartSeconds || songSeconds >= region.EndSeconds)
                    {
                        continue;
                    }

                    int index = (int) ((songSeconds - region.StartSeconds) / HOP_SECONDS);
                    return index < region.Roots.Length ? region.Roots[index] : -1;
                }
            }

            return -1;
        }

        public static int[] Roots(float[] samples, BassStemFormat format)
        {
            int factor = Math.Max(1, format.Frequency / TARGET_RATE);
            int channels = format.Channels;
            var mono = new float[samples.Length / channels / factor];
            for (int index = 0; index < mono.Length; index++)
            {
                float sum = 0;
                int first = index * factor * channels;
                for (int sample = first; sample < first + factor * channels; sample++)
                {
                    sum += samples[sample];
                }

                mono[index] = sum / (factor * channels);
            }

            int rate = format.Frequency / factor;
            var detector = new BassSoloPitch(rate, MIN_FREQUENCY, MAX_FREQUENCY, WINDOW_SECONDS, HOP_SECONDS);
            var readings = new List<float>();
            detector.Detect(mono, readings);
            var pitchClasses = new int[readings.Count];
            for (int index = 0; index < readings.Count; index++)
            {
                pitchClasses[index] = readings[index] > 0f
                    ? (((int) Math.Round(69 + 12 * Math.Log(readings[index] / 440.0, 2)) % 12) + 12) % 12
                    : -1;
            }

            int holdHops = (int) Math.Round(HOLD_SECONDS / HOP_SECONDS);
            var roots = new int[pitchClasses.Length];
            var votes = new int[12];
            int lastRoot = -1;
            int lastRootIndex = int.MinValue;
            for (int index = 0; index < pitchClasses.Length; index++)
            {
                Array.Clear(votes, 0, votes.Length);
                int first = Math.Max(0, index - VOTE_RADIUS);
                int last = Math.Min(pitchClasses.Length - 1, index + VOTE_RADIUS);
                for (int neighbour = first; neighbour <= last; neighbour++)
                {
                    if (pitchClasses[neighbour] >= 0)
                    {
                        votes[pitchClasses[neighbour]]++;
                    }
                }

                int best = 0;
                for (int pitchClass = 1; pitchClass < 12; pitchClass++)
                {
                    if (votes[pitchClass] > votes[best])
                    {
                        best = pitchClass;
                    }
                }

                int fifthBelow = (best + 12 - FIFTH) % 12;
                if (votes[fifthBelow] * 2 >= votes[best])
                {
                    best = fifthBelow;
                }

                if (votes[best] >= MIN_VOTES)
                {
                    lastRoot = best;
                    lastRootIndex = index;
                }

                roots[index] = index - lastRootIndex <= holdHops ? lastRoot : -1;
            }

            return roots;
        }
    }
}
