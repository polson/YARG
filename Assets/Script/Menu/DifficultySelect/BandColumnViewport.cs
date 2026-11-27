using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YARG.Menu.Navigation;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandColumnViewport
    {
        private const float SCROLLBAR_WIDTH = 15f;
        private const int SCROLL_FADE = 28;
        private const float SCROLLBAR_WIDTH_SHARE = 0.08f;
        private const int COLLAPSED_PADDING_TRIM = 19;
        private static readonly Color DIVIDER_COLOR = new Color(0f, 0.48f, 0.72f, 0.3f);

        private readonly RectTransform _card;
        private readonly VerticalLayoutGroup _cardLayout;
        private readonly ScrollRect _scroll;
        private readonly VerticalLayoutGroup _rowLayout;
        private readonly LayoutElement _viewportLayout;
        private readonly LayoutElement _layout;
        private readonly NavigationGroup _nav;
        private readonly BandColumnRows _rows;
        private readonly RectMask2D _viewportMask;
        private readonly int _expandedBottomPadding;
        private readonly List<(Transform Row, GameObject Divider)> _dividers = new();

        public BandColumnViewport(RectTransform card, VerticalLayoutGroup cardLayout, ScrollRect scroll,
            VerticalLayoutGroup rowLayout, LayoutElement viewportLayout, LayoutElement layout, NavigationGroup nav,
            BandColumnRows rows)
        {
            _card = card;
            _cardLayout = cardLayout;
            _scroll = scroll;
            _rowLayout = rowLayout;
            _viewportLayout = viewportLayout;
            _layout = layout;
            _nav = nav;
            _rows = rows;

            _viewportMask = scroll.viewport.GetComponent<RectMask2D>();
            _viewportMask.softness = new Vector2Int(0, SCROLL_FADE);
            scroll.onValueChanged.AddListener(_ => UpdateScrollFade());
            _expandedBottomPadding = cardLayout.padding.bottom;
        }

        public float CollapsedHeight => _cardLayout.padding.top + _expandedBottomPadding - COLLAPSED_PADDING_TRIM;

        private RectTransform Rows => _scroll.content;

        public void StyleRows()
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

        public void UpdateDividers()
        {
            int selectedIndex = FindSelectedRowIndex();
            for (int i = 0; i < _dividers.Count; i++)
            {
                var divider = _dividers[i].Divider;
                if (divider != null)
                {
                    divider.SetActive(i != selectedIndex && i != selectedIndex + 1);
                }
            }
        }

        public void ResetScroll()
        {
            Rows.anchoredPosition = Vector2.zero;
        }

        public void Fit(bool collapsed, float maxCardHeight)
        {
            _cardLayout.padding.bottom = collapsed
                ? _expandedBottomPadding - COLLAPSED_PADDING_TRIM
                : _expandedBottomPadding;

            WrapLongRows();

            var rows = Rows;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rows);

            float contentHeight = LayoutUtility.GetPreferredHeight(rows);
            float maxHeight = Mathf.Max(BandColumnRows.ROW_HEIGHT, maxCardHeight - _cardLayout.padding.vertical);
            _viewportLayout.preferredHeight = Mathf.Min(contentHeight, maxHeight);
            UpdateScrollbarLayout(contentHeight > maxHeight);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            ScrollToSelection();
            UpdateScrollFade();
        }

        public void OnScroll(PointerEventData eventData)
        {
            _scroll.OnScroll(eventData);
        }

        public void ScrollToSelection()
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

        public bool ContainsScreenPoint(Vector2 position, Camera camera)
        {
            return RectTransformUtility.RectangleContainsScreenPoint((RectTransform) _scroll.transform, position,
                camera);
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

        private int FindSelectedRowIndex()
        {
            var selected = _nav.SelectedBehaviour;
            if (selected == null)
            {
                return -1;
            }

            for (int i = 0; i < _dividers.Count; i++)
            {
                if (selected.transform.IsChildOf(_dividers[i].Row))
                {
                    return i;
                }
            }

            return -1;
        }

        private void UpdateScrollbarLayout(bool scrollable)
        {
            var scrollbar = (RectTransform) _scroll.verticalScrollbar.transform;
            scrollbar.gameObject.SetActive(scrollable);
            float scrollbarWidth = Mathf.Min(SCROLLBAR_WIDTH, _layout.preferredWidth * SCROLLBAR_WIDTH_SHARE);
            scrollbar.sizeDelta = new Vector2(scrollbarWidth, scrollbar.sizeDelta.y);
            scrollbar.anchoredPosition = new Vector2(-scrollbarWidth / 2f, scrollbar.anchoredPosition.y);
            _rowLayout.padding.right = scrollable ? Mathf.CeilToInt(scrollbar.sizeDelta.x) : 0;
        }

        private void UpdateScrollFade()
        {
            float hidden = Rows.rect.height - _scroll.viewport.rect.height;
            float offset = Rows.anchoredPosition.y;
            bool atTop = offset <= 0.5f;
            bool atBottom = offset >= hidden - 0.5f;
            _viewportMask.padding = new Vector4(0f, atBottom ? -SCROLL_FADE : 0f, 0f, atTop ? -SCROLL_FADE : 0f);
        }

        private void WrapLongRows()
        {
            if (_rows.WrappableRows.Count == 0)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            foreach (var (row, layout) in _rows.WrappableRows)
            {
                if (row.WrapIfLong() && layout != null)
                {
                    layout.preferredHeight = BandColumnRows.TWO_LINE_ROW_HEIGHT;
                }
            }
        }
    }
}
