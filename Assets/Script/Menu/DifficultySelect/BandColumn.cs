using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
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
    internal sealed partial class BandColumn : MonoBehaviour, IPointerClickHandler, IScrollHandler
    {
        private const float PROFILE_ROW_HEIGHT = 64f;
        private const float AVATAR_MARGIN = 6f;
        private const float AVATAR_EDGE = 6f;
        private const float AVATAR_WIDTH_SHARE = 0.25f;
        private const float HEADER_ICON_SIZE = 38f;
        private const float HEADER_MARGIN = 40f;
        private const float HEADER_SPACING = 10f;
        private const float HEADER_FONT_SIZE_MIN = 6f;
        private const float HEADER_ICON_MIN_CARD_WIDTH = 110f;
        private const float SCROLLBAR_WIDTH = 15f;
        private const int SCROLL_FADE = 28;
        private const float SCROLLBAR_WIDTH_SHARE = 0.08f;
        private const float ROW_HEIGHT = 52f;
        private const float TWO_LINE_ROW_HEIGHT = 72f;
        private const float SMALL_FONT_SIZE = 20f;
        private const float LABEL_FONT_SIZE = 18f;
        private const float VALUE_FONT_SIZE = 28f;
        private const float MODIFIER_LINE_HEIGHT = 34f;

        private const float UNFOCUSED_BORDER_BRIGHTNESS = 0.5f;
        private const int COLLAPSED_PADDING_TRIM = 19;
        private static readonly Color WARNING_COLOR = new Color32(0xFB, 0x44, 0x3F, 0xFF);
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
        private LayoutElement _iconLayout;
        private HorizontalLayoutGroup _headerContent;
        private bool _wantsHeaderIcon;
        [SerializeField]
        private ScrollRect _scroll;
        private RectMask2D _viewportMask;
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

        private BandSelectMenu _menu;
        private BandSessionState _session;
        private Color _borderColor;
        private Color _headingColor;

        private PlayerCardHeader _header;
        private float _appliedWidth = -1f;
        private readonly List<(DifficultyItem Row, VerticalLayoutGroup Layout, float Height)> _profileRows = new();
        private readonly List<(DifficultyItem Row, LayoutElement Layout)> _wrappableRows = new();
        private Tween _rise;
        private Tween _collapse;
        private readonly List<Texture2D> _avatarTextures = new();
        private readonly List<(ModifierItem Item, Modifier Modifier)> _modifierToggles = new();
        private readonly List<(Transform Row, GameObject Divider)> _dividers = new();

        private BandColumnState _builtState;
        private int _expandedBottomPadding;
        private NavigatableBehaviour _readyButton;
        private NavigatableBehaviour _difficultyButton;
        private NavigatableBehaviour _defaultButton;
        private NavigatableBehaviour _moveLeftButton;
        private NavigatableBehaviour _moveRightButton;

        public BandColumnModel Model { get; private set; }
        public YargPlayer Player => Model?.Player;

        public BandColumnState State => Model.State;
        public bool Ready => Model.Ready;

        public bool IsJoining => Model.IsJoining;
        private bool IsCollapsed => Model.IsCollapsed;
        public bool IsExpanded => Model.IsExpanded;
        public bool IsAtTopLevel => Model.IsAtTopLevel;
        public bool IsSittingOut => _session.IsSittingOut(Model);
        public bool HasChosenInstrument => Model.HasChosenInstrument;
        public bool IsDecided => _session.IsDecided(Model);

        public NavigationGroup Nav => _nav;
        public RectTransform Card => _card;

        private RectTransform Rows => _scroll.content;

        private void Awake()
        {
            _header = new PlayerCardHeader(_heading, _icon);
            _iconLayout = _icon.GetComponent<LayoutElement>();
            _headerContent = _icon.GetComponentInParent<HorizontalLayoutGroup>(true);
            _heading.fontSizeMin = HEADER_FONT_SIZE_MIN;
            _viewportMask = _scroll.viewport.GetComponent<RectMask2D>();
            _viewportMask.softness = new Vector2Int(0, SCROLL_FADE);
            _scroll.onValueChanged.AddListener(_ => UpdateScrollFade());
            _expandedBottomPadding = _cardLayout.padding.bottom;
            _borderColor = _border.color;
            _headingColor = _heading.color;
        }

        public void Initialize(BandSelectMenu menu, BandSessionState session, BandColumnModel model)
        {
            _menu = menu;
            _session = session;
            Model = model;
            _border.raycastTarget = true;
            _nav.SelectionChanged += OnSelectionChanged;
        }

        public void RiseFrom(float offset)
        {
            _rise?.Kill();
            _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, offset);
            _rise = _card.DOAnchorPosY(0f, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true);
        }

        private void OnDestroy()
        {
            _card.DOKill();
            _nav.SelectionChanged -= OnSelectionChanged;
            _header.ReleaseIcon();
            ClearAvatars();
        }

        public void DismissAndDestroy()
        {
            _nav.SelectionChanged -= OnSelectionChanged;
            _layout.ignoreLayout = true;
            gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;

            _card.DOKill();
            _card.DOAnchorPosY(-BandSelectMenu.SLIDE_DISTANCE, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.InCubic)
                .SetUpdate(true)
                .SetLink(gameObject)
                .OnComplete(() => Destroy(gameObject));
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
            _card.DOAnchorPosY(-BandSelectMenu.SLIDE_DISTANCE, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.InCubic)
                .SetUpdate(true)
                .SetLink(gameObject);
        }

        public void SetFocused(bool focused)
        {
            _border.color = focused
                ? _borderColor
                : (_borderColor * UNFOCUSED_BORDER_BRIGHTNESS).WithAlpha(_borderColor.a);
        }

        public void BeginJoin()
        {
            Rebuild();
        }

        public void Open()
        {
            Rebuild();
        }

        public void Collapse()
        {
            _session.ResetForOvershell(Model);
            Rebuild();
        }

        public void CompleteJoin()
        {
            if (_session.IsOvershell)
            {
                RebuildFromCurrentTop();
            }
            else
            {
                Rebuild();
            }
        }

        public void Back()
        {
            if (StopEditing())
            {
                return;
            }

            if (State == BandColumnState.ChooseProfile ||
                (State == BandColumnState.Instrument && Model.ViewStack.Depth == 0))
            {
                _menu.Disconnect(this);
                return;
            }

            if (State == BandColumnState.Main)
            {
                if (Ready)
                {
                    ToggleReady();
                }
                else if (_session.CollapseColumn(Model))
                {
                    SlideDownAndCollapse();
                }

                return;
            }

            if (State == BandColumnState.SittingOut)
            {
                OpenPicker(canCancel: true);
                return;
            }

            PopView();
        }

        public void Rebuild()
        {
            int? previousIndex = _nav.SelectedIndex;
            bool sameState = _builtState == State;

            if (_collapse != null)
            {
                _collapse.Kill();
                _collapse = null;
                _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, 0f);
            }

            _nav.ClearNavigatables();
            ClearAvatars();
            _profileRows.Clear();
            _steppers.Clear();
            _editingRow = null;
            _wrappableRows.Clear();
            _modifierToggles.Clear();
            _readyButton = null;
            _difficultyButton = null;
            _defaultButton = null;
            _moveLeftButton = null;
            _moveRightButton = null;
            for (int i = Rows.childCount - 1; i >= 0; i--)
            {
                var child = Rows.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            switch (State)
            {
                case BandColumnState.ChooseProfile:
                    BuildProfilePicker();
                    break;
                case BandColumnState.Main:
                    BuildMain();
                    break;
                case BandColumnState.Difficulty:
                    BuildDifficultyPicker();
                    break;
                case BandColumnState.Instrument:
                    BuildInstrumentPicker();
                    break;
                case BandColumnState.Adjustments:
                    BuildAdjustments();
                    break;
                case BandColumnState.Modifiers:
                    BuildModifiers();
                    break;
                case BandColumnState.Accessibility:
                    BuildAccessibility();
                    break;
                case BandColumnState.OpenLane:
                    BuildOpenLane();
                    break;
                case BandColumnState.SittingOut:
                    BuildSittingOut();
                    break;
                case BandColumnState.Position:
                    BuildPosition();
                    break;
                case BandColumnState.Settings:
                    BuildSettings();
                    break;
                case BandColumnState.SettingOptions:
                    BuildSettingOptions();
                    break;
            }

            _builtState = State;
            Model.ViewStack.RestoreFocusKey = null;

            StyleRows();
            SelectDefault(sameState ? previousIndex : null);
            UpdateDividers();
            if (!sameState)
            {
                Rows.anchoredPosition = Vector2.zero;
            }

            FitRows();
        }

        private void PushView(BandColumnState state, string focusKey = null, string settingTitle = null,
            List<(string Label, bool Current, Action Pick)> settingOptions = null)
        {
            _session.PushView(Model, state, focusKey, settingTitle, settingOptions);
        }

        private void PopView()
        {
            _session.PopView(Model);
        }

        private void OpenPicker(bool canCancel)
        {
            _session.OpenPicker(Model, canCancel);
        }

        private void PickInstrument(Instrument instrument)
        {
            _session.PickInstrument(Model, instrument);
        }

        private void ToggleReady()
        {
            _session.ToggleReady(Model);
        }

        public void ApplyReadyVisuals()
        {
            if (Ready)
            {
                SlideDownAndCollapse();
            }
            else
            {
                RebuildFromCurrentTop();
                if (_readyButton != null)
                {
                    _nav.SelectButton(_readyButton);
                }
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || !_session.CanClickCards ||
                State != BandColumnState.Main)
            {
                return;
            }

            if (eventData.pointerPressRaycast.gameObject == null)
            {
                return;
            }

            if (Ready)
            {
                ToggleReady();
                return;
            }

            if (Expand())
            {
                return;
            }

            bool onHeader = !RectTransformUtility.RectangleContainsScreenPoint((RectTransform) _scroll.transform,
                eventData.position, eventData.pressEventCamera);
            if (_session.IsOvershell && onHeader)
            {
                Back();
            }
        }

        public bool Expand()
        {
            return _session.ExpandColumn(Model);
        }

        public void RebuildFromCurrentTop()
        {
            float top = _card.anchoredPosition.y + _card.rect.height;
            Rebuild();
            RiseFrom(top - _card.rect.height);
        }

        private void ConfirmDifficulty(Difficulty difficulty)
        {
            _session.ConfirmDifficulty(Model, difficulty);
            _nav.SelectButton(_readyButton);
        }

        private void CycleHarmonyPart()
        {
            _session.CycleHarmonyPart(Model);
        }

        private void OnSelectionChanged(NavigatableBehaviour selected, SelectionOrigin origin)
        {
            if (_editingRow != null && selected != _editingRow.Button)
            {
                StopEditing();
            }

            UpdateDividers();

            if (origin == SelectionOrigin.Mouse)
            {
                _session.FocusColumn(Model);
            }
            else
            {
                ScrollToSelection();
            }
        }

        private void SetPlayerHeading()
        {
            string mode = Player.Profile.GameMode.ToResourceName();
            _header.SetIcon(mode == null ? null : $"InstrumentIcons[{mode}]");
            SetHeading(Player.Profile.Name, showIcon: true);
            if (Ready)
            {
                _heading.color = BandSelectMenu.READY_COLOR;
            }
        }

    }

    internal static class BandColumnExtensions
    {
        public static void SelectButton(this NavigationGroup nav, NavigatableBehaviour button)
        {
            for (int i = 0; i < nav.Navigatables.Count; i++)
            {
                if (nav.Navigatables[i] == button)
                {
                    nav.SelectAt(i);
                    return;
                }
            }

            nav.SelectAt(0);
        }

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
