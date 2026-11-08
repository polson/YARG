#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemStretchCandidate
    {
        public readonly int                    Key;
        public readonly BassStemSeed           Seed;
        public readonly float[]                Raw;
        public          int                    AttackFrames;
        public          BassSoloStretchTarget? Target;

        public BassStemStretchCandidate(int key, BassStemSeed seed, float[] raw)
        {
            Key = key;
            Seed = seed;
            Raw = raw;
        }
    }

    internal sealed class BassStemSeedJob
    {
        public readonly BassStemSoloSeeds              Seeds;
        public readonly List<BassStemStretchCandidate> Candidates = new();
        public          float[]                        Region = Array.Empty<float>();
        public          long                           RegionStartFrame;

        public BassStemSeedJob(BassStemSoloSeeds seeds)
        {
            Seeds = seeds;
        }

        public bool HasRegion => Region.Length > 0;
    }
}
