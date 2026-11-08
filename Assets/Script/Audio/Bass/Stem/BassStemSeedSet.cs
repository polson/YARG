#nullable enable
using System.Collections.Generic;

namespace YARG.Audio.BASS
{
    internal readonly struct BassStemSeedNote
    {
        public readonly int    Key;
        public readonly double SongSeconds;
        public readonly double PlaybackSeconds;
        public readonly double DurationSeconds;
        public readonly double SustainStartSeconds;
        public readonly bool   IsSustain;

        public BassStemSeedNote(int key, double songSeconds, double playbackSeconds, double durationSeconds,
            double sustainStartSeconds, bool isSustain)
        {
            Key = key;
            SongSeconds = songSeconds;
            PlaybackSeconds = playbackSeconds;
            DurationSeconds = durationSeconds;
            SustainStartSeconds = sustainStartSeconds;
            IsSustain = isSustain;
        }
    }

    internal sealed class BassStemSeedSet
    {
        public readonly double                 StartTime;
        public readonly double                 EndTime;
        public readonly List<BassStemSeedNote> Notes;

        public BassStemSeedSet(double startTime, double endTime, List<BassStemSeedNote> notes)
        {
            StartTime = startTime;
            EndTime = endTime;
            Notes = notes;
        }

        public bool Covers(double soloTime)
        {
            return soloTime >= StartTime && soloTime <= EndTime;
        }
    }
}
