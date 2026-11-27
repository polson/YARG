using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandCardAnimator
    {
        private readonly Dictionary<RectTransform, Tween> _shifts = new();
        private readonly Dictionary<RectTransform, Tween> _resizes = new();

        public void KillTweens(RectTransform card)
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

        public void Capture(
            IReadOnlyList<BandColumn> columns,
            IReadOnlyList<RectTransform> joinCards,
            Dictionary<RectTransform, (float Position, float Width)> target)
        {
            target.Clear();
            for (int i = 0; i < columns.Count; i++)
            {
                var card = columns[i].Card;
                target[card] = (CardPosition(card), card.sizeDelta.x);
            }

            for (int i = 0; i < joinCards.Count; i++)
            {
                var card = joinCards[i];
                target[card] = (CardPosition(card), card.sizeDelta.x);
            }
        }

        public void RemapCard(
            Dictionary<RectTransform, (float Position, float Width)> snapshots,
            RectTransform oldCard,
            RectTransform newCard)
        {
            if (snapshots != null && snapshots.Remove(oldCard, out var snapshot))
            {
                snapshots[newCard] = snapshot;
            }
        }

        public void ShiftCards(
            RectTransform band,
            IReadOnlyList<BandColumn> columns,
            IReadOnlyList<RectTransform> joinCards,
            Dictionary<RectTransform, (float Position, float Width)> before,
            float delay,
            float? riseDelay)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(band);

            for (int i = 0; i < columns.Count; i++)
            {
                ShiftCard(columns[i].Card, before, delay);
            }

            for (int i = 0; i < joinCards.Count; i++)
            {
                var card = joinCards[i];
                if (before.ContainsKey(card))
                {
                    ShiftCard(card, before, delay);
                }
                else if (riseDelay is { } rise)
                {
                    card.DOKill();
                    card.anchoredPosition = new Vector2(0f, -BandSelectMenu.SLIDE_DISTANCE);
                    card.DOAnchorPosY(0f, BandSelectMenu.SLIDE_SECONDS)
                        .SetDelay(rise)
                        .SetEase(Ease.OutCubic)
                        .SetUpdate(true);
                }
            }
        }

        public void SlideOutJoinCards(IReadOnlyList<RectTransform> joinCards)
        {
            for (int i = 0; i < joinCards.Count; i++)
            {
                var card = joinCards[i];
                card.DOAnchorPosY(-BandSelectMenu.SLIDE_DISTANCE, BandSelectMenu.SLIDE_SECONDS)
                    .SetEase(Ease.InCubic)
                    .SetUpdate(true)
                    .SetLink(card.gameObject);
            }
        }

        private void ShiftCard(
            RectTransform card,
            Dictionary<RectTransform, (float Position, float Width)> before,
            float delay)
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

            _shifts[card] = card.DOAnchorPosX(0f, BandSelectMenu.SLIDE_SECONDS)
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
            _resizes[card] = DOTween.To(
                    () => card.sizeDelta.x,
                    x => card.sizeDelta = new Vector2(x, card.sizeDelta.y),
                    to,
                    BandSelectMenu.SLIDE_SECONDS)
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
