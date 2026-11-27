using System.Collections.Generic;
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

        public void Record(IReadOnlyList<YargPlayer> slotAssignments)
        {
            _seats.Clear();
            for (int i = 0; i < slotAssignments.Count; i++)
            {
                if (slotAssignments[i] != null)
                {
                    _seats[slotAssignments[i]] = i;
                }
            }
        }

        public static void ComputeOrder(
            Dictionary<YargPlayer, int> seats,
            IReadOnlyList<YargPlayer> players,
            int joinSlotCount,
            List<YargPlayer> slotAssignments)
        {
            int count = players.Count + joinSlotCount;
            slotAssignments.Clear();
            for (int i = 0; i < count; i++)
            {
                slotAssignments.Add(null);
            }

            var unseated = new List<YargPlayer>();
            foreach (var player in players)
            {
                if (seats.TryGetValue(player, out int seat) && seat >= 0 && seat < count &&
                    slotAssignments[seat] == null)
                {
                    slotAssignments[seat] = player;
                }
                else
                {
                    unseated.Add(player);
                }
            }

            int next = 0;
            foreach (var player in unseated)
            {
                while (slotAssignments[next] != null)
                {
                    next++;
                }

                slotAssignments[next] = player;
            }
        }
    }
}
