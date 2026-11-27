using System.Collections.Generic;
using UnityEngine;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandSeatTracker
    {
        private readonly Dictionary<YargPlayer, int> _seats = new();

        public Dictionary<YargPlayer, int> Snapshot()
        {
            return new Dictionary<YargPlayer, int>(_seats);
        }

        public void Remove(YargPlayer player)
        {
            _seats.Remove(player);
        }

        public void Record(IReadOnlyList<BandColumn> columns, IReadOnlyList<Transform> orderedSlots)
        {
            _seats.Clear();
            for (int i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                int seat = -1;
                for (int s = 0; s < orderedSlots.Count; s++)
                {
                    if (orderedSlots[s] == column.transform)
                    {
                        seat = s;
                        break;
                    }
                }

                _seats[column.Player] = seat;
            }
        }

        public void Apply(
            Dictionary<YargPlayer, int> seats,
            List<BandColumn> columns,
            IReadOnlyList<Transform> joinSlotTransforms)
        {
            int count = columns.Count + joinSlotTransforms.Count;
            var seated = new Transform[count];
            var unseated = new List<BandColumn>();

            for (int i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                if (seats.TryGetValue(column.Player, out int seat) && seat >= 0 && seat < count && seated[seat] == null)
                {
                    seated[seat] = column.transform;
                }
                else
                {
                    unseated.Add(column);
                }
            }

            int next = 0;
            for (int i = 0; i < unseated.Count; i++)
            {
                while (seated[next] != null)
                {
                    next++;
                }

                seated[next] = unseated[i].transform;
            }

            int joinIndex = 0;
            for (int i = 0; i < count; i++)
            {
                if (seated[i] == null)
                {
                    seated[i] = joinSlotTransforms[joinIndex++];
                }

                seated[i].SetAsLastSibling();
            }

            columns.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
        }
    }
}
