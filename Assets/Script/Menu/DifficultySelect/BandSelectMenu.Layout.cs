using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YARG.Menu.Persistent;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu
    {
        internal float MaxCardHeight => _band.rect.height;

        private void RefitOnBandResize()
        {
            float height = MaxCardHeight;
            if (Mathf.Approximately(height, _fittedBandHeight))
            {
                return;
            }

            _fittedBandHeight = height;
            for (int i = 0; i < _columns.Count; i++)
            {
                _columns[i].FitRows();
            }

            if (_profilesInUse != null)
            {
                _profilesInUse.FitRows();
            }
        }

        private void UpdateSortingOrder()
        {
            if (_bandCanvas == null)
            {
                if (!_band.TryGetComponent(out _bandCanvas))
                {
                    return;
                }

                _bandSortingOrder = _bandCanvas.sortingOrder;
            }

            bool raise = false;
            if (IsOvershell)
            {
                raise = _profilesInUse != null;
                for (int i = 0; !raise && i < _columns.Count; i++)
                {
                    if (_columns[i].IsExpanded)
                    {
                        raise = true;
                    }
                }
            }

            _bandCanvas.sortingOrder = raise ? OPEN_CARD_SORTING_ORDER : _bandSortingOrder;
        }

        private BandColumn InsertColumn(YargPlayer player, Dictionary<RectTransform, (float Position, float Width)> before,
            LayoutElement target = null)
        {
            var column = Instantiate(_columnPrefab, _band);
            column.Initialize(this, player);

            if (target == null)
            {
                target = EarliestJoinSlot();
            }

            if (target != null)
            {
                column.transform.SetSiblingIndex(target.transform.GetSiblingIndex());
                _animator.RemapCard(before, JoinCard(target), column.Card);
                DestroyJoinSlot(target);
            }

            int siblingIndex = column.transform.GetSiblingIndex();
            int index = 0;
            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].transform.GetSiblingIndex() < siblingIndex)
                {
                    index++;
                }
            }

            _columns.Insert(index, column);
            if (_columns.Count > 1 && index <= _focusIndex)
            {
                _focusIndex++;
            }

            RefreshProfilePickers(column);
            return column;
        }

        internal bool CanMoveColumn(BandColumn column, int direction)
        {
            return SlotBeside(column, direction) != null;
        }

        private Transform SlotBeside(BandColumn column, int direction)
        {
            var slots = OrderedSlots();
            int index = slots.IndexOf(column.transform);
            int target = index + direction;
            return index >= 0 && target >= 0 && target < slots.Count ? slots[target] : null;
        }

        internal bool MoveColumn(BandColumn column, int direction)
        {
            var other = SlotBeside(column, direction);
            if (other == null)
            {
                return false;
            }

            var focused = FocusedColumn;
            CaptureCardPositions();

            int siblingIndex = column.transform.GetSiblingIndex();
            column.transform.SetSiblingIndex(other.GetSiblingIndex());
            other.SetSiblingIndex(siblingIndex);

            _columns.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
            _focusIndex = _columns.IndexOf(focused);

            OnColumnChanged();
            PlayerContainer.SaveProfiles();
            _animator.ShiftCards(_band, _columns, _joinCards, _cardSnapshotBuffer, delay: 0f, riseDelay: null);

            for (int i = 0; i < _columns.Count; i++)
            {
                var otherColumn = _columns[i];
                if (otherColumn != column && otherColumn.State == BandColumnState.Position)
                {
                    otherColumn.Rebuild();
                }
            }

            return true;
        }

        private List<Transform> OrderedSlots()
        {
            _slotOrderBuffer.Clear();
            foreach (Transform child in _band)
            {
                if (IsManagedSlot(child))
                {
                    _slotOrderBuffer.Add(child);
                }
            }

            return _slotOrderBuffer;
        }

        private bool IsManagedSlot(Transform child)
        {
            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].transform == child)
                {
                    return true;
                }
            }

            for (int i = 0; i < _joinSlots.Count; i++)
            {
                if (_joinSlots[i].Slot.transform == child)
                {
                    return true;
                }
            }

            return false;
        }

        private void RemoveColumn(BandColumn column)
        {
            CaptureCardPositions();
            int siblingIndex = column.transform.GetSiblingIndex();

            _columns.Remove(column);
            _joins.Remove(column);
            _animator.KillTweens(column.Card);
            column.DismissAndDestroy();

            if (_columns.Count + 1 < MIN_SLOTS)
            {
                CreateJoinSlot(siblingIndex);
            }

            RefreshProfilePickers(column);
            OnColumnChanged();
            _animator.ShiftCards(_band, _columns, _joinCards, _cardSnapshotBuffer, delay: SLIDE_SECONDS,
                riseDelay: SLIDE_SECONDS);
        }

        private void CaptureCardPositions()
        {
            _animator.Capture(_columns, _joinCards, _cardSnapshotBuffer);
        }

        private void ShowFooterBackground(bool show)
        {
            if (IsOvershell)
            {
                return;
            }

            _footerBackground.gameObject.SetActive(show);

            if (!show || HelpBar.Instance == null)
            {
                return;
            }

            var helpBar = (RectTransform) HelpBar.Instance.transform;
            _footerBackground.sizeDelta = new Vector2(_footerBackground.sizeDelta.x, helpBar.rect.height);
        }

        private float GetColumnWidth(int slots)
        {
            float spacing = _bandLayout != null ? _bandLayout.spacing : 0f;
            float available = _band.rect.width - spacing * (slots - 1);
            return Mathf.Min(MAX_COLUMN_WIDTH, available / slots);
        }
    }
}
