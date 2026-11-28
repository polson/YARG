using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace YARG.Menu.Overshell
{
    /// <summary>
    /// Owns the empty "press to join" slots in the band strip: creates and destroys them, sizes and labels them,
    /// and animates them through the shared <see cref="StripAnimator"/>.
    /// </summary>
    internal sealed class JoinSlotStrip
    {
        private readonly struct JoinSlotEntry
        {
            public readonly LayoutElement Slot;
            public readonly RectTransform Card;
            public readonly TextMeshProUGUI Label;

            public JoinSlotEntry(LayoutElement slot, RectTransform card, TextMeshProUGUI label)
            {
                Slot = slot;
                Card = card;
                Label = label;
            }
        }

        private readonly RectTransform _band;
        private readonly LayoutElement _template;
        private readonly StripAnimator _animator;
        private readonly Action<LayoutElement> _clicked;
        private readonly List<JoinSlotEntry> _entries = new();

        public JoinSlotStrip(RectTransform band, LayoutElement template, StripAnimator animator,
            Action<LayoutElement> clicked)
        {
            _band = band;
            _template = template;
            _animator = animator;
            _clicked = clicked;
        }

        /// <summary>
        /// Returns the card inside a join slot.
        /// </summary>
        public static RectTransform CardOf(LayoutElement slot)
        {
            return (RectTransform) slot.transform.GetChild(0);
        }

        /// <summary>
        /// Adds or removes join slots at the end of the band until there are exactly <paramref name="count"/>.
        /// </summary>
        public void SetCount(int count)
        {
            while (_entries.Count > count)
            {
                Destroy(_entries[_entries.Count - 1].Slot);
            }

            while (_entries.Count < count)
            {
                Create();
            }
        }

        /// <summary>
        /// Stops the slot's tweens and destroys it.
        /// </summary>
        public void Destroy(LayoutElement slot)
        {
            if (slot == null)
            {
                return;
            }

            int entryIndex = IndexOf(slot);
            if (entryIndex >= 0)
            {
                _entries.RemoveAt(entryIndex);
            }

            _animator.Kill(CardOf(slot));
            slot.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(slot.gameObject);
        }

        /// <summary>
        /// Returns true if the slot is one of this strip's join slots.
        /// </summary>
        public bool Contains(LayoutElement slot)
        {
            return IndexOf(slot) >= 0;
        }

        /// <summary>
        /// Returns the join slot at a sibling index in the band, or null if a player card is there.
        /// </summary>
        public LayoutElement FindAtSiblingIndex(int siblingIndex)
        {
            foreach (var entry in _entries)
            {
                if (entry.Slot.transform.GetSiblingIndex() == siblingIndex)
                {
                    return entry.Slot;
                }
            }

            return null;
        }

        /// <summary>
        /// Moves the join slot with the given order index to the end of the band.
        /// </summary>
        public void MoveToEnd(int joinSlotIndex)
        {
            _entries[joinSlotIndex].Slot.transform.SetAsLastSibling();
        }

        /// <summary>
        /// Sets every join slot's width and label.
        /// </summary>
        public void UpdateCards(float width, string text)
        {
            foreach (var entry in _entries)
            {
                if (!Mathf.Approximately(entry.Slot.preferredWidth, width))
                {
                    entry.Slot.preferredWidth = width;
                    entry.Card.sizeDelta = new Vector2(width, entry.Card.sizeDelta.y);
                }

                entry.Label.text = text;
            }
        }

        /// <summary>
        /// Remembers where every join card is before the layout changes.
        /// </summary>
        public void CapturePositions()
        {
            foreach (var entry in _entries)
            {
                _animator.Capture(entry.Card);
            }
        }

        /// <summary>
        /// Slides join cards to their new slots, rising new ones from below when a rise delay is given.
        /// </summary>
        public void Shift(float delay, float? riseDelay)
        {
            foreach (var entry in _entries)
            {
                _animator.ShiftOrRise(entry.Card, delay, riseDelay);
            }
        }

        /// <summary>
        /// Slides every join card down off screen.
        /// </summary>
        public void SlideOut()
        {
            foreach (var entry in _entries)
            {
                entry.Card.DOAnchorPosY(-StripAnimator.SLIDE_DISTANCE, StripAnimator.SLIDE_SECONDS)
                    .SetEase(Ease.InCubic)
                    .SetUpdate(true)
                    .SetLink(entry.Card.gameObject);
            }
        }

        private void Create()
        {
            int siblingIndex = _band.childCount;
            var slot = UnityEngine.Object.Instantiate(_template, _band);
            slot.transform.SetSiblingIndex(siblingIndex);
            slot.preferredWidth = -1f;

            var card = CardOf(slot);
            card.anchoredPosition = Vector2.zero;
            foreach (var graphic in card.GetComponentsInChildren<Graphic>())
            {
                graphic.raycastTarget = true;
            }

            card.gameObject.AddComponent<JoinSlotClick>().Clicked = () => _clicked(slot);
            slot.gameObject.SetActive(true);

            var label = slot.GetComponentInChildren<TextMeshProUGUI>(true);
            _entries.Add(new JoinSlotEntry(slot, card, label));
        }

        private int IndexOf(LayoutElement slot)
        {
            for (int entryIndex = 0; entryIndex < _entries.Count; entryIndex++)
            {
                if (_entries[entryIndex].Slot == slot)
                {
                    return entryIndex;
                }
            }

            return -1;
        }
    }
}
