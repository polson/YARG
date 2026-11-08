#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSeedJob
    {
        public readonly BassStemSoloSeeds Seeds;
        public readonly List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> Notes;
        public readonly List<(BassStemSeed Seed, float[] Raw, int AttackFrames)> Candidates = new();
        public          float[]                                                  Region = Array.Empty<float>();
        public          long                                                     RegionStartFrame;

        public BassStemSeedJob(BassStemSoloSeeds seeds,
            List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> notes)
        {
            Seeds = seeds;
            Notes = notes;
        }

        public bool HasRegion => Region.Length > 0;
    }
}
