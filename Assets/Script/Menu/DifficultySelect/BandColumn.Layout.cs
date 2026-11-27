using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed partial class BandColumn
    {
        public void SetWidth(float width)
        {
            if (Mathf.Approximately(_appliedWidth, width))
            {
                return;
            }

            _appliedWidth = width;
            _layout.preferredWidth = width;
            foreach (var (row, rowHeight) in _profileRows)
            {
                FitProfileRow(row, rowHeight);
            }

            _card.sizeDelta = new Vector2(width, _card.sizeDelta.y);
            FitHeader();
        }

        private void StyleRows()
        {
            _dividers.Clear();
            foreach (Transform row in Rows)
            {
                if (!row.gameObject.activeSelf)
                {
                    continue;
                }

                var background = row.Find("Background");
                if (background != null && background.TryGetComponent<Image>(out var image))
                {
                    image.sprite = null;
                    image.color = Color.clear;
                }

                GameObject divider = null;
                if (_dividers.Count > 0)
                {
                    divider = AddDivider(row);
                    if (background != null)
                    {
                        divider.transform.SetSiblingIndex(background.GetSiblingIndex() + 1);
                    }
                }

                _dividers.Add((row, divider));
            }
        }

        private static GameObject AddDivider(Transform row)
        {
            var divider = new GameObject("Divider", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            var rect = (RectTransform) divider.transform;
            rect.SetParent(row, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, -1f);
            rect.offsetMax = Vector2.zero;

            var image = divider.GetComponent<Image>();
            image.color = DIVIDER_COLOR;
            image.raycastTarget = false;
            divider.GetComponent<LayoutElement>().ignoreLayout = true;
            return divider;
        }

        private void UpdateDividers()
        {
            var selected = _nav.SelectedBehaviour;
            int selectedIndex = selected == null
                ? -1
                : _dividers.FindIndex(entry => selected.transform.IsChildOf(entry.Row));

            for (int i = 0; i < _dividers.Count; i++)
            {
                var divider = _dividers[i].Divider;
                if (divider != null)
                {
                    divider.SetActive(i != selectedIndex && i != selectedIndex + 1);
                }
            }
        }

        private void SelectDefault(int? restoreIndex)
        {
            if (_nav.Count == 0 || _nav.SelectedIndex is not null)
            {
                return;
            }

            if (restoreIndex is { } index)
            {
                _nav.SelectAt(Mathf.Clamp(index, 0, _nav.Count - 1));
            }
            else if (_defaultButton != null)
            {
                _nav.SelectAt(IndexOf(_defaultButton));
            }
            else if (_readyButton != null && Ready)
            {
                _nav.SelectAt(IndexOf(_readyButton));
            }
            else if (_difficultyButton != null)
            {
                _nav.SelectAt(IndexOf(_difficultyButton));
            }
            else
            {
                _nav.SelectFirst();
            }
        }

        public void FitRows()
        {
            _cardLayout.padding.bottom = IsCollapsed
                ? _expandedBottomPadding - COLLAPSED_PADDING_TRIM
                : _expandedBottomPadding;

            WrapLongRows();

            var rows = Rows;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rows);

            float contentHeight = LayoutUtility.GetPreferredHeight(rows);
            float maxHeight = Mathf.Max(ROW_HEIGHT, _menu.MaxCardHeight - _cardLayout.padding.vertical);
            bool scrollable = contentHeight > maxHeight;

            var scrollbar = (RectTransform) _scroll.verticalScrollbar.transform;
            _viewportLayout.preferredHeight = Mathf.Min(contentHeight, maxHeight);
            scrollbar.gameObject.SetActive(scrollable);
            float scrollbarWidth = Mathf.Min(SCROLLBAR_WIDTH, _layout.preferredWidth * SCROLLBAR_WIDTH_SHARE);
            scrollbar.sizeDelta = new Vector2(scrollbarWidth, scrollbar.sizeDelta.y);
            scrollbar.anchoredPosition = new Vector2(-scrollbarWidth / 2f, scrollbar.anchoredPosition.y);
            _rowLayout.padding.right = scrollable ? Mathf.CeilToInt(scrollbar.sizeDelta.x) : 0;

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            ScrollToSelection();
            UpdateScrollFade();
        }

        private void UpdateScrollFade()
        {
            float hidden = Rows.rect.height - _scroll.viewport.rect.height;
            float offset = Rows.anchoredPosition.y;
            bool atTop = offset <= 0.5f;
            bool atBottom = offset >= hidden - 0.5f;
            _viewportMask.padding = new Vector4(0f, atBottom ? -SCROLL_FADE : 0f, 0f, atTop ? -SCROLL_FADE : 0f);
        }

        public void OnScroll(PointerEventData eventData)
        {
            _scroll.OnScroll(eventData);
        }

        private void WrapLongRows()
        {
            if (_wrappableRows.Count == 0)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            foreach (var row in _wrappableRows)
            {
                if (row.WrapIfLong())
                {
                    row.GetComponent<LayoutElement>().preferredHeight = TWO_LINE_ROW_HEIGHT;
                }
            }
        }

        private void ScrollToSelection()
        {
            if (_nav.SelectedIndex is not { } index)
            {
                return;
            }

            var rows = Rows;
            var item = (RectTransform) _nav.Navigatables[index].transform;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_scroll.viewport, item);
            var view = _scroll.viewport.rect;

            float y = rows.anchoredPosition.y;
            if (bounds.max.y > view.yMax)
            {
                y -= bounds.max.y - view.yMax;
            }
            else if (bounds.min.y < view.yMin)
            {
                y += view.yMin - bounds.min.y;
            }

            float maxScroll = Mathf.Max(0f, rows.rect.height - view.height);
            rows.anchoredPosition = new Vector2(0f, Mathf.Clamp(y, 0f, maxScroll));
        }

        private void SlideDownAndCollapse()
        {
            _rise?.Kill();
            _collapse?.Kill();

            float collapsedHeight = _cardLayout.padding.top + _expandedBottomPadding - COLLAPSED_PADDING_TRIM;
            _collapse = _card.DOAnchorPosY(collapsedHeight - _card.rect.height, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .OnComplete(() =>
                {
                    _collapse = null;
                    _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, 0f);
                    Rebuild();
                });
        }

        private void FitHeader()
        {
            float width = _layout.preferredWidth;
            _icon.gameObject.SetActive(_wantsHeaderIcon && width >= HEADER_ICON_MIN_CARD_WIDTH);
            float scale = Mathf.Clamp01(width * AVATAR_WIDTH_SHARE / HEADER_ICON_SIZE);
            float iconSize = HEADER_ICON_SIZE * scale;
            _iconLayout.minWidth = iconSize;
            _iconLayout.minHeight = iconSize;
            _iconLayout.preferredWidth = iconSize;
            _iconLayout.preferredHeight = iconSize;
            _icon.rectTransform.sizeDelta = new Vector2(iconSize, iconSize);
            float spaceScale = scale * scale;
            _headerContent.spacing = HEADER_SPACING * spaceScale;
            _header.Fit(width, HEADER_MARGIN * spaceScale);
        }

        private void SetHeading(string text, bool showIcon = false)
        {
            _heading.text = text;
            _heading.color = _headingColor;
            _heading.gameObject.SetActive(!string.IsNullOrEmpty(text));
            _wantsHeaderIcon = showIcon && _icon.sprite != null;
            FitHeader();
        }

    }
}
