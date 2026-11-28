using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// Slides and resizes band cards from where they were to where the layout puts them after players join,
    /// leave, or move. Call <see cref="ClearSnapshots"/> and <see cref="Capture"/> before the layout changes,
    /// then <see cref="Shift"/> or <see cref="ShiftOrRise"/> after it.
    /// </summary>
    internal sealed class BandStripAnimator
    {
        internal const float SLIDE_DISTANCE = 900f;
        internal const float SLIDE_SECONDS = 0.3f;

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

        private readonly Dictionary<RectTransform, CardSnapshot> _snapshots = new();
        private readonly Dictionary<RectTransform, Tween> _shifts = new();
        private readonly Dictionary<RectTransform, Tween> _resizes = new();

        /// <summary>
        /// Forgets every captured card position.
        /// </summary>
        public void ClearSnapshots()
        {
            _snapshots.Clear();
        }

        /// <summary>
        /// Remembers where a card is and how wide it is right now.
        /// </summary>
        public void Capture(RectTransform card)
        {
            _snapshots[card] = new CardSnapshot(CardPosition(card), card.sizeDelta.x);
        }

        /// <summary>
        /// Moves a captured position from one card to another, so the new card slides from where the old one was.
        /// </summary>
        public void RemapSnapshot(RectTransform oldCard, RectTransform newCard)
        {
            if (_snapshots.Remove(oldCard, out var snapshot))
            {
                _snapshots[newCard] = snapshot;
            }
        }

        /// <summary>
        /// Slides a captured card to its slot, or rises an uncaptured one from below when a rise delay is given.
        /// </summary>
        public void ShiftOrRise(RectTransform card, float delay, float? riseDelay)
        {
            if (_snapshots.ContainsKey(card))
            {
                Shift(card, delay);
                return;
            }

            if (riseDelay is not { } rise)
            {
                return;
            }

            card.DOKill();
            card.anchoredPosition = new Vector2(0f, -SLIDE_DISTANCE);
            card.DOAnchorPosY(0f, SLIDE_SECONDS)
                .SetDelay(rise)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .SetLink(card.gameObject);
        }

        /// <summary>
        /// Slides and resizes a card from its captured position and width to where the layout put it.
        /// </summary>
        public void Shift(RectTransform card, float delay)
        {
            if (_shifts.Remove(card, out var running))
            {
                running.Kill();
            }

            bool captured = _snapshots.TryGetValue(card, out var snapshot);
            if (captured)
            {
                Resize(card, snapshot.Width, delay);
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
                .SetUpdate(true)
                .SetLink(card.gameObject);
        }

        /// <summary>
        /// Stops every tween running on a card.
        /// </summary>
        public void Kill(RectTransform card)
        {
            if (_shifts.Remove(card, out var shift))
            {
                shift.Kill();
            }

            if (_resizes.Remove(card, out var resize))
            {
                resize.Kill();
            }

            card.DOKill();
        }

        /// <summary>
        /// Stops every slide and resize this animator started.
        /// </summary>
        public void KillAll()
        {
            foreach (var shift in _shifts.Values)
            {
                shift.Kill();
            }

            foreach (var resize in _resizes.Values)
            {
                resize.Kill();
            }

            _shifts.Clear();
            _resizes.Clear();
        }

        private void Resize(RectTransform card, float from, float delay)
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
            _resizes[card] = DOTween.To(
                    () => card.sizeDelta.x,
                    x => card.sizeDelta = new Vector2(x, card.sizeDelta.y),
                    to,
                    SLIDE_SECONDS)
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
    }
}
