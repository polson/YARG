#nullable enable
using YARG.Core.Chart;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// The two derived properties of a chart note that the stem layer needs: how long its clip runs
    /// and whether it holds.
    /// </summary>
    internal static class BassStemNote
    {
        public static double ClipDuration(GuitarNote note)
        {
            return note.NextNote != null ? note.NextNote.Time - note.Time : double.MaxValue;
        }

        public static bool HasSustain(GuitarNote note)
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
    }
}
