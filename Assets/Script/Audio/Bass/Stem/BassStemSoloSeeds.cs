#nullable enable
using System;
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemSoloSeeds
    {
        public const int FRET_COUNT = 5;

        public readonly double                                StartTime;
        public readonly double                                EndTime;
        public readonly Dictionary<int, List<BassStemClip>>   ByKey = new();
        public readonly List<BassStemClip>                    Ladder = new();
        public readonly List<(double SongSeconds, int Start)> Windows = new();
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
            if (Ladder.Count > 0)
            {
                for (int fret = 0; fret < FRET_COUNT; fret++)
                {
                    if (key == 1 << fret)
                    {
                        clip = Ladder[WindowStart() + fret];
                        return true;
                    }
                }
            }

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

        private int WindowStart()
        {
            int start = Windows[0].Start;
            foreach (var window in Windows)
            {
                if (window.SongSeconds > _songSeconds)
                {
                    break;
                }

                start = window.Start;
            }

            return start;
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
