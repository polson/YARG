#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSeedLibrary : IBassStemClipSource
    {
        // Lock order: the stem lock (BassStemReplay.Lock) is always taken before this one.
        private readonly object                _lock = new();
        private readonly BassStemSeedRenderer  _renderer;
        private readonly Random                _random = new();
        private          List<BassStemSeedSet>   _requests  = new();
        private          List<BassStemSeedJob>   _pending   = new();
        private          List<BassStemSoloSeeds> _published = new();
        private          BassStemSoloSeeds?      _selected;
        private          double                  _requestedSoloTime;

        public BassStemSeedLibrary(BassStemSeedRenderer renderer)
        {
            _renderer = renderer;
        }

        public bool IsSelected
        {
            get
            {
                lock (_lock)
                {
                    return _selected != null;
                }
            }
        }

        public void Request(List<BassStemSeedSet> seedSets)
        {
            lock (_lock)
            {
                _requests = seedSets;
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

        public void ClearSelection()
        {
            lock (_lock)
            {
                _selected = null;
            }
        }

        public void Forget()
        {
            lock (_lock)
            {
                _requests = new List<BassStemSeedSet>();
                _pending = new List<BassStemSeedJob>();
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

                if (key != GuitarEngine.OPEN_MASK)
                {
                    return selected.TryGetClip(key, out clip);
                }

                return TryGetRandomFretClip(selected, out clip) || selected.TryGetAnyClip(out clip);
            }
        }

        public bool TryGetFretClip(int key, out BassStemClip clip)
        {
            lock (_lock)
            {
                var selected = _selected;
                if (selected != null)
                {
                    for (int fret = 1; fret <= key; fret <<= 1)
                    {
                        if ((key & fret) != 0 && selected.TryGetClip(fret, out clip!))
                        {
                            return true;
                        }
                    }
                }

                clip = null!;
                return false;
            }
        }

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            lock (_lock)
            {
                var selected = _selected;
                if (selected != null)
                {
                    return selected.TryGetAnyClip(out clip);
                }

                clip = null!;
                return false;
            }
        }

        public void PrepareJobs(List<BassStemSampleJob> jobs)
        {
            lock (_lock)
            {
                _pending = _renderer.BuildJobs(_requests, jobs);
            }
        }

        public void Publish(CancellationToken token)
        {
            List<BassStemSeedJob> pending;
            double requestedSoloTime;
            lock (_lock)
            {
                pending = _pending;
                _pending = new List<BassStemSeedJob>();
                requestedSoloTime = _requestedSoloTime;
            }

            var published = new List<BassStemSoloSeeds>(pending.Count);
            while (pending.Count > 0)
            {
                var job = TakeNext(pending, requestedSoloTime);
                if (!_renderer.Prepare(job, token))
                {
                    return;
                }

                if (!_renderer.Stretch(job, token))
                {
                    return;
                }

                published.Add(job.Seeds);
            }

            lock (_lock)
            {
                _published.Clear();
                _published.AddRange(published);
                _selected = null;
            }
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

        private static BassStemSeedJob TakeNext(List<BassStemSeedJob> queue, double soloTime)
        {
            int index = Math.Max(queue.FindIndex(job => job.Seeds.Covers(soloTime)), 0);
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
