#nullable enable
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSeed
    {
        public readonly double       SongSeconds;
        public readonly int          SustainStartFrame;
        public readonly long         StartFrame;
        public          BassStemClip Clip;

        public BassStemSeed(double songSeconds, BassStemClip clip, int sustainStartFrame, long startFrame)
        {
            SongSeconds = songSeconds;
            Clip = clip;
            SustainStartFrame = sustainStartFrame;
            StartFrame = startFrame;
        }
    }

    internal sealed class BassStemSoloSeeds
    {
        public readonly double                                StartTime;
        public readonly double                                EndTime;
        public readonly Dictionary<int, List<BassStemSeed>>   ByKey = new();

        private double _songSeconds;

        public BassStemSoloSeeds(double startTime, double endTime)
        {
            StartTime = startTime;
            EndTime = endTime;
        }

        public bool IsEmpty => ByKey.Count == 0;

        public bool Covers(double soloTime)
        {
            return soloTime >= StartTime && soloTime <= EndTime;
        }

        public void Select(double songSeconds)
        {
            _songSeconds = songSeconds;
        }

        public bool TryGetClip(int key, out BassStemClip clip)
        {
            if (ByKey.TryGetValue(key, out var seeds))
            {
                clip = ClipAt(seeds);
                return true;
            }

            clip = null!;
            return false;
        }

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            foreach (var seeds in ByKey.Values)
            {
                clip = ClipAt(seeds);
                return true;
            }

            clip = null!;
            return false;
        }

        private BassStemClip ClipAt(List<BassStemSeed> seeds)
        {
            foreach (var seed in seeds)
            {
                if (seed.SongSeconds >= _songSeconds)
                {
                    return seed.Clip;
                }
            }

            return seeds[seeds.Count - 1].Clip;
        }
    }
}
