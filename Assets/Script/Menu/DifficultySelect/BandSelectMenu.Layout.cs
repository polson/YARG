using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Gameplay.Visuals;
using YARG.Helpers;
using YARG.Input;
using YARG.Input.Bindings;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu : MonoBehaviour
    {
        private void RefitOnBandResize()
        {
            float height = MaxCardHeight;
            if (Mathf.Approximately(height, _fittedBandHeight))
            {
                return;
            }

            _fittedBandHeight = height;
            foreach (var column in _columns)
            {
                column.FitRows();
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

            bool raise = IsOvershell && (_profilesInUse != null || _columns.Any(c => c.IsExpanded));
            _bandCanvas.sortingOrder = raise ? OPEN_CARD_SORTING_ORDER : _bandSortingOrder;
        }

        private BandColumn InsertColumn(YargPlayer player, Dictionary<RectTransform, CardSnapshot> before,
            LayoutElement target = null)
        {
            var column = Instantiate(_columnPrefab, _band);
            column.Initialize(this, player);

            target ??= _joinSlots.OrderBy(slot => slot.transform.GetSiblingIndex()).FirstOrDefault();
            if (target is not null)
            {
                column.transform.SetSiblingIndex(target.transform.GetSiblingIndex());
                if (before is not null && before.Remove(JoinCard(target), out var snapshot))
                {
                    before[column.Card] = snapshot;
                }

                DestroyJoinSlot(target);
            }

            int siblingIndex = column.transform.GetSiblingIndex();
            int index = _columns.Count(c => c.transform.GetSiblingIndex() < siblingIndex);
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
            bool inRange = index >= 0 && target >= 0 && target < slots.Count;
            return inRange ? slots[target] : null;
        }

        internal bool MoveColumn(BandColumn column, int direction)
        {
            var other = SlotBeside(column, direction);
            if (other == null)
            {
                return false;
            }

            var focused = FocusedColumn;
            var before = CaptureCardPositions();

            int siblingIndex = column.transform.GetSiblingIndex();
            column.transform.SetSiblingIndex(other.GetSiblingIndex());
            other.SetSiblingIndex(siblingIndex);

            _columns.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
            _focusIndex = _columns.IndexOf(focused);

            OnColumnChanged();
            PlayerContainer.SaveProfiles();
            ShiftCards(before, delay: 0f, riseDelay: null);

            foreach (var c in _columns)
            {
                if (c != column && c.State == BandColumnState.Position)
                {
                    c.Rebuild();
                }
            }

            return true;
        }

        private List<Transform> OrderedSlots()
        {
            var slots = new List<Transform>();
            foreach (Transform child in _band)
            {
                if (_columns.Any(c => c.transform == child) || _joinSlots.Any(s => s.transform == child))
                {
                    slots.Add(child);
                }
            }

            return slots;
        }

        private void RemoveColumn(BandColumn column)
        {
            var before = CaptureCardPositions();
            int siblingIndex = column.transform.GetSiblingIndex();

            _columns.Remove(column);
            _joins.Remove(column);
            _shifts.Remove(column.Card);
            _resizes.Remove(column.Card);
            column.DismissAndDestroy();

            if (_columns.Count + 1 < MIN_SLOTS)
            {
                CreateJoinSlot(siblingIndex);
            }

            RefreshProfilePickers(column);
            OnColumnChanged();
            ShiftCards(before, delay: SLIDE_SECONDS, riseDelay: SLIDE_SECONDS);
        }

        private Dictionary<RectTransform, CardSnapshot> CaptureCardPositions()
        {
            var positions = new Dictionary<RectTransform, CardSnapshot>();
            foreach (var column in _columns)
            {
                positions[column.Card] = new CardSnapshot(CardPosition(column.Card), column.Card.sizeDelta.x);
            }

            foreach (var slot in _joinSlots)
            {
                var card = JoinCard(slot);
                positions[card] = new CardSnapshot(CardPosition(card), card.sizeDelta.x);
            }

            return positions;
        }

        private void ShiftCards(Dictionary<RectTransform, CardSnapshot> before, float delay, float? riseDelay)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_band);

            foreach (var column in _columns)
            {
                ShiftCard(column.Card, before, delay);
            }

            foreach (var slot in _joinSlots)
            {
                var card = JoinCard(slot);
                if (before.ContainsKey(card))
                {
                    ShiftCard(card, before, delay);
                }
                else if (riseDelay is { } rise)
                {
                    card.DOKill();
                    card.anchoredPosition = new Vector2(0f, -SLIDE_DISTANCE);
                    card.DOAnchorPosY(0f, SLIDE_SECONDS)
                        .SetDelay(rise)
                        .SetEase(Ease.OutCubic)
                        .SetUpdate(true);
                }
            }
        }

        private void ShiftCard(RectTransform card, Dictionary<RectTransform, CardSnapshot> before, float delay)
        {
            if (_shifts.Remove(card, out var running))
            {
                running.Kill();
            }

            bool captured = before.TryGetValue(card, out var snapshot);
            if (captured)
            {
                ResizeCard(card, snapshot.Width, delay);
            }

            float offset = captured ? snapshot.Position - SlotPosition(card) : 0f;

            bool settled = Mathf.Abs(offset) < 0.5f;
            card.anchoredPosition = new Vector2(settled ? 0f : offset, card.anchoredPosition.y);
            if (settled)
            {
                return;
            }

            _shifts[card] = card.DOAnchorPosX(0f, SLIDE_SECONDS)
                .SetDelay(delay)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true);
        }

        private void ResizeCard(RectTransform card, float from, float delay)
        {
            if (_resizes.Remove(card, out var running))
            {
                running.Kill();
            }

            float to = card.sizeDelta.x;
            if (Mathf.Abs(to - from) < 0.5f)
            {
                return;
            }

            card.sizeDelta = new Vector2(from, card.sizeDelta.y);
            _resizes[card] = DOTween.To(() => card.sizeDelta.x, x => card.sizeDelta = new Vector2(x, card.sizeDelta.y),
                    to, SLIDE_SECONDS)
                .SetDelay(delay)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .SetLink(card.gameObject);
        }

        private static float SlotPosition(RectTransform card)
        {
            return card.parent.localPosition.x;
        }

        private static float CardPosition(RectTransform card)
        {
            return SlotPosition(card) + card.anchoredPosition.x;
        }

        private readonly struct CardSnapshot
        {
            public readonly float Position;
            public readonly float Width;

            public CardSnapshot(float position, float width)
            {
                Position = position;
                Width = width;
            }
        }

        internal float MaxCardHeight => _band.rect.height;

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
            float spacing = _band.GetComponent<HorizontalLayoutGroup>().spacing;
            float available = _band.rect.width - spacing * (slots - 1);
            return Mathf.Min(MAX_COLUMN_WIDTH, available / slots);
        }
    }
}
