#nullable enable
using System.Collections.Generic;
using YARG.Core.Chart;

namespace YARG.Audio.BASS
{
    internal static class BassSoloExtensions
    {
        public static List<(uint StartTick, uint EndTick)> SoloSpans(this List<GuitarNote> notes)
        {
            var spans = new List<(uint StartTick, uint EndTick)>();
            int start = -1;
            for (int index = 0; index < notes.Count; index++)
            {
                var note = notes[index];
                if (!note.IsSolo)
                {
                    start = -1;
                    continue;
                }

                if (start < 0)
                {
                    start = index;
                }

                bool ends = note.IsSoloEnd || index + 1 == notes.Count || !notes[index + 1].IsSolo;
                if (ends)
                {
                    spans.Add((notes[start].Tick, note.Tick));
                    start = -1;
                }
            }

            return spans;
        }

        /// <summary>
        /// The two derived properties of a chart note that the stem layer needs: how long its clip runs
        /// and whether it holds.
        /// </summary>
        public static double ClipDuration(this GuitarNote note)
        {
            return note.NextNote != null ? note.NextNote.Time - note.Time : double.MaxValue;
        }

        public static bool HasSustain(this GuitarNote note)
        {
            foreach (var child in note.AllNotes)
            {
                if (child.IsSustain)
                {
                    return true;
                }
            }

            return false;
        }

        public static T SortedMedian<T>(this List<T> values, T fallback)
        {
            if (values.Count == 0)
            {
                return fallback;
            }

            values.Sort();
            return values[values.Count / 2];
        }
    }
}
