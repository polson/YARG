using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YARG.Menu.Persistent;

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
            if (_session.IsOvershell)
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

        private BandColumn CreateColumnView(BandColumnModel model)
        {
            var column = Instantiate(_columnPrefab, _band);
            column.Initialize(this, _session, model);
            _columnViews[model] = column;
            RefreshProfilePickers(column);
            return column;
        }

        private BandColumn CreateAndRemapColumnView(BandColumnModel model, int replacedSlotIndex)
        {
            var column = CreateColumnView(model);
            if (replacedSlotIndex >= 0)
            {
                var target = FindJoinSlotAtSiblingIndex(replacedSlotIndex);
                if (target != null)
                {
                    column.transform.SetSiblingIndex(replacedSlotIndex);
                    _animator.RemapCard(_cardSnapshotBuffer, JoinCard(target), column.Card);
                    DestroyJoinSlot(target);
                }
            }

            return column;
        }

        private LayoutElement FindJoinSlotAtSiblingIndex(int siblingIndex)
        {
            foreach (var entry in _joinSlots)
            {
                if (entry.Slot.transform.GetSiblingIndex() == siblingIndex)
                {
                    return entry.Slot;
                }
            }

            return null;
        }

        internal bool MoveColumn(BandColumn column, int direction)
        {
            CaptureCardPositions();
            if (!_session.MoveColumn(column.Model, direction))
            {
                return false;
            }

            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: 0f, riseDelay: null);
            return true;
        }

        private void RemoveColumnView(BandColumn column)
        {
            _columns.Remove(column);
            _columnViews.Remove(column.Model);
            _animator.KillTweens(column.Card);
            column.DismissAndDestroy();

            RefreshProfilePickers(column);
            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: SLIDE_SECONDS,
                riseDelay: SLIDE_SECONDS);
        }

        private void CaptureCardPositions()
        {
            _animator.Capture(_columns, _joinSlots, _cardSnapshotBuffer);
        }

        private void ShowFooterBackground(bool show)
        {
            if (_session.IsOvershell)
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
