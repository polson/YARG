using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YARG.Menu.DifficultySelect
{
    internal readonly struct JoinSlotEntry
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

    internal sealed class BandJoinSlots
    {
        private readonly RectTransform _band;
        private readonly LayoutElement _template;
        private readonly BandCardAnimator _animator;
        private readonly Action<LayoutElement> _clicked;
        private readonly List<JoinSlotEntry> _entries = new();

        public BandJoinSlots(RectTransform band, LayoutElement template, BandCardAnimator animator,
            Action<LayoutElement> clicked)
        {
            _band = band;
            _template = template;
            _animator = animator;
            _clicked = clicked;
        }

        public IReadOnlyList<JoinSlotEntry> Entries => _entries;

        public static RectTransform CardOf(LayoutElement slot)
        {
            return (RectTransform) slot.transform.GetChild(0);
        }

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

        public bool Contains(LayoutElement slot)
        {
            return IndexOf(slot) >= 0;
        }

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

        public void Destroy(LayoutElement slot)
        {
            if (slot == null)
            {
                return;
            }

            var card = CardOf(slot);
            int index = IndexOf(slot);
            if (index >= 0)
            {
                _entries.RemoveAt(index);
            }

            _animator.KillTweens(card);
            slot.gameObject.SetActive(false);
            Object.Destroy(slot.gameObject);
        }

        private void Create()
        {
            int siblingIndex = _band.childCount;
            var slot = Object.Instantiate(_template, _band);
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
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Slot == slot)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
