using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Game;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;
using YARG.Menu.Navigation;
using Object = UnityEngine.Object;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// The view for one band card. It draws whatever <see cref="BandCardContent"/> it is given, moves the
    /// selection, scrolls, and plays animations when told. It makes no decisions about what the card shows.
    /// </summary>
    internal sealed class BandColumn : MonoBehaviour, IPointerClickHandler, IScrollHandler
    {
        public const float ROW_HEIGHT = 52f;
        public const float TWO_LINE_ROW_HEIGHT = 72f;
        public const float PROFILE_ROW_HEIGHT = 64f;
        public static readonly Color READY_COLOR = new Color32(0x00, 0xD3, 0xFF, 0xFF);

        private const float UNFOCUSED_BORDER_BRIGHTNESS = 0.5f;

        private const float AVATAR_WIDTH_SHARE = 0.25f;
        private const float AVATAR_MARGIN = 6f;
        private const float AVATAR_EDGE = 6f;
        private const float SMALL_FONT_SIZE = 20f;
        private const float LABEL_FONT_SIZE = 18f;
        private const float VALUE_FONT_SIZE = 28f;

        private const float ICON_SIZE = 38f;
        private const float HEADER_MARGIN = 40f;
        private const float HEADER_SPACING = 10f;
        private const float HEADING_FONT_SIZE_MIN = 6f;
        private const float ICON_MIN_CARD_WIDTH = 110f;

        private const float SCROLLBAR_WIDTH = 15f;
        private const int SCROLL_FADE = 28;
        private const float SCROLLBAR_WIDTH_SHARE = 0.08f;
        private const int COLLAPSED_PADDING_TRIM = 19;
        private static readonly Color DIVIDER_COLOR = new Color(0f, 0.48f, 0.72f, 0.3f);

        [SerializeField]
        private RectTransform _card;
        [SerializeField]
        private VerticalLayoutGroup _cardLayout;
        [SerializeField]
        private Image _border;
        [SerializeField]
        private TextMeshProUGUI _heading;
        [SerializeField]
        private Image _icon;
        [SerializeField]
        private ScrollRect _scroll;
        [SerializeField]
        private VerticalLayoutGroup _rowLayout;
        [SerializeField]
        private LayoutElement _viewportLayout;
        [SerializeField]
        private LayoutElement _layout;
        [SerializeField]
        private NavigationGroup _nav;

        [Space]
        [SerializeField]
        private DifficultyItem _itemPrefab;
        [SerializeField]
        private DifficultyItem _readyPrefab;
        [SerializeField]
        private DifficultyItem _smallPrefab;
        [SerializeField]
        private DifficultyItem _profilePrefab;
        [SerializeField]
        private ModifierItem _modifierPrefab;
        [SerializeField]
        private Sprite _profileGenericSprite;
        [SerializeField]
        private Sprite _profileBotSprite;

        private sealed class ShownRow
        {
            public BandMenuItem Item;
            public DifficultyItem Row;
            public ModifierItem Toggle;
            public NavigatableBehaviour Navigatable;
            public VerticalLayoutGroup AvatarLayout;
        }

        private readonly struct DividerEntry
        {
            public readonly Transform Row;
            public readonly GameObject Divider;

            public DividerEntry(Transform row, GameObject divider)
            {
                Row = row;
                Divider = divider;
            }
        }

        private readonly List<ShownRow> _shownRows = new();
        private readonly List<DividerEntry> _dividers = new();
        private readonly List<Texture2D> _avatarTextures = new();

        private PlayerCardHeader _playerHeader;
        private LayoutElement _iconLayout;
        private HorizontalLayoutGroup _headerContent;
        private Color _headingColor;
        private bool _wantsIcon;

        private RectMask2D _viewportMask;
        private int _expandedBottomPadding;

        private Color _borderColor;
        private float _appliedWidth = -1f;
        private Tween _rise;
        private Tween _collapse;

        /// <summary>
        /// Raised when the card is left-clicked. The argument is true if the click was on the heading.
        /// </summary>
        public event Action<bool> Clicked;

        /// <summary>
        /// Raised when the selected row changes. The second argument is true if the mouse caused it.
        /// </summary>
        public event Action<BandMenuItem, bool> SelectionChanged;

        public RectTransform Card => _card;
        public float TopEdge => _card.anchoredPosition.y + _card.rect.height;
        public int? SelectedIndex => _nav.SelectedIndex;
        public BandMenuItem SelectedItem => FindShownRow(_nav.SelectedBehaviour)?.Item;

        private RectTransform Rows => _scroll.content;
        private float CollapsedHeight => _cardLayout.padding.top + _expandedBottomPadding - COLLAPSED_PADDING_TRIM;

        private void Awake()
        {
            _playerHeader = new PlayerCardHeader(_heading, _icon);
            _iconLayout = _icon.GetComponent<LayoutElement>();
            _headerContent = _icon.GetComponentInParent<HorizontalLayoutGroup>(true);
            _heading.fontSizeMin = HEADING_FONT_SIZE_MIN;
            _headingColor = _heading.color;

            _viewportMask = _scroll.viewport.GetComponent<RectMask2D>();
            _viewportMask.softness = new Vector2Int(0, SCROLL_FADE);
            _scroll.onValueChanged.AddListener(_ => UpdateScrollFade());
            _expandedBottomPadding = _cardLayout.padding.bottom;

            _borderColor = _border.color;
            _border.raycastTarget = true;
            _nav.SelectionChanged += OnNavSelectionChanged;
        }

        private void OnDestroy()
        {
            _card.DOKill();
            _nav.SelectionChanged -= OnNavSelectionChanged;
            _playerHeader.ReleaseIcon();
            ReleaseAvatars();
        }

        /// <summary>
        /// Replaces every row with <paramref name="content"/> and picks the selection.
        /// </summary>
        public void Show(BandCardContent content, int? restoreIndex, bool resetScroll)
        {
            if (_collapse != null)
            {
                _collapse.Kill();
                _collapse = null;
                _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, 0f);
            }

            ClearRows();
            ShowHeader(content);
            foreach (var item in content.Items)
            {
                AddRow(item);
            }

            StyleRows();
            SelectInitial(content, restoreIndex);
            UpdateDividers();
            if (resetScroll)
            {
                Rows.anchoredPosition = Vector2.zero;
            }
        }

        /// <summary>
        /// Selects <paramref name="item"/>, or the first row if it cannot be selected.
        /// </summary>
        public void Select(BandMenuItem item)
        {
            if (!TrySelect(item))
            {
                _nav.SelectAt(0);
            }
        }

        /// <summary>
        /// Selects <paramref name="item"/> if it is on the card and can be selected.
        /// </summary>
        public bool TrySelect(BandMenuItem item)
        {
            var navigatable = FindNavigatable(item);
            if (navigatable == null)
            {
                return false;
            }

            for (int i = 0; i < _nav.Navigatables.Count; i++)
            {
                if (_nav.Navigatables[i] == navigatable)
                {
                    _nav.SelectAt(i);
                    return true;
                }
            }

            return false;
        }

        public void SelectPrevious(bool isRepeat)
        {
            _nav.SelectPrevious(isRepeat);
        }

        public void SelectNext(bool isRepeat)
        {
            _nav.SelectNext(isRepeat);
        }

        public void ConfirmSelection()
        {
            _nav.ConfirmSelection();
        }

        /// <summary>
        /// Highlights a number row while it is being changed with up and down.
        /// </summary>
        public void SetEditing(BandMenuItem item, bool editing)
        {
            var shown = FindShownRow(item);
            if (shown?.Row != null)
            {
                shown.Row.SetBodyColor(editing ? READY_COLOR : null);
            }
        }

        /// <summary>
        /// Changes a number row by one step and shows its new value.
        /// </summary>
        public void Step(BandMenuItem item, int direction)
        {
            item.Stepped(direction);

            var shown = FindShownRow(item);
            if (shown?.Row != null)
            {
                shown.Row.SetBody(FormatStepper(item.Value()));
            }
        }

        /// <summary>
        /// Sizes the card to its rows, up to <paramref name="maxCardHeight"/>, and updates the scrollbar.
        /// </summary>
        public void Fit(bool collapsed, float maxCardHeight)
        {
            _cardLayout.padding.bottom = collapsed
                ? _expandedBottomPadding - COLLAPSED_PADDING_TRIM
                : _expandedBottomPadding;

            WrapLongRows();

            var rows = Rows;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rows);

            float contentHeight = LayoutUtility.GetPreferredHeight(rows);
            float maxHeight = Mathf.Max(ROW_HEIGHT, maxCardHeight - _cardLayout.padding.vertical);
            _viewportLayout.preferredHeight = Mathf.Min(contentHeight, maxHeight);
            UpdateScrollbarLayout(contentHeight > maxHeight);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            ScrollToSelection();
            UpdateScrollFade();
        }

        public void SetFocused(bool focused)
        {
            _border.color = focused
                ? _borderColor
                : (_borderColor * UNFOCUSED_BORDER_BRIGHTNESS).WithAlpha(_borderColor.a);
        }

        public void SetWidth(float width)
        {
            if (Mathf.Approximately(_appliedWidth, width))
            {
                return;
            }

            _appliedWidth = width;
            _layout.preferredWidth = width;
            foreach (var shown in _shownRows)
            {
                if (shown.Item.Avatar != null)
                {
                    FitAvatar(shown);
                }
            }

            _card.sizeDelta = new Vector2(width, _card.sizeDelta.y);
            FitHeader();
        }

        public void RiseFrom(float offset)
        {
            _rise?.Kill();
            _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, offset);
            _rise = _card.DOAnchorPosY(0f, BandStripAnimator.SLIDE_SECONDS)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true);
        }

        public void SlideOut()
        {
            _rise?.Kill();
            _collapse?.Kill();
            if (!TryGetComponent(out CanvasGroup group))
            {
                group = gameObject.AddComponent<CanvasGroup>();
            }

            group.blocksRaycasts = false;
            _card.DOAnchorPosY(-BandStripAnimator.SLIDE_DISTANCE, BandStripAnimator.SLIDE_SECONDS)
                .SetEase(Ease.InCubic)
                .SetUpdate(true)
                .SetLink(gameObject);
        }

        /// <summary>
        /// Slides the card down until only its heading shows, then calls <paramref name="onComplete"/>.
        /// </summary>
        public void SlideDownAndCollapse(Action onComplete)
        {
            _rise?.Kill();
            _collapse?.Kill();

            _collapse = _card.DOAnchorPosY(CollapsedHeight - _card.rect.height, BandStripAnimator.SLIDE_SECONDS)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .OnComplete(() =>
                {
                    _collapse = null;
                    _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, 0f);
                    onComplete();
                });
        }

        public void DismissAndDestroy()
        {
            _nav.SelectionChanged -= OnNavSelectionChanged;
            _layout.ignoreLayout = true;
            gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;

            _card.DOKill();
            _card.DOAnchorPosY(-BandStripAnimator.SLIDE_DISTANCE, BandStripAnimator.SLIDE_SECONDS)
                .SetEase(Ease.InCubic)
                .SetUpdate(true)
                .SetLink(gameObject)
                .OnComplete(() => Destroy(gameObject));
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            if (eventData.pointerPressRaycast.gameObject == null)
            {
                return;
            }

            bool onHeader = !RectTransformUtility.RectangleContainsScreenPoint((RectTransform) _scroll.transform,
                eventData.position, eventData.pressEventCamera);
            Clicked?.Invoke(onHeader);
        }

        public void OnScroll(PointerEventData eventData)
        {
            _scroll.OnScroll(eventData);
        }

        private void OnNavSelectionChanged(NavigatableBehaviour selected, SelectionOrigin origin)
        {
            bool fromMouse = origin == SelectionOrigin.Mouse;
            SelectionChanged?.Invoke(FindShownRow(selected)?.Item, fromMouse);
            UpdateDividers();

            if (!fromMouse)
            {
                ScrollToSelection();
            }
        }

        private void ShowHeader(BandCardContent content)
        {
            var profile = content.HeaderProfile;
            if (profile == null)
            {
                SetHeaderText(content.HeaderText, showIcon: false);
                return;
            }

            string mode = profile.GameMode.ToResourceName();
            _playerHeader.SetIcon(mode == null ? null : $"InstrumentIcons[{mode}]");
            SetHeaderText(profile.Name, showIcon: true);
            if (content.HeaderReady)
            {
                _heading.color = READY_COLOR;
            }
        }

        private void SetHeaderText(string text, bool showIcon)
        {
            _heading.text = text;
            _heading.color = _headingColor;
            _heading.gameObject.SetActive(!string.IsNullOrEmpty(text));
            _wantsIcon = showIcon && _icon.sprite != null;
            FitHeader();
        }

        private void FitHeader()
        {
            float width = _layout.preferredWidth;
            _icon.gameObject.SetActive(_wantsIcon && width >= ICON_MIN_CARD_WIDTH);
            float scale = Mathf.Clamp01(width * AVATAR_WIDTH_SHARE / ICON_SIZE);
            float iconSize = ICON_SIZE * scale;
            _iconLayout.minWidth = iconSize;
            _iconLayout.minHeight = iconSize;
            _iconLayout.preferredWidth = iconSize;
            _iconLayout.preferredHeight = iconSize;
            _icon.rectTransform.sizeDelta = new Vector2(iconSize, iconSize);
            float spaceScale = scale * scale;
            _headerContent.spacing = HEADER_SPACING * spaceScale;
            _playerHeader.Fit(width, HEADER_MARGIN * spaceScale);
        }

        private void ClearRows()
        {
            _nav.ClearNavigatables();
            ReleaseAvatars();
            _shownRows.Clear();
            for (int i = Rows.childCount - 1; i >= 0; i--)
            {
                var child = Rows.GetChild(i).gameObject;
                child.SetActive(false);
                Object.Destroy(child);
            }
        }

        private void ReleaseAvatars()
        {
            foreach (var texture in _avatarTextures)
            {
                Object.Destroy(texture);
            }

            _avatarTextures.Clear();
        }

        private void AddRow(BandMenuItem item)
        {
            switch (item.Kind)
            {
                case BandMenuItemKind.Label:
                    AddLabel(item);
                    break;
                case BandMenuItemKind.Toggle:
                    AddToggle(item);
                    break;
                default:
                    AddButton(item);
                    break;
            }
        }

        private void AddLabel(BandMenuItem item)
        {
            var label = Object.Instantiate(_itemPrefab, Rows);
            label.InitializeAsTitle(item.Body);
            label.UseCompactText(SMALL_FONT_SIZE);

            if (item.TextColor is { } color)
            {
                label.SetTextColor(color);
            }

            if (item.WrapText)
            {
                label.UseWrappedText();
            }

            if (item.Height is { } height)
            {
                label.GetComponent<LayoutElement>().preferredHeight = height;
            }

            _shownRows.Add(new ShownRow { Item = item, Row = label });
        }

        private void AddToggle(BandMenuItem item)
        {
            var toggle = Object.Instantiate(_modifierPrefab, Rows);
            toggle.Initialize(item.Body, item.IsOn(), on =>
            {
                item.Toggled(on);
                RefreshToggles();
            });

            _nav.AddNavigatable(toggle);
            _shownRows.Add(new ShownRow { Item = item, Toggle = toggle, Navigatable = toggle });
        }

        private void AddButton(BandMenuItem item)
        {
            var prefab = PrefabFor(item.Style);
            var row = Object.Instantiate(prefab != null ? prefab : _itemPrefab, Rows);

            bool isStepper = item.Kind == BandMenuItemKind.Stepper;
            string body = isStepper ? FormatStepper(item.Value()) : item.Body;
            if (item.Header == null)
            {
                row.Initialize(body, () => item.Picked?.Invoke());
            }
            else
            {
                row.Initialize(item.Header, body, () => item.Picked?.Invoke());
                row.UseCompactText(LABEL_FONT_SIZE, VALUE_FONT_SIZE);
            }

            row.GetComponent<LayoutElement>().preferredHeight = item.Height ?? ROW_HEIGHT;
            row.SetInteractable(item.Enabled);
            if (item.Enabled)
            {
                _nav.AddNavigatable(row.Button);
            }

            var shown = new ShownRow { Item = item, Row = row, Navigatable = item.Enabled ? row.Button : null };
            _shownRows.Add(shown);

            if (item.Avatar != null)
            {
                ShowAvatar(shown);
            }

            if (item.AllowTinyText)
            {
                row.AllowTinyText();
            }

            if (item.TextColor is { } color)
            {
                row.SetTextColor(color);
            }

            if (item.RingTemplate != null)
            {
                row.gameObject.AddComponent<DifficultyItemRings>()
                    .ShowInstrumentTiers(item.RingTemplate, item.RingInstruments, item.RingCurrent, item.RingSong);
            }

            if (isStepper)
            {
                row.Button.gameObject.AddComponent<SettingStepClick>().Stepped = direction => Step(item, direction);
            }
        }

        private void RefreshToggles()
        {
            foreach (var shown in _shownRows)
            {
                if (shown.Toggle != null)
                {
                    shown.Toggle.Active = shown.Item.IsOn();
                }
            }
        }

        private void ShowAvatar(ShownRow shown)
        {
            var profile = shown.Item.Avatar;
            var row = shown.Row;
            if (profile.Avatar != null && profile.Avatar.IsValid)
            {
                var texture = profile.Avatar.LoadTexture(false);
                _avatarTextures.Add(texture);
                row.SetAvatar(Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f)), flipped: true);
            }
            else
            {
                row.SetAvatar(profile.IsBot ? _profileBotSprite : _profileGenericSprite, flipped: false);
            }

            shown.AvatarLayout = row.GetComponent<VerticalLayoutGroup>();
            FitAvatar(shown);
        }

        private void FitAvatar(ShownRow shown)
        {
            float rowHeight = shown.Item.Height ?? ROW_HEIGHT;
            float fullSize = rowHeight - AVATAR_EDGE * 2f;
            float size = Mathf.Min(fullSize, _layout.preferredWidth * AVATAR_WIDTH_SHARE);
            float scale = size / fullSize;
            float edge = AVATAR_EDGE * 2f * scale;
            float gap = AVATAR_MARGIN * scale;
            shown.Row.FitAvatar(rowHeight, size, edge);

            int inset = Mathf.CeilToInt(edge + size + gap);
            if (shown.AvatarLayout != null)
            {
                shown.AvatarLayout.padding = new RectOffset(inset, Mathf.CeilToInt(edge), 0, 0);
            }
        }

        private DifficultyItem PrefabFor(BandRowStyle style)
        {
            return style switch
            {
                BandRowStyle.Small   => _smallPrefab,
                BandRowStyle.Ready   => _readyPrefab,
                BandRowStyle.Profile => _profilePrefab,
                _                    => _itemPrefab,
            };
        }

        private void SelectInitial(BandCardContent content, int? restoreIndex)
        {
            if (content.FocusItem != null)
            {
                TrySelect(content.FocusItem);
            }

            if (_nav.Count == 0 || _nav.SelectedIndex is not null)
            {
                return;
            }

            if (restoreIndex is { } index)
            {
                _nav.SelectAt(Mathf.Clamp(index, 0, _nav.Count - 1));
            }
            else if (content.DefaultItem != null)
            {
                Select(content.DefaultItem);
            }
            else
            {
                _nav.SelectFirst();
            }
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

                _dividers.Add(new DividerEntry(row, divider));
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
            int selectedRowIndex = FindSelectedRowIndex();
            for (int rowIndex = 0; rowIndex < _dividers.Count; rowIndex++)
            {
                var divider = _dividers[rowIndex].Divider;
                if (divider == null)
                {
                    continue;
                }

                bool touchesSelection = rowIndex == selectedRowIndex || rowIndex == selectedRowIndex + 1;
                divider.SetActive(!touchesSelection);
            }
        }

        private int FindSelectedRowIndex()
        {
            var selected = _nav.SelectedBehaviour;
            if (selected == null)
            {
                return -1;
            }

            for (int rowIndex = 0; rowIndex < _dividers.Count; rowIndex++)
            {
                if (selected.transform.IsChildOf(_dividers[rowIndex].Row))
                {
                    return rowIndex;
                }
            }

            return -1;
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
            bool anyWrappable = false;
            foreach (var shown in _shownRows)
            {
                if (shown.Item.WrapIfLong)
                {
                    anyWrappable = true;
                    break;
                }
            }

            if (!anyWrappable)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            foreach (var shown in _shownRows)
            {
                if (!shown.Item.WrapIfLong)
                {
                    continue;
                }

                var layout = shown.Row.GetComponent<LayoutElement>();
                if (shown.Row.WrapIfLong() && layout != null)
                {
                    layout.preferredHeight = TWO_LINE_ROW_HEIGHT;
                }
            }
        }

        private ShownRow FindShownRow(NavigatableBehaviour navigatable)
        {
            if (navigatable == null)
            {
                return null;
            }

            foreach (var shown in _shownRows)
            {
                if (shown.Navigatable == navigatable)
                {
                    return shown;
                }
            }

            return null;
        }

        private ShownRow FindShownRow(BandMenuItem item)
        {
            if (item == null)
            {
                return null;
            }

            foreach (var shown in _shownRows)
            {
                if (shown.Item == item)
                {
                    return shown;
                }
            }

            return null;
        }

        private NavigatableBehaviour FindNavigatable(BandMenuItem item)
        {
            return FindShownRow(item)?.Navigatable;
        }

        private static string FormatStepper(string value)
        {
            return $"<noparse><</noparse>  {value}  <noparse>></noparse>";
        }
    }

    internal static class BandColumnExtensions
    {
        public static bool ContainsInstrument(this IReadOnlyList<Instrument> instruments, Instrument target)
        {
            foreach (var instrument in instruments)
            {
                if (instrument == target)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
