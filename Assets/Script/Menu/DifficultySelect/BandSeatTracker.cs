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
            foreach (var column in columns)
            {
                _seats[column.Player] = FindSlotIndex(column.transform, orderedSlots);
            }
        }

        public void Apply(
            Dictionary<YargPlayer, int> seats,
            List<BandColumn> columns,
            IReadOnlyList<Transform> joinSlotTransforms)
        {
            int count = columns.Count + joinSlotTransforms.Count;
            var seated = new Transform[count];
            var unseated = AssignExistingSeats(seats, columns, seated);

            AssignRemainingColumns(unseated, seated);
            ApplySiblingOrder(seated, joinSlotTransforms);

            columns.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
        }

        private static int FindSlotIndex(Transform target, IReadOnlyList<Transform> orderedSlots)
        {
            for (int i = 0; i < orderedSlots.Count; i++)
            {
                if (orderedSlots[i] == target)
                {
                    return i;
                }
            }

            return -1;
        }

        private static List<BandColumn> AssignExistingSeats(
            Dictionary<YargPlayer, int> seats,
            IReadOnlyList<BandColumn> columns,
            Transform[] seated)
        {
            var unseated = new List<BandColumn>();
            foreach (var column in columns)
            {
                if (seats.TryGetValue(column.Player, out int seat) && seat >= 0 && seat < seated.Length &&
                    seated[seat] == null)
                {
                    seated[seat] = column.transform;
                }
                else
                {
                    unseated.Add(column);
                }
            }

            return unseated;
        }

        private static void AssignRemainingColumns(IReadOnlyList<BandColumn> unseated, Transform[] seated)
        {
            int next = 0;
            foreach (var column in unseated)
            {
                while (seated[next] != null)
                {
                    next++;
                }

                seated[next] = column.transform;
            }
        }

        private static void ApplySiblingOrder(Transform[] seated, IReadOnlyList<Transform> joinSlotTransforms)
        {
            int joinIndex = 0;
            for (int i = 0; i < seated.Length; i++)
            {
                if (seated[i] == null)
                {
                    seated[i] = joinSlotTransforms[joinIndex++];
                }

                seated[i].SetAsLastSibling();
            }
        }
    }
}
