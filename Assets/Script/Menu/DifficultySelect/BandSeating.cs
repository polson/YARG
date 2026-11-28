using System;
using System.Collections.Generic;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// Owns where each band column sits: the ordered slots (a null slot is an empty join slot), which column has
    /// focus, and the seats players return to the next time the band opens.
    /// </summary>
    internal sealed class BandSeating
    {
        public const int MIN_SLOTS = 4;

        private static readonly Dictionary<YargPlayer, int> _savedSeats = new();

        private readonly List<BandColumnModel> _slots = new();

        public IReadOnlyList<BandColumnModel> Slots => _slots;
        public int FocusIndex { get; private set; }

        public BandColumnModel FocusedColumn
        {
            get
            {
                int count = OccupiedCount;
                return count > 0 ? GetOccupiedColumn(Math.Clamp(FocusIndex, 0, count - 1)) : null;
            }
        }

        public int OccupiedCount
        {
            get
            {
                int count = 0;
                foreach (var slot in _slots)
                {
                    if (slot != null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Seats the given columns, putting each player back in their saved seat when it is still free.
        /// </summary>
        public void SeatColumns(IReadOnlyList<BandColumnModel> columns)
        {
            FocusIndex = 0;
            var savedSeats = new Dictionary<YargPlayer, int>(_savedSeats);
            int joinSlotCount = Math.Max(MIN_SLOTS, columns.Count + 1) - columns.Count;
            int slotCount = columns.Count + joinSlotCount;

            _slots.Clear();
            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                _slots.Add(null);
            }

            var unseatedColumns = new List<BandColumnModel>();
            foreach (var column in columns)
            {
                bool hasFreeSavedSeat = savedSeats.TryGetValue(column.Player, out int savedSlotIndex)
                    && savedSlotIndex >= 0
                    && savedSlotIndex < slotCount
                    && _slots[savedSlotIndex] == null;
                if (hasFreeSavedSeat)
                {
                    _slots[savedSlotIndex] = column;
                }
                else
                {
                    unseatedColumns.Add(column);
                }
            }

            int nextFreeSlotIndex = 0;
            foreach (var column in unseatedColumns)
            {
                while (_slots[nextFreeSlotIndex] != null)
                {
                    nextFreeSlotIndex++;
                }

                _slots[nextFreeSlotIndex] = column;
            }
        }

        /// <summary>
        /// Empties every slot.
        /// </summary>
        public void Clear()
        {
            _slots.Clear();
        }

        /// <summary>
        /// Trims or pads empty join slots, remembers every player's seat, and keeps focus on an existing column.
        /// </summary>
        public void Settle(bool keepEmptyJoinSlot)
        {
            int columnCount = OccupiedCount;
            int emptyJoinSlotCount = keepEmptyJoinSlot ? 1 : 0;
            AdjustJoinSlotsToCount(Math.Max(MIN_SLOTS, columnCount + emptyJoinSlotCount));
            SaveSeats();
            FocusIndex = Math.Clamp(FocusIndex, 0, Math.Max(0, columnCount - 1));
        }

        /// <summary>
        /// Forgets a player's saved seat.
        /// </summary>
        public void ForgetSeat(YargPlayer player)
        {
            _savedSeats.Remove(player);
        }

        /// <summary>
        /// Returns true if the slot index points at an empty join slot.
        /// </summary>
        public bool IsEmptySlot(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < _slots.Count && _slots[slotIndex] == null;
        }

        /// <summary>
        /// Returns the column of the given player, or null.
        /// </summary>
        public BandColumnModel Find(YargPlayer player)
        {
            if (player == null)
            {
                return null;
            }

            foreach (var column in _slots)
            {
                if (column != null && column.Player == player)
                {
                    return column;
                }
            }

            return null;
        }

        /// <summary>
        /// Focuses a column. Returns false if it is not seated or already focused.
        /// </summary>
        public bool Focus(BandColumnModel column)
        {
            int columnIndex = IndexOfOccupiedColumn(column);
            if (columnIndex < 0 || columnIndex == FocusIndex)
            {
                return false;
            }

            FocusIndex = columnIndex;
            return true;
        }

        /// <summary>
        /// Moves focus left or right. Returns false if focus did not change.
        /// </summary>
        public bool MoveFocus(int direction)
        {
            int count = OccupiedCount;
            if (count == 0)
            {
                return false;
            }

            int nextFocusIndex = Math.Clamp(FocusIndex + direction, 0, count - 1);
            if (nextFocusIndex == FocusIndex)
            {
                return false;
            }

            FocusIndex = nextFocusIndex;
            return true;
        }

        /// <summary>
        /// Returns true if the column can swap with the slot next to it in the given direction.
        /// </summary>
        public bool CanMove(BandColumnModel column, int direction)
        {
            int currentSlotIndex = _slots.IndexOf(column);
            int targetSlotIndex = currentSlotIndex + direction;
            return currentSlotIndex >= 0 && targetSlotIndex >= 0 && targetSlotIndex < _slots.Count;
        }

        /// <summary>
        /// Swaps the column with the slot next to it, keeping focus on the same column.
        /// </summary>
        public bool Move(BandColumnModel column, int direction)
        {
            if (!CanMove(column, direction))
            {
                return false;
            }

            int currentSlotIndex = _slots.IndexOf(column);
            int targetSlotIndex = currentSlotIndex + direction;

            var focusedColumn = FocusedColumn;
            (_slots[currentSlotIndex], _slots[targetSlotIndex]) =
                (_slots[targetSlotIndex], _slots[currentSlotIndex]);
            FocusIndex = focusedColumn != null ? Math.Max(0, IndexOfOccupiedColumn(focusedColumn)) : 0;
            return true;
        }

        /// <summary>
        /// Seats a new column in the target slot if it is empty, otherwise the first empty slot, otherwise at the
        /// end. Returns the replaced join slot index, or -1 if the column was added at the end.
        /// </summary>
        public int Insert(BandColumnModel model, int targetSlotIndex)
        {
            int replacedSlotIndex = PlaceColumnInSlot(model, targetSlotIndex);
            int columnIndex = IndexOfOccupiedColumn(model);
            if (OccupiedCount > 1 && columnIndex <= FocusIndex)
            {
                FocusIndex++;
            }

            return replacedSlotIndex;
        }

        /// <summary>
        /// Removes a column, leaving an empty join slot behind while the band has fewer than the minimum slots.
        /// Returns the slot index it was in, or -1.
        /// </summary>
        public int Remove(BandColumnModel column)
        {
            int slotIndex = _slots.IndexOf(column);
            if (slotIndex < 0)
            {
                return slotIndex;
            }

            int remainingColumns = OccupiedCount - 1;
            if (remainingColumns + 1 < MIN_SLOTS)
            {
                _slots[slotIndex] = null;
            }
            else
            {
                _slots.RemoveAt(slotIndex);
            }

            return slotIndex;
        }

        private BandColumnModel GetOccupiedColumn(int targetColumnIndex)
        {
            int columnIndex = 0;
            foreach (var slot in _slots)
            {
                if (slot == null)
                {
                    continue;
                }

                if (columnIndex == targetColumnIndex)
                {
                    return slot;
                }

                columnIndex++;
            }

            return null;
        }

        private int IndexOfOccupiedColumn(BandColumnModel target)
        {
            int columnIndex = 0;
            foreach (var slot in _slots)
            {
                if (slot == null)
                {
                    continue;
                }

                if (slot == target)
                {
                    return columnIndex;
                }

                columnIndex++;
            }

            return -1;
        }

        private int PlaceColumnInSlot(BandColumnModel model, int targetSlotIndex)
        {
            if (IsEmptySlot(targetSlotIndex))
            {
                _slots[targetSlotIndex] = model;
                return targetSlotIndex;
            }

            for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                if (_slots[slotIndex] == null)
                {
                    _slots[slotIndex] = model;
                    return slotIndex;
                }
            }

            _slots.Add(model);
            return -1;
        }

        private void SaveSeats()
        {
            _savedSeats.Clear();
            for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                if (_slots[slotIndex] != null)
                {
                    _savedSeats[_slots[slotIndex].Player] = slotIndex;
                }
            }
        }

        private void AdjustJoinSlotsToCount(int targetSlotCount)
        {
            while (_slots.Count > targetSlotCount)
            {
                int lastEmptySlotIndex = _slots.LastIndexOf(null);
                if (lastEmptySlotIndex < 0)
                {
                    break;
                }

                _slots.RemoveAt(lastEmptySlotIndex);
            }

            while (_slots.Count < targetSlotCount)
            {
                _slots.Add(null);
            }
        }
    }
}
