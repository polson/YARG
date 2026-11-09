#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoloSeeds
    {
        public readonly double                                StartTime;
        public readonly double                                EndTime;
        public readonly Dictionary<int, List<BassStemClip>>   ByKey = new();
        public readonly List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> Notes;
        public readonly List<(BassStemClip Clip, float[] Raw, int AttackFrames)> Candidates = new();
        public          float[]                                                  Region = Array.Empty<float>();
        public          long                                                     RegionStartFrame;

        private double _songSeconds;

        public BassStemSoloSeeds(double startTime, double endTime,
            List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> notes)
        {
            StartTime = startTime;
            EndTime = endTime;
            Notes = notes;
        }

        public bool IsEmpty => ByKey.Count == 0;

        public bool HasRegion => Region.Length > 0;

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
            if (ByKey.TryGetValue(key, out var clips))
            {
                clip = ClipAt(clips);
                return true;
            }

            clip = null!;
            return false;
        }

        public bool TryGetAnyClip(out BassStemClip clip)
        {
            foreach (var clips in ByKey.Values)
            {
                clip = ClipAt(clips);
                return true;
            }

            clip = null!;
            return false;
        }

        private BassStemClip ClipAt(List<BassStemClip> clips)
        {
            foreach (var clip in clips)
            {
                if (clip.SongSeconds >= _songSeconds)
                {
                    return clip;
                }
            }

            return clips[clips.Count - 1];
        }
    }
}
