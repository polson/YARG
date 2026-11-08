#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using YARG.Core.Engine.Guitar;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSeedLibrary : IBassStemClipSource
    {
        private readonly object                _lock;
        private readonly BassStemSeedRenderer  _renderer;
        private readonly Random                _random = new();
        private          List<BassStemSeedSet>   _requests  = new();
        private          List<BassStemSeedJob>   _pending   = new();
        private          List<BassStemSoloSeeds> _published = new();
        private          BassStemSoloSeeds?      _selected;
        private          double                  _requestedSoloTime;

        public BassStemSeedLibrary(object lockObject, BassStemSeedRenderer renderer)
        {
            _lock = lockObject;
            _renderer = renderer;
        }

        public bool IsActive => _selected != null;

        public void Request(List<BassStemSeedSet> seedSets)
        {
            _requests = seedSets;
        }

        public void Select(double soloTime, double songTime)
        {
            _requestedSoloTime = soloTime;
            _selected = Find(soloTime);
            _selected?.Select(songTime);
        }

        public void ClearSelection()
        {
            _selected = null;
        }

        public bool HasSeeds(double soloTime)
        {
            var seeds = Find(soloTime);
            return seeds != null && !seeds.IsEmpty;
        }

        public bool TryGetClip(int key, out BassStemClip clip)
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

        public bool TryGetFretClip(int key, out BassStemClip clip)
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

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            var selected = _selected;
            if (selected != null)
            {
                return selected.TryGetAnyClip(out clip);
            }

            clip = null!;
            return false;
        }

        public void PrepareJobs(List<BassStemSampleJob> jobs)
        {
            _pending = _renderer.BuildJobs(_requests, jobs);
        }

        public void Publish(CancellationToken token)
        {
            List<BassStemSeedJob> pending;
            double requestedSoloTime;
            lock (_lock)
            {
                _published.Clear();
                _selected = null;
                pending = _pending;
                _pending = new List<BassStemSeedJob>();
                requestedSoloTime = _requestedSoloTime;
            }

            while (pending.Count > 0)
            {
                var job = TakeNext(pending, requestedSoloTime);
                if (!_renderer.Prepare(job, token))
                {
                    return;
                }

                lock (_lock)
                {
                    _published.Add(job.Seeds);
                }

                if (!_renderer.Stretch(job, token))
                {
                    return;
                }
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
