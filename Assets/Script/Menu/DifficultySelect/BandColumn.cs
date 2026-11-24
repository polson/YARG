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
    internal enum BandColumnState
    {
        ChooseProfile,
        Main,
        Difficulty,
        Instrument,
        Adjustments,
        Modifiers,
        Accessibility,
        OpenLane,
        SittingOut,
        Position,
    }

    internal sealed class BandColumn : MonoBehaviour, IPointerClickHandler, IScrollHandler
    {
        private const float PROFILE_ROW_HEIGHT = 64f;
        private const float AVATAR_MARGIN = 6f;
        private const float AVATAR_EDGE = 6f;
        private const float AVATAR_WIDTH_SHARE = 0.25f;
        private const float HEADER_ICON_SIZE = 38f;
        private const float HEADER_MARGIN = 40f;
        private const float HEADER_SPACING = 10f;
        private const float HEADER_FONT_SIZE_MIN = 6f;
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

        private BandSelectMenu _menu;
        private Color _borderColor;
        private Color _headingColor;

        private PlayerCardHeader _header;
        private float _appliedWidth = -1f;
        private readonly List<(DifficultyItem Row, float Height)> _profileRows = new();
        private Tween _rise;
        private Tween _collapse;
        private readonly List<Modifier> _modifiers = new();
        [System.NonSerialized]
        private Modifier _excusableModifiers;
        private readonly List<Texture2D> _avatarTextures = new();
        private readonly List<Difficulty> _difficulties = new();
        private readonly List<(ModifierItem Item, Modifier Modifier)> _modifierToggles = new();

        private BandColumnState _builtState;
        private BandColumnState _pickerReturnState;
        private bool _pickerCanCancel;
        private bool _locked;
        private bool _collapsed;
        private int _expandedBottomPadding;
        private string _warning;
        private NavigatableBehaviour _readyButton;
        private NavigatableBehaviour _difficultyButton;
        private NavigatableBehaviour _defaultButton;
        private NavigatableBehaviour _moveLeftButton;
        private NavigatableBehaviour _moveRightButton;
        private readonly List<(Transform Row, GameObject Divider)> _dividers = new();

        public YargPlayer Player { get; set; }

        public BandColumnState State { get; private set; }
        public bool Ready { get; private set; }

        public bool IsJoining => State == BandColumnState.ChooseProfile;
        private bool IsCollapsed => State == BandColumnState.Main && (Ready || _collapsed);
        public bool IsExpanded => !IsCollapsed;
        public bool IsAtTopLevel => State switch
        {
            BandColumnState.Main       => !Ready,
            BandColumnState.SittingOut => true,
            BandColumnState.Instrument => !_pickerCanCancel,
            _                          => false,
        };
        public bool IsSittingOut => State == BandColumnState.SittingOut || HasNoPlayableInstrument;
        private bool HasNoPlayableInstrument => State == BandColumnState.Instrument &&
            _menu.Instruments.All(instrument => _menu.GetInstrumentBlocker(this, instrument, out _) is not null);
        public bool HasChosenInstrument => State switch
        {
            BandColumnState.ChooseProfile => false,
            BandColumnState.SittingOut    => false,
            BandColumnState.Instrument    => _pickerCanCancel && _pickerReturnState == BandColumnState.Main,
            _                             => true,
        };
        public bool IsDecided => IsSittingOut || (State == BandColumnState.Main && Ready);

        public NavigationGroup Nav => _nav;
        public RectTransform Card => _card;

        private RectTransform Rows => _scroll.content;

        private void Awake()
        {
            _header = new PlayerCardHeader(_heading, _icon);
            _iconLayout = _icon.GetComponent<LayoutElement>();
            _headerContent = _icon.GetComponentInParent<HorizontalLayoutGroup>(true);
            _heading.fontSizeMin = HEADER_FONT_SIZE_MIN;
            _expandedBottomPadding = _cardLayout.padding.bottom;
            _borderColor = _border.color;
            _headingColor = _heading.color;
        }

        public void Initialize(BandSelectMenu menu, YargPlayer player)
        {
            _menu = menu;
            Player = player;
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

        public void BeginJoin()
        {
            Ready = false;
            State = BandColumnState.ChooseProfile;
            Rebuild();
        }

        public void Open()
        {
            if (_menu.IsOvershell)
            {
                Collapse();
            }
            else
            {
                OpenForSong();
            }
        }

        public void Collapse()
        {
            ResetForOvershell();
            Rebuild();
        }

        public void CompleteJoin()
        {
            if (_menu.IsOvershell)
            {
                ResetForOvershell();
                RebuildFromCurrentTop();
            }
            else
            {
                OpenForSong();
            }
        }

        private void OpenForSong()
        {
            Player.Profile.RestoreSavedModifiers();
            Ready = false;
            _warning = null;
            _locked = false;
            _collapsed = false;

            if (GlobalVariables.State.CurrentCareer is not null && Player.Profile.IsBot)
            {
                _locked = true;
                State = BandColumnState.SittingOut;
                Rebuild();
                return;
            }

            var profile = Player.Profile;
            foreach (var candidate in new[] { profile.PreferredInstrument, profile.CurrentInstrument })
            {
                if (!_menu.Instruments.Contains(candidate))
                {
                    continue;
                }

                if (_menu.GetInstrumentBlocker(this, candidate, out var mode) is not null)
                {
                    continue;
                }

                SetGameMode(mode);
                profile.CurrentInstrument = candidate;
                profile.ResolveHarmonyIndex(_menu.MaxHarmonyIndex);
                CollectOptions();
                State = BandColumnState.Main;
                Rebuild();
                return;
            }

            OpenPicker(canCancel: false);
        }

        private void ResetForOvershell()
        {
            Ready = false;
            _warning = null;
            _locked = false;
            _collapsed = true;
            State = BandColumnState.Main;
        }

        public void Back()
        {
            switch (State)
            {
                case BandColumnState.ChooseProfile:
                    _menu.Disconnect(this);
                    break;
                case BandColumnState.Main when Ready:
                    ToggleReady();
                    break;
                case BandColumnState.Main when !_collapsed:
                    _collapsed = true;
                    SlideDownAndCollapse();
                    break;
                case BandColumnState.SittingOut:
                    OpenPicker(canCancel: true);
                    break;
                case BandColumnState.Instrument when _pickerCanCancel:
                    SetState(_pickerReturnState);
                    break;
                case BandColumnState.Instrument:
                    _menu.Disconnect(this);
                    break;
                case BandColumnState.Difficulty:
                case BandColumnState.Adjustments:
                case BandColumnState.Position:
                    SetState(BandColumnState.Main);
                    break;
                case BandColumnState.Modifiers:
                case BandColumnState.Accessibility:
                    SetState(BandColumnState.Adjustments);
                    break;
                case BandColumnState.OpenLane:
                    SetState(BandColumnState.Modifiers);
                    break;
            }
        }

        public void Rebuild()
        {
            int? previousIndex = _nav.SelectedIndex;
            bool sameState = _builtState == State;

            if (_collapse is not null)
            {
                _collapse.Kill();
                _collapse = null;
                _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, 0f);
            }

            _nav.ClearNavigatables();
            ClearAvatars();
            _profileRows.Clear();
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
            }

            _builtState = State;

            StyleRows();
            SelectDefault(sameState ? previousIndex : null);
            UpdateDividers();
            if (!sameState)
            {
                Rows.anchoredPosition = Vector2.zero;
            }

            FitRows();
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

            var rows = Rows;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rows);

            float contentHeight = LayoutUtility.GetPreferredHeight(rows);
            float maxHeight = Mathf.Max(ROW_HEIGHT, _menu.MaxCardHeight - _cardLayout.padding.vertical);
            bool scrollable = contentHeight > maxHeight;

            var scrollbar = (RectTransform) _scroll.verticalScrollbar.transform;
            _viewportLayout.preferredHeight = Mathf.Min(contentHeight, maxHeight);
            scrollbar.gameObject.SetActive(scrollable);
            _rowLayout.padding.right = scrollable ? Mathf.CeilToInt(scrollbar.sizeDelta.x) : 0;

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            ScrollToSelection();
        }

        public void OnScroll(PointerEventData eventData)
        {
            _scroll.OnScroll(eventData);
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

        private void SetState(BandColumnState state)
        {
            _collapsed = false;
            State = state;
            Rebuild();
            _menu.OnColumnChanged();
        }

        private void OpenPicker(bool canCancel)
        {
            if (_locked)
            {
                return;
            }

            _pickerReturnState = State;
            _pickerCanCancel = canCancel;
            Ready = false;
            SetState(BandColumnState.Instrument);
        }

        private void BuildProfilePicker()
        {
            SetHeading(Localize.Key("Menu.DifficultySelect.Band.ChooseProfile"));

            foreach (var profile in PlayerContainer.Profiles.Where(p => !p.IsBot))
            {
                AddProfileRow(profile);
            }

            foreach (var profile in PlayerContainer.Profiles.Where(p => p.IsBot))
            {
                AddProfileRow(profile);
            }

            AddRow(null, Localize.Key("Menu.Common.Cancel"), CancelProfilePicker, true, _smallPrefab);
        }

        private void CancelProfilePicker()
        {
            if (Player is null)
            {
                _menu.CloseProfilesInUse();
            }
            else
            {
                _menu.Disconnect(this);
            }
        }

        public void ShowProfilesInUse()
        {
            State = BandColumnState.ChooseProfile;
            Rebuild();
        }

        private void AddProfileRow(YargProfile profile)
        {
            bool current = Player != null && profile == Player.Profile;
            bool taken = !current && PlayerContainer.IsProfileTaken(profile);

            var row = AddRow(null, profile.Name, () => _menu.ChooseJoinProfile(this, profile), !taken, _profilePrefab,
                PROFILE_ROW_HEIGHT);
            AddAvatar(row, profile, PROFILE_ROW_HEIGHT);

            if (current)
            {
                _defaultButton = row.Button;
            }
        }

        private void AddAvatar(DifficultyItem row, YargProfile profile, float rowHeight)
        {
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

            _profileRows.Add((row, rowHeight));
            FitProfileRow(row, rowHeight);
        }

        private void FitProfileRow(DifficultyItem row, float rowHeight)
        {
            float fullSize = rowHeight - AVATAR_EDGE * 2f;
            float size = Mathf.Min(fullSize, _layout.preferredWidth * AVATAR_WIDTH_SHARE);
            float scale = size / fullSize;
            float edge = AVATAR_EDGE * 2f * scale;
            float gap = AVATAR_MARGIN * scale;
            row.FitAvatar(rowHeight, size, edge);

            int inset = Mathf.CeilToInt(edge + size + gap);
            row.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(inset, Mathf.CeilToInt(edge), 0, 0);
        }

        private void ClearAvatars()
        {
            foreach (var texture in _avatarTextures)
            {
                Destroy(texture);
            }

            _avatarTextures.Clear();
        }

        private void BuildMain()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (IsCollapsed)
            {
                return;
            }

            if (_menu.IsOvershell)
            {
                AddPositionRow();
                AddRow(null, Localize.Key("Menu.DifficultySelect.Band.DropOut"), () => _menu.Disconnect(this), true,
                    _smallPrefab);
                return;
            }

            if (_warning is not null)
            {
                var warning = AddLabel(_warning);
                warning.SetTextColor(WARNING_COLOR);
            }

            _readyButton = AddRow(null,
                Localize.Key("Menu.DifficultySelect.Band", Ready ? "Unready" : "Ready"),
                ToggleReady, true, _readyPrefab).Button;

            AddInstrumentRow();

            bool canPickDifficulty = !Ready && _difficulties.Count > 0;
            var difficulty = AddRow(Localize.Key("Menu.DifficultySelect.Difficulty"),
                profile.CurrentDifficulty.ToLocalizedName(), () => SetState(BandColumnState.Difficulty),
                canPickDifficulty, height: TWO_LINE_ROW_HEIGHT);
            if (canPickDifficulty)
            {
                _difficultyButton = difficulty.Button;
            }

            if (profile.CurrentInstrument == Instrument.Harmony && _menu.MaxHarmonyIndex > 1)
            {
                AddRow(null,
                    Localize.KeyFormat("Menu.DifficultySelect.Band.Part", profile.HarmonyIndex + 1),
                    CycleHarmonyPart, !Ready);
            }

            string adjustments = PlayerSelectionRules.BuildAdjustmentsSummary(profile, _modifiers,
                _excusableModifiers, out int optionCount);
            AddStateRow(Localize.Key("Menu.DifficultySelect.Adjustments"), adjustments,
                BandColumnState.Adjustments, !Ready, MultiLineHeight(optionCount));

            AddPositionRow();
            AddRow(null, Localize.Key("Menu.DifficultySelect.Band.DropOut"), () => _menu.Disconnect(this), true,
                _smallPrefab);
        }

        private void AddPositionRow()
        {
            AddRow(null, Localize.Key("Menu.DifficultySelect.Band.Position"),
                () => SetState(BandColumnState.Position), true);
        }

        private void BuildPosition()
        {
            SetPlayerHeading();

            _moveLeftButton = AddRow(null, Localize.Key("Menu.DifficultySelect.Band.MoveLeft"), () => Move(-1),
                _menu.CanMoveColumn(this, -1)).Button;
            _moveRightButton = AddRow(null, Localize.Key("Menu.DifficultySelect.Band.MoveRight"), () => Move(1),
                _menu.CanMoveColumn(this, 1)).Button;

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), () => SetState(BandColumnState.Main), true);
        }

        private void Move(int direction)
        {
            if (!_menu.MoveColumn(this, direction))
            {
                return;
            }

            Rebuild();
            var button = _menu.CanMoveColumn(this, direction)
                ? (direction < 0 ? _moveLeftButton : _moveRightButton)
                : (direction < 0 ? _moveRightButton : _moveLeftButton);
            if (_nav.Navigatables.Contains(button))
            {
                _nav.SelectAt(IndexOf(button));
            }
        }

        private void AddInstrumentRow()
        {
            var profile = Player.Profile;
            var song = GlobalVariables.State.CurrentSong;
            var instruments = profile.GameMode.PossibleInstrumentsForSong(song)
                .Where(instrument => _menu.Instruments.Contains(instrument))
                .ToList();
            bool showRings = _menu.RingTemplate != null && instruments.Count > 0;

            float height = TWO_LINE_ROW_HEIGHT;
            if (showRings)
            {
                height += DifficultyItemRings.RingRowHeight(DifficultyItemRings.INSTRUMENT_RING_SIZE);
            }

            var row = AddRow(Localize.Key("Menu.DifficultySelect.Instrument"),
                PlayerSelectionRules.GetInstrumentDisplayName(profile.CurrentInstrument, profile.GameMode),
                () => OpenPicker(canCancel: true), !Ready, height: height);

            if (showRings)
            {
                row.gameObject.AddComponent<DifficultyItemRings>()
                    .ShowInstrumentTiers(_menu.RingTemplate, instruments, profile.CurrentInstrument, song);
            }
        }

        private void BuildDifficultyPicker()
        {
            SetPlayerHeading();

            var current = Player.Profile.CurrentDifficulty;
            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                if (!_difficulties.Contains(difficulty))
                {
                    continue;
                }

                var row = AddRow(null, difficulty.ToLocalizedName(), () => ConfirmDifficulty(difficulty), true);
                if (difficulty == current)
                {
                    row.SetTextColor(BandSelectMenu.READY_COLOR);
                    _defaultButton = row.Button;
                }
            }
        }

        private void BuildInstrumentPicker()
        {
            var profile = Player.Profile;

            SetPlayerHeading();
            var song = GlobalVariables.State.CurrentSong;

            bool anyPlayable = false;
            foreach (var instrument in _menu.Instruments)
            {
                if (_menu.GetInstrumentBlocker(this, instrument, out var mode) is not null)
                {
                    continue;
                }

                anyPlayable = true;
                var row = AddRow(null, PlayerSelectionRules.GetInstrumentLabelWithTier(song, instrument, mode),
                    () => PickInstrument(instrument), true, height: TWO_LINE_ROW_HEIGHT);

                if (instrument == profile.CurrentInstrument && _pickerCanCancel)
                {
                    _nav.SelectAt(IndexOf(row.Button));
                }
            }

            if (!anyPlayable)
            {
                var label = AddLabel(Localize.Key("Menu.DifficultySelect.Band.NoPlayableInstruments"));
                label.UseWrappedText();
                label.GetComponent<LayoutElement>().preferredHeight = TWO_LINE_ROW_HEIGHT;
            }
        }

        private void BuildAdjustments()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            AddStateRow(Localize.Key("Menu.DifficultySelect.Modifiers"),
                PlayerSelectionRules.BuildModifierSummary(profile, _modifiers, _excusableModifiers),
                BandColumnState.Modifiers);

            if (PlayerSelectionRules.HasAccessibilityOptions(profile, _modifiers))
            {
                AddStateRow(Localize.Key("Menu.DifficultySelect.Accessibility"),
                    PlayerSelectionRules.BuildAccessibilitySummary(profile, _modifiers),
                    BandColumnState.Accessibility);
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), () => SetState(BandColumnState.Main), true);
        }

        private void BuildModifiers()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            foreach (var modifier in _modifiers)
            {
                if ((modifier & PlayerSelectionRules.ACCESSIBILITY_MODIFIERS) == 0)
                {
                    AddModifierToggle(modifier);
                }
            }

            if (profile.GameMode == GameMode.ProKeys)
            {
                AddStateRow(Localize.Key("Menu.DifficultySelect.DedicatedOpenLane"),
                    profile.OpenLaneDisplayType.ToLocalizedName(), BandColumnState.OpenLane);
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), () => SetState(BandColumnState.Adjustments),
                true);
        }

        private void BuildAccessibility()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (PlayerSelectionRules.SupportsLeftyFlip(profile.GameMode))
            {
                AddToggle(Localize.Key("Menu.DifficultySelect.LeftyFlip"), profile.LeftyFlip,
                    on => profile.LeftyFlip = on);
            }

            if (PlayerSelectionRules.SupportsRangeShifts(profile.GameMode))
            {
                AddToggle(Localize.Key("Menu.DifficultySelect.NoRangeShifts"),
                    PlayerSelectionRules.HasNoRangeShifts(profile, _modifiers),
                    on => PlayerSelectionRules.SetNoRangeShifts(profile, _modifiers, on));
            }

            foreach (var modifier in _modifiers)
            {
                if ((modifier & PlayerSelectionRules.ACCESSIBILITY_MODIFIERS) != 0 && modifier != Modifier.RangeCompress)
                {
                    AddModifierToggle(modifier);
                }
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), () => SetState(BandColumnState.Adjustments),
                true);
        }

        private void BuildOpenLane()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            foreach (var displayType in PlayerSelectionRules.OPEN_LANE_OPTIONS)
            {
                var row = AddRow(null, displayType.ToLocalizedName(), () =>
                {
                    profile.OpenLaneDisplayType = displayType;
                    SetState(BandColumnState.Modifiers);
                }, true);

                if (displayType == profile.OpenLaneDisplayType)
                {
                    row.SetTextColor(BandSelectMenu.READY_COLOR);
                    _defaultButton = row.Button;
                }
            }
        }

        private void BuildSittingOut()
        {
            SetPlayerHeading();
            AddRow(null, Localize.Key("Menu.DifficultySelect.Band.SittingOut"), () => OpenPicker(canCancel: true), !_locked);
        }

        private void PickInstrument(Instrument instrument)
        {
            if (_menu.GetInstrumentBlocker(this, instrument, out var mode) is not null)
            {
                Rebuild();
                return;
            }

            SetGameMode(mode);
            PlayerSelectionRules.PickInstrument(Player.Profile, instrument, _menu.Instruments, _menu.MaxHarmonyIndex);
            CollectOptions();
            Ready = false;
            _warning = null;
            SetState(BandColumnState.Main);
        }

        private void ToggleReady()
        {
            if (!Ready)
            {
                if (Player.IsMissingMicrophone)
                {
                    _warning = Localize.Key("Menu.DifficultySelect.Band.NoMicrophone");
                    Rebuild();
                    return;
                }

                if (Player.IsMissingController)
                {
                    _warning = Localize.Key("Menu.DifficultySelect.Band.NoController");
                    Rebuild();
                    return;
                }
            }

            _warning = null;
            _collapsed = false;
            Ready = !Ready;

            if (Ready)
            {
                SlideDownAndCollapse();
            }
            else
            {
                RebuildFromCurrentTop();
                if (_readyButton != null)
                {
                    _nav.SelectAt(IndexOf(_readyButton));
                }
            }

            _menu.OnColumnChanged();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || !_menu.CanClickCards || State != BandColumnState.Main)
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
            if (_menu.IsOvershell && onHeader)
            {
                Back();
            }
        }

        public bool Expand()
        {
            bool canExpand = State == BandColumnState.Main && !Ready && _collapsed;
            if (!canExpand)
            {
                return false;
            }

            _collapsed = false;
            RebuildFromCurrentTop();
            return true;
        }

        private void RebuildFromCurrentTop()
        {
            float top = _card.anchoredPosition.y + _card.rect.height;
            Rebuild();
            RiseFrom(top - _card.rect.height);
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

        private void ConfirmDifficulty(Difficulty difficulty)
        {
            Player.Profile.CurrentDifficulty = Player.Profile.DifficultyFallback = difficulty;
            SetState(BandColumnState.Main);
            _nav.SelectAt(IndexOf(_readyButton));
        }

        private void CycleHarmonyPart()
        {
            var profile = Player.Profile;
            profile.HarmonyIndex = (byte) ((profile.HarmonyIndex + 1) % _menu.MaxHarmonyIndex);
            Rebuild();
        }

        private void OnSelectionChanged(NavigatableBehaviour selected, SelectionOrigin origin)
        {
            UpdateDividers();

            if (origin == SelectionOrigin.Mouse)
            {
                _menu.FocusColumn(this);
            }
            else
            {
                ScrollToSelection();
            }
        }

        private void SetGameMode(GameMode mode)
        {
            var profile = Player.Profile;
            if (profile.GameMode == mode)
            {
                return;
            }

            profile.GameMode = mode;
            Player.DeviceInfo.RefreshGameMode();
            PlayerContainer.NotifyProfileChanged(profile);
        }

        private void CollectOptions()
        {
            _excusableModifiers = PlayerSelectionRules.CollectPossibleModifiers(Player.Profile, _modifiers);
            PlayerSelectionRules.CollectPossibleDifficulties(Player.Profile, _menu.Songs, _difficulties);
        }

        private void SetPlayerHeading()
        {
            string mode = Player.Profile.GameMode.ToResourceName();
            _header.SetIcon(mode is null ? null : $"InstrumentIcons[{mode}]");
            SetHeading(Player.Profile.Name, showIcon: true);
            if (Ready)
            {
                _heading.color = BandSelectMenu.READY_COLOR;
            }
        }

        private void FitHeader()
        {
            float width = _layout.preferredWidth;
            float scale = Mathf.Clamp01(width * AVATAR_WIDTH_SHARE / HEADER_ICON_SIZE);
            float iconSize = HEADER_ICON_SIZE * scale;
            _iconLayout.preferredWidth = iconSize;
            _iconLayout.preferredHeight = iconSize;
            _icon.rectTransform.sizeDelta = new Vector2(iconSize, iconSize);
            _headerContent.spacing = HEADER_SPACING * scale;
            _header.Fit(width, HEADER_MARGIN * scale);
        }

        private void SetHeading(string text, bool showIcon = false)
        {
            _heading.text = text;
            _heading.color = _headingColor;
            _heading.gameObject.SetActive(!string.IsNullOrEmpty(text));
            _icon.gameObject.SetActive(showIcon && _icon.sprite != null);
            FitHeader();
        }

        private DifficultyItem AddLabel(string text)
        {
            var label = Instantiate(_itemPrefab, Rows);
            label.InitializeAsTitle(text);
            label.UseCompactText(SMALL_FONT_SIZE);
            return label;
        }

        private DifficultyItem AddRow(string header, string body, UnityAction action, bool interactable,
            DifficultyItem prefab = null, float height = ROW_HEIGHT)
        {
            var row = Instantiate(prefab != null ? prefab : _itemPrefab, Rows);

            if (header is null)
            {
                row.Initialize(body, action);
            }
            else
            {
                row.Initialize(header, body, action);
                row.UseCompactText(LABEL_FONT_SIZE, VALUE_FONT_SIZE);
            }

            row.GetComponent<LayoutElement>().preferredHeight = height;
            row.SetInteractable(interactable);

            if (interactable)
            {
                _nav.AddNavigatable(row.Button);
            }

            return row;
        }

        private DifficultyItem AddStateRow(string header, string body, BandColumnState state,
            bool interactable = true, float height = TWO_LINE_ROW_HEIGHT)
        {
            var row = AddRow(header, body, () => SetState(state), interactable, height: height);
            if (interactable && _builtState == state)
            {
                _defaultButton = row.Button;
            }

            return row;
        }

        private static float MultiLineHeight(int lineCount)
        {
            return TWO_LINE_ROW_HEIGHT + Mathf.Max(0, lineCount - 1) * MODIFIER_LINE_HEIGHT;
        }

        private void AddModifierToggle(Modifier modifier)
        {
            var profile = Player.Profile;
            var toggle = AddToggle(modifier.ToLocalizedName(), profile.IsModifierActive(modifier), on =>
            {
                if (on)
                {
                    profile.AddSingleModifier(modifier);
                }
                else
                {
                    profile.RemoveModifiers(modifier);
                }

                foreach (var (item, itemModifier) in _modifierToggles)
                {
                    item.Active = profile.IsModifierActive(itemModifier);
                }
            });

            _modifierToggles.Add((toggle, modifier));
        }

        private ModifierItem AddToggle(string label, bool active, System.Action<bool> onChanged)
        {
            var toggle = Instantiate(_modifierPrefab, Rows);
            toggle.Initialize(label, active, onChanged);

            _nav.AddNavigatable(toggle);
            return toggle;
        }

        private int IndexOf(NavigatableBehaviour navigatable)
        {
            for (int i = 0; i < _nav.Navigatables.Count; i++)
            {
                if (_nav.Navigatables[i] == navigatable)
                {
                    return i;
                }
            }

            return 0;
        }
    }
}
