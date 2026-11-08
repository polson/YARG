#nullable enable
using System;
using System.Collections.Generic;
using YARG.Core.Chart;
using YARG.Core.Engine;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Turns the engine's solo phrases into the seed sets the stem soundboard renders from.
    /// </summary>
    internal static class BassStemSeedSetBuilder
    {
        public static List<BassStemSeedSet> Build(IReadOnlyList<SoloSection> phrases,
            List<GuitarNote> notes, Func<double, double> playbackTime)
        {
            var seedSets = new List<BassStemSeedSet>(phrases.Count);

            foreach (var phrase in phrases)
            {
                var seedNotes = new List<BassStemSeedNote>();
                foreach (var note in notes)
                {
                    if (note.Tick < phrase.StartTick || note.Tick > phrase.EndTick)
                    {
                        continue;
                    }

                    seedNotes.Add(new BassStemSeedNote(key: note.NoteMask, songSeconds: note.Time,
                        playbackSeconds: playbackTime(note.Time),
                        durationSeconds: BassStemNote.ClipDuration(note), sustainStartSeconds: 0,
                        isSustain: BassStemNote.HasSustain(note)));
                }

                if (seedNotes.Count == 0)
                {
                    continue;
                }

                seedSets.Add(new BassStemSeedSet(seedNotes[0].SongSeconds, seedNotes[^1].SongSeconds, seedNotes));
            }

            return seedSets;
        }
    }
}
