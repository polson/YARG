using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Filters;
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
        Modifiers,
        SittingOut,
    }

    internal sealed class BandColumn
    {
        private const float HEADER_HEIGHT = 73f;
        private const float HEADER_MARGIN = 40f;
        private const float HEADING_MIN_FONT_SIZE = 14f;
        private const float SCROLL_SENSITIVITY = 30f;
        private const float PROFILE_ROW_HEIGHT = 64f;
        private const float AVATAR_MARGIN = 6f;
        private const float ROW_HEIGHT = 52f;
        private const float TWO_LINE_ROW_HEIGHT = 72f;
        private const float ROW_FONT_SIZE = 26f;
        private const float SMALL_FONT_SIZE = 20f;
        private const float LABEL_FONT_SIZE = 18f;
        private const float VALUE_FONT_SIZE = 28f;
        private const float MODIFIER_LINE_HEIGHT = 34f;

        private const float UNFOCUSED_BORDER_BRIGHTNESS = 0.5f;
        private static readonly Color WARNING_COLOR = new Color32(0xFB, 0x44, 0x3F, 0xFF);

        private readonly BandSelectMenu _menu;

        private readonly RectTransform _card;
        private readonly Image _border;
        private readonly Color _borderColor;
        private readonly RectTransform _rows;
        private readonly RectTransform _viewport;
        private readonly LayoutElement _viewportLayout;
        private readonly VerticalLayoutGroup _rowLayout;
        private readonly Scrollbar _scrollbar;
        private readonly float _scrollbarWidth;
        private readonly TextMeshProUGUI _heading;
        private readonly LayoutElement _headingLayout;
        private readonly Color _headingColor;
        private readonly Image _icon;
        private readonly float _iconWidth;
        private readonly float _headingSpacing;

        private AsyncOperationHandle<Sprite> _iconHandle;
        private string _iconKey;
        private float _width;
        private Tween _rise;
        private readonly List<Modifier> _modifiers = new();
        private readonly List<Texture2D> _avatarTextures = new();
        private readonly List<Difficulty> _difficulties = new();
        private readonly Dictionary<Difficulty, DifficultyItem> _difficultyItems = new();
        private readonly List<(ModifierItem Item, Modifier Modifier)> _modifierToggles = new();

        private BandColumnState _builtState;
        private BandColumnState _pickerReturnState;
        private bool _pickerCanCancel;
        private bool _switchingProfile;
        private bool _locked;
        private string _warning;
        private NavigatableBehaviour _readyButton;
        private NavigatableBehaviour _difficultyButton;
        private bool _selectReady;

        public YargPlayer Player;
        public InputDevice JoinDevice;
        public bool JoinDeviceOwned;
        public YargProfile CreatedProfile;

        public readonly GameObject Root;
        public readonly NavigationGroup Nav;
        public readonly LayoutElement Layout;

        public BandColumnState State { get; private set; }
        public bool Ready { get; private set; }

        public bool IsJoining => State == BandColumnState.ChooseProfile && !_switchingProfile;
        public bool IsAtTopLevel => State switch
        {
            BandColumnState.Main       => !Ready,
            BandColumnState.SittingOut => true,
            BandColumnState.Instrument => !_pickerCanCancel,
            _                          => false,
        };
        public bool IsSittingOut => State == BandColumnState.SittingOut;
        public bool IsDecided => IsSittingOut || (State == BandColumnState.Main && Ready);

        public BandColumn(BandSelectMenu menu, Transform parent, YargPlayer player, bool slideIn)
        {
            _menu = menu;
            Player = player;

            Root = new GameObject("Column", typeof(RectTransform));
            Root.transform.SetParent(parent, false);
            ((RectTransform) Root.transform).sizeDelta = Vector2.zero;

            _card = (RectTransform) menu.CreateCard(Root.transform).transform;
            BandSelectMenu.PlaceCard(_card);
            _card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var card = _card.transform;

            _border = card.Find("Border").GetComponent<Image>();
            _borderColor = _border.color;

            var header = card.Find("Background/Header Gradient/Header Content");
            _heading = header.Find("Heading").GetComponent<TextMeshProUGUI>();
            _headingLayout = _heading.GetComponent<LayoutElement>();
            _headingColor = _heading.color;
            _heading.fontSizeMin = HEADING_MIN_FONT_SIZE;
            _heading.textWrappingMode = TextWrappingModes.NoWrap;
            _icon = header.Find("Instrument Icon").GetComponent<Image>();
            _iconWidth = _icon.GetComponent<LayoutElement>().preferredWidth;
            _headingSpacing = header.GetComponent<HorizontalLayoutGroup>().spacing;

            _rows = (RectTransform) card.Find("Options");
            for (int i = _rows.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(_rows.GetChild(i).gameObject);
            }

            _viewport = (RectTransform) new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D)).transform;
            _viewport.SetParent(card, false);
            _viewport.SetSiblingIndex(_rows.GetSiblingIndex());
            _viewportLayout = _viewport.gameObject.AddComponent<LayoutElement>();

            _rows.SetParent(_viewport, false);
            _rows.anchorMin = Vector2.up;
            _rows.anchorMax = Vector2.one;
            _rows.pivot = new Vector2(0.5f, 1f);
            _rows.anchoredPosition = Vector2.zero;
            _rows.sizeDelta = Vector2.zero;
            _rows.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = _viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = _rows;
            scroll.viewport = _viewport;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            scroll.scrollSensitivity = SCROLL_SENSITIVITY;

            _scrollbar = Object.Instantiate(menu.Prefabs.ScrollbarPrefab, _viewport);
            _scrollbar.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            _scrollbarWidth = ((RectTransform) _scrollbar.transform).sizeDelta.x;
            scroll.verticalScrollbar = _scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            _rowLayout = _rows.GetComponent<VerticalLayoutGroup>();
            _rowLayout.spacing = 2f;
            _rowLayout.childAlignment = TextAnchor.UpperCenter;
            _rowLayout.childControlWidth = true;
            _rowLayout.childControlHeight = true;
            _rowLayout.childForceExpandWidth = true;
            _rowLayout.childForceExpandHeight = false;

            var layout = _card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(2, 2, (int) HEADER_HEIGHT, (int) BandSelectMenu.CARD_BOTTOM_INSET);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Layout = Root.AddComponent<LayoutElement>();
            Layout.flexibleWidth = 0f;

            Nav = Root.AddComponent<NavigationGroup>();
            Nav.SelectionChanged += OnSelectionChanged;

            if (slideIn)
            {
                RiseFrom(-BandSelectMenu.SLIDE_DISTANCE);
            }
        }

        public RectTransform Card => _card;

        public void RiseFrom(float offset)
        {
            _rise?.Kill();
            _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, offset);
            _rise = _card.DOAnchorPosY(0f, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true);
        }

        public void Destroy()
        {
            _card.DOKill();
            Nav.SelectionChanged -= OnSelectionChanged;
            ReleaseIcon();
            ClearAvatars();
            Object.Destroy(Root);
        }

        public void DismissAndDestroy()
        {
            Nav.SelectionChanged -= OnSelectionChanged;
            Layout.ignoreLayout = true;
            Root.AddComponent<CanvasGroup>().blocksRaycasts = false;

            _card.DOKill();
            _card.DOAnchorPosY(-BandSelectMenu.SLIDE_DISTANCE, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.InCubic)
                .SetUpdate(true)
                .SetLink(Root)
                .OnComplete(() =>
                {
                    ReleaseIcon();
                    ClearAvatars();
                    Object.Destroy(Root);
                });
        }

        public void SetFocused(bool focused)
        {
            _border.color = focused
                ? _borderColor
                : (_borderColor * UNFOCUSED_BORDER_BRIGHTNESS).WithAlpha(_borderColor.a);
        }

        public void SetWidth(float width)
        {
            Layout.preferredWidth = width;
            _card.sizeDelta = new Vector2(width, _card.sizeDelta.y);
            _width = width;
            FitHeading();
        }

        public void BeginJoin(InputDevice device, bool deviceOwned, YargProfile createdProfile)
        {
            JoinDevice = device;
            JoinDeviceOwned = deviceOwned;
            CreatedProfile = createdProfile;
            Ready = false;
            _switchingProfile = false;
            State = BandColumnState.ChooseProfile;
            Rebuild();
        }

        public void OpenForSong()
        {
            JoinDevice = null;
            CreatedProfile = null;
            Ready = false;
            _switchingProfile = false;
            _warning = null;
            _locked = false;

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

                ApplyInstrument(candidate, mode, false);
                State = BandColumnState.Main;
                Rebuild();
                return;
            }

            OpenPicker(false);
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
                case BandColumnState.Main when Player.DeviceInfo.HasControllerAssigned:
                    OpenProfileSwitch();
                    break;
                case BandColumnState.Main:
                case BandColumnState.SittingOut:
                    OpenPicker(true);
                    break;
                case BandColumnState.Instrument when _pickerCanCancel:
                    SetState(_pickerReturnState);
                    break;
                case BandColumnState.Difficulty:
                case BandColumnState.Modifiers:
                    SetState(BandColumnState.Main);
                    break;
            }
        }

        public void Rebuild()
        {
            int? previousIndex = Nav.SelectedIndex;
            bool sameState = _builtState == State;

            Nav.ClearNavigatables();
            _difficultyItems.Clear();
            ClearAvatars();
            _modifierToggles.Clear();
            _readyButton = null;
            _difficultyButton = null;
            for (int i = _rows.childCount - 1; i >= 0; i--)
            {
                var child = _rows.GetChild(i).gameObject;
                child.SetActive(false);
                Object.Destroy(child);
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
                case BandColumnState.Modifiers:
                    BuildModifiers();
                    break;
                case BandColumnState.SittingOut:
                    BuildSittingOut();
                    break;
            }

            _builtState = State;

            SelectDefault(sameState, previousIndex);
            FitRows(!sameState);
        }

        private void SelectDefault(bool sameState, int? previousIndex)
        {
            bool selectReady = _selectReady;
            _selectReady = false;

            if (Nav.Count == 0 || Nav.SelectedIndex is not null)
            {
                return;
            }

            var currentDifficulty = Player.Profile.CurrentDifficulty;
            if (sameState && previousIndex is { } index)
            {
                Nav.SelectAt(Mathf.Clamp(index, 0, Nav.Count - 1));
            }
            else if (_readyButton != null && (Ready || selectReady))
            {
                Nav.SelectAt(IndexOf(_readyButton));
            }
            else if (_difficultyButton != null && !Ready)
            {
                Nav.SelectAt(IndexOf(_difficultyButton));
            }
            else if (State == BandColumnState.Difficulty && _difficulties.Contains(currentDifficulty) &&
                _difficultyItems.TryGetValue(currentDifficulty, out var current))
            {
                Nav.SelectAt(IndexOf(current.Button));
            }
            else
            {
                Nav.SelectFirst();
            }
        }

        private void FitRows(bool resetScroll)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rows);

            float contentHeight = LayoutUtility.GetPreferredHeight(_rows);
            float maxHeight = Mathf.Max(ROW_HEIGHT,
                _menu.MaxCardHeight - HEADER_HEIGHT - BandSelectMenu.CARD_BOTTOM_INSET);
            bool scrollable = contentHeight > maxHeight;

            _viewportLayout.preferredHeight = Mathf.Min(contentHeight, maxHeight);
            _scrollbar.gameObject.SetActive(scrollable);
            _rowLayout.padding.right = scrollable ? Mathf.CeilToInt(_scrollbarWidth) : 0;

            if (resetScroll)
            {
                _rows.anchoredPosition = Vector2.zero;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            ScrollToSelection();
        }

        private void ScrollToSelection()
        {
            if (Nav.SelectedIndex is not { } index)
            {
                return;
            }

            var item = (RectTransform) Nav.Navigatables[index].transform;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_viewport, item);
            var view = _viewport.rect;

            float y = _rows.anchoredPosition.y;
            if (bounds.max.y > view.yMax)
            {
                y -= bounds.max.y - view.yMax;
            }
            else if (bounds.min.y < view.yMin)
            {
                y += view.yMin - bounds.min.y;
            }

            float maxScroll = Mathf.Max(0f, _rows.rect.height - view.height);
            _rows.anchoredPosition = new Vector2(0f, Mathf.Clamp(y, 0f, maxScroll));
        }

        private void SetState(BandColumnState state)
        {
            State = state;
            Rebuild();
            _menu.OnColumnChanged();
        }

        private void OpenProfileSwitch()
        {
            JoinDevice = Player.DeviceInfo.Controllers[0];
            JoinDeviceOwned = true;
            CreatedProfile = null;
            Ready = false;
            _switchingProfile = true;
            SetState(BandColumnState.ChooseProfile);
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
        }

        private void AddProfileRow(YargProfile profile)
        {
            bool current = profile == Player.Profile;
            bool taken = !current && PlayerContainer.IsProfileTaken(profile);

            float height = taken ? TWO_LINE_ROW_HEIGHT : PROFILE_ROW_HEIGHT;
            var row = AddRow(taken ? Localize.Key("Menu.DifficultySelect.Band.InUse") : null, profile.Name,
                () => _menu.ChooseJoinProfile(this, profile), !taken, height: height);
            AddAvatar(row, profile, height);
        }

        private void AddAvatar(DifficultyItem row, YargProfile profile, float rowHeight)
        {
            float size = rowHeight - AVATAR_MARGIN * 2f;

            var frame = new GameObject("Avatar", typeof(RectTransform));
            var frameRect = (RectTransform) frame.transform;
            frameRect.SetParent(row.transform, false);
            frameRect.anchorMin = new Vector2(0f, 0.5f);
            frameRect.anchorMax = new Vector2(0f, 0.5f);
            frameRect.pivot = new Vector2(0f, 0.5f);
            frameRect.anchoredPosition = new Vector2(AVATAR_MARGIN * 2f, 0f);
            frameRect.sizeDelta = new Vector2(size, size);
            frame.AddComponent<LayoutElement>().ignoreLayout = true;

            var maskImage = frame.AddComponent<Image>();
            maskImage.sprite = _menu.Prefabs.AvatarMask;
            maskImage.raycastTarget = false;
            frame.AddComponent<Mask>().showMaskGraphic = false;

            var picture = new GameObject("Picture", typeof(RectTransform));
            var pictureRect = (RectTransform) picture.transform;
            pictureRect.SetParent(frameRect, false);
            pictureRect.anchorMin = Vector2.zero;
            pictureRect.anchorMax = Vector2.one;
            pictureRect.offsetMin = Vector2.zero;
            pictureRect.offsetMax = Vector2.zero;

            var image = picture.AddComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;

            if (profile.Avatar != null && profile.Avatar.IsValid)
            {
                var texture = profile.Avatar.LoadTexture(false);
                _avatarTextures.Add(texture);
                image.sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));
                pictureRect.localScale = new Vector3(1f, -1f, 1f);
            }
            else
            {
                image.sprite = profile.IsBot ? _menu.Prefabs.ProfileBotSprite : _menu.Prefabs.ProfileGenericSprite;
            }

            if (row.TryGetComponent<VerticalLayoutGroup>(out var layout))
            {
                int inset = Mathf.CeilToInt(size + AVATAR_MARGIN * 3f);
                layout.padding = new RectOffset(inset, inset, 0, 0);
            }
        }

        private void ClearAvatars()
        {
            foreach (var texture in _avatarTextures)
            {
                Object.Destroy(texture);
            }

            _avatarTextures.Clear();
        }

        private void BuildMain()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (_warning is not null)
            {
                var warning = AddLabel(_warning);
                warning.SetTextColor(WARNING_COLOR);
            }

            bool labels = !_menu.ShortLabels;
            float labeledHeight = labels ? TWO_LINE_ROW_HEIGHT : ROW_HEIGHT;

            AddRow(labels ? Localize.Key("Menu.DifficultySelect.Instrument") : null,
                DifficultySelectMenu.GetInstrumentDisplayName(profile.CurrentInstrument, profile.GameMode),
                () => OpenPicker(true), !Ready, height: labeledHeight);

            var difficulty = AddRow(labels ? Localize.Key("Menu.DifficultySelect.Difficulty") : null,
                GetDifficultyLabel(profile.CurrentDifficulty), () => SetState(BandColumnState.Difficulty),
                !Ready && _difficulties.Count > 0, height: labeledHeight);
            if (!Ready && _difficulties.Count > 0)
            {
                _difficultyButton = difficulty.Button;
            }

            if (profile.CurrentInstrument == Instrument.Harmony && _menu.MaxHarmonyIndex > 1)
            {
                AddRow(null,
                    Localize.KeyFormat("Menu.DifficultySelect.Band.Part", profile.HarmonyIndex + 1),
                    CycleHarmonyPart, !Ready);
            }

            var modifiers = GetActiveModifierNames();
            AddRow(labels ? Localize.Key("Menu.DifficultySelect.Modifiers") : null,
                modifiers.Count == 0 ? Modifier.None.ToLocalizedName() : string.Join("\n", modifiers),
                () => SetState(BandColumnState.Modifiers), !Ready,
                height: labeledHeight + Mathf.Max(0, modifiers.Count - 1) * MODIFIER_LINE_HEIGHT);

            var ready = AddRow(null,
                Localize.Key("Menu.DifficultySelect.Band", Ready ? "Unready" : "Ready"),
                ToggleReady, true, _menu.Prefabs.ReadyPrefab);
            _readyButton = ready.Button;
        }

        private void BuildDifficultyPicker()
        {
            SetPlayerHeading();

            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                var capture = difficulty;
                _difficultyItems[difficulty] = AddRow(null, GetDifficultyLabel(difficulty),
                    () => ConfirmDifficulty(capture), _difficulties.Contains(difficulty));
            }

            UpdateDifficultyHighlight();
        }

        private void BuildInstrumentPicker()
        {
            var profile = Player.Profile;

            SetHeading(Localize.Key("Menu.DifficultySelect.Band.ChooseInstrument"));

            var blocked = new List<(Instrument Instrument, string Blocker, GameMode Mode)>();
            foreach (var instrument in _menu.Instruments)
            {
                string blocker = _menu.GetInstrumentBlocker(this, instrument, out var mode);
                if (blocker is not null)
                {
                    blocked.Add((instrument, blocker, mode));
                    continue;
                }

                var capture = instrument;
                var row = AddRow(null, DifficultySelectMenu.GetInstrumentDisplayName(instrument, mode),
                    () => PickInstrument(capture, mode), true);

                if (instrument == profile.CurrentInstrument && _pickerCanCancel)
                {
                    Nav.SelectAt(IndexOf(row.Button));
                }
            }

            foreach (var (instrument, blocker, mode) in blocked)
            {
                AddRow(blocker, DifficultySelectMenu.GetInstrumentDisplayName(instrument, mode), () => { }, false,
                    height: TWO_LINE_ROW_HEIGHT);
            }

            if (!_menu.Instruments.Contains(profile.PreferredInstrument))
            {
                AddRow(Localize.Key("Menu.DifficultySelect.Band.NotInSong"),
                    DifficultySelectMenu.GetInstrumentDisplayName(profile.PreferredInstrument, profile.GameMode),
                    () => { }, false, height: TWO_LINE_ROW_HEIGHT);
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.SitOut"), SitOut, true, _menu.Prefabs.SmallPrefab);
            AddRow(null, Localize.Key("Menu.DifficultySelect.Disconnect"), () => _menu.Disconnect(this), true,
                _menu.Prefabs.SmallPrefab);
        }

        private void BuildModifiers()
        {
            var profile = Player.Profile;

            SetHeading(Localize.Key("Menu.DifficultySelect.Modifiers"));

            foreach (var modifier in _modifiers)
            {
                var capture = modifier;
                var toggle = AddToggle(modifier.ToLocalizedName(), profile.IsModifierActive(modifier), on =>
                {
                    if (on)
                    {
                        profile.AddSingleModifier(capture);
                    }
                    else
                    {
                        profile.RemoveModifiers(capture);
                    }

                    foreach (var (item, itemModifier) in _modifierToggles)
                    {
                        item.Active = profile.IsModifierActive(itemModifier);
                    }
                });

                _modifierToggles.Add((toggle, modifier));
            }

            if (DifficultySelectMenu.SupportsLeftyFlip(profile.GameMode))
            {
                AddToggle(Localize.Key("Menu.DifficultySelect.LeftyFlip"), profile.LeftyFlip,
                    on => profile.LeftyFlip = on);
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), () => SetState(BandColumnState.Main), true);
        }

        private void BuildSittingOut()
        {
            SetPlayerHeading();
            AddRow(null, Localize.Key("Menu.DifficultySelect.Band.SittingOut"), () => OpenPicker(true), !_locked);
        }

        private void PickInstrument(Instrument instrument, GameMode mode)
        {
            ApplyInstrument(instrument, mode, true);
            Ready = false;
            _warning = null;
            SetState(BandColumnState.Main);
        }

        private void SitOut()
        {
            Ready = false;
            _warning = null;
            SetState(BandColumnState.SittingOut);
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
            Ready = !Ready;
            Rebuild();
            if (_readyButton != null)
            {
                Nav.SelectAt(IndexOf(_readyButton));
            }

            _menu.OnColumnChanged();
        }

        private void ConfirmDifficulty(Difficulty difficulty)
        {
            SetDifficulty(difficulty);
            _selectReady = true;
            SetState(BandColumnState.Main);
        }

        private void SetDifficulty(Difficulty difficulty)
        {
            Player.Profile.CurrentDifficulty = Player.Profile.DifficultyFallback = difficulty;
            UpdateDifficultyHighlight();
        }

        private void CycleHarmonyPart()
        {
            var profile = Player.Profile;
            profile.HarmonyIndex = (byte) ((profile.HarmonyIndex + 1) % _menu.MaxHarmonyIndex);
            Rebuild();
        }

        private void OnSelectionChanged(NavigatableBehaviour selected, SelectionOrigin origin)
        {
            if (origin == SelectionOrigin.Mouse)
            {
                _menu.FocusColumn(this);
            }
            else
            {
                ScrollToSelection();
            }
        }

        private void UpdateDifficultyHighlight()
        {
            var current = Player.Profile.CurrentDifficulty;
            foreach (var (difficulty, item) in _difficultyItems)
            {
                if (difficulty == current)
                {
                    item.SetTextColor(BandSelectMenu.READY_COLOR);
                }
                else
                {
                    item.ResetTextColor();
                }
            }
        }

        private void ApplyInstrument(Instrument instrument, GameMode mode, bool deliberate)
        {
            var profile = Player.Profile;

            if (profile.GameMode != mode)
            {
                profile.GameMode = mode;
                Player.DeviceInfo?.RefreshGameMode();
                PlayerContainer.NotifyProfileChanged(profile);
            }

            profile.CurrentInstrument = instrument;

            if (deliberate)
            {
                profile.PreferredInstrument = instrument;
                FiltersMenu.ResetIntensityFiltersForProfile(profile);
            }

            profile.ResolveHarmonyIndex(_menu.MaxHarmonyIndex);
            DifficultySelectMenu.CollectPossibleModifiers(profile, _modifiers);
            UpdatePossibleDifficulties();
        }

        private void UpdatePossibleDifficulties()
        {
            var profile = Player.Profile;

            _difficulties.Clear();
            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                bool playable = true;
                foreach (var song in _menu.Songs)
                {
                    if (!DifficultySelectMenu.HasPlayableDifficulty(song, profile.CurrentInstrument, difficulty))
                    {
                        playable = false;
                        break;
                    }
                }

                if (playable)
                {
                    _difficulties.Add(difficulty);
                }
            }

            var diff = (int) profile.DifficultyFallback;
            while (diff >= (int) Difficulty.Beginner && !_difficulties.Contains((Difficulty) diff))
            {
                --diff;
            }

            if (diff < (int) Difficulty.Beginner)
            {
                diff = (int) profile.DifficultyFallback;
                while (diff < (int) Difficulty.ExpertPlus)
                {
                    ++diff;
                    if (_difficulties.Contains((Difficulty) diff))
                    {
                        break;
                    }
                }
            }

            profile.CurrentDifficulty = (Difficulty) diff;
        }

        private string GetDifficultyLabel(Difficulty difficulty)
        {
            return _menu.ShortLabels
                ? Localize.Key("Menu.DifficultySelect.Band.ShortDifficulty", difficulty.ToString())
                : difficulty.ToLocalizedName();
        }

        private List<string> GetActiveModifierNames()
        {
            var profile = Player.Profile;
            var active = new List<string>();

            foreach (var modifier in _modifiers)
            {
                if (profile.IsModifierActive(modifier))
                {
                    active.Add(modifier.ToLocalizedName());
                }
            }

            if (DifficultySelectMenu.SupportsLeftyFlip(profile.GameMode) && profile.LeftyFlip)
            {
                active.Add(Localize.Key("Menu.DifficultySelect.LeftyFlip"));
            }

            return active;
        }

        private void SetPlayerHeading()
        {
            UpdateIcon();
            SetHeading(_menu.HideNames ? string.Empty : Player.Profile.Name, true);
            if (Ready)
            {
                _heading.color = BandSelectMenu.READY_COLOR;
            }
        }

        private void SetHeading(string text, bool showIcon = false)
        {
            _heading.text = text;
            _heading.color = _headingColor;
            _heading.gameObject.SetActive(!string.IsNullOrEmpty(text));
            _icon.gameObject.SetActive(showIcon && _icon.sprite != null);
            FitHeading();
        }

        private void FitHeading()
        {
            if (_width <= 0f)
            {
                return;
            }

            float maxWidth = _width - HEADER_MARGIN;
            if (_icon.gameObject.activeSelf)
            {
                maxWidth -= _iconWidth + _headingSpacing;
            }

            _heading.enableAutoSizing = false;
            _heading.fontSize = _heading.fontSizeMax;

            float preferred = _heading.preferredWidth;
            _headingLayout.preferredWidth = Mathf.Min(preferred, maxWidth);
            _heading.enableAutoSizing = preferred > maxWidth;
        }

        private void UpdateIcon()
        {
            string key = Player.GetInstrumentSprite();
            if (key == _iconKey)
            {
                return;
            }

            ReleaseIcon();
            _iconKey = key;
            if (key is null)
            {
                return;
            }

            _iconHandle = Addressables.LoadAssetAsync<Sprite>(key);
            _icon.sprite = _iconHandle.WaitForCompletion();
        }

        private void ReleaseIcon()
        {
            _icon.sprite = null;
            _iconKey = null;
            if (_iconHandle.IsValid())
            {
                Addressables.Release(_iconHandle);
            }
        }

        private DifficultyItem AddLabel(string text)
        {
            var label = Object.Instantiate(_menu.Prefabs.ItemPrefab, _rows);
            label.InitializeAsTitle(text);
            PrepareRow(label, ROW_HEIGHT, SMALL_FONT_SIZE);
            return label;
        }

        private DifficultyItem AddRow(string header, string body, UnityAction action, bool interactable,
            DifficultyItem prefab = null, float height = ROW_HEIGHT)
        {
            var row = Object.Instantiate(prefab != null ? prefab : _menu.Prefabs.ItemPrefab, _rows);

            if (header is null)
            {
                row.Initialize(body, action);
            }
            else
            {
                row.Initialize(header, body, action);
            }

            PrepareRow(row, height, ROW_FONT_SIZE);
            if (header is not null)
            {
                row.UseCompactText(LABEL_FONT_SIZE, VALUE_FONT_SIZE);
            }

            row.SetInteractable(interactable);

            if (interactable)
            {
                Nav.AddNavigatable(row.Button);
            }

            return row;
        }

        private ModifierItem AddToggle(string label, bool active, System.Action<bool> onChanged)
        {
            var toggle = Object.Instantiate(_menu.Prefabs.ModifierPrefab, _rows);
            toggle.Initialize(label, active, onChanged);
            toggle.gameObject.AddComponent<LayoutElement>().preferredHeight = ROW_HEIGHT;
            Nav.AddNavigatable(toggle);
            return toggle;
        }

        private static void PrepareRow(DifficultyItem row, float height, float fontSize)
        {
            row.UseCompactText(fontSize);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = height;

            if (row.TryGetComponent<VerticalLayoutGroup>(out var layout))
            {
                layout.childControlWidth = true;
                layout.childAlignment = TextAnchor.MiddleCenter;
                layout.spacing = 0f;
                layout.padding = new RectOffset(4, 4, 0, 0);
            }
        }

        private int IndexOf(NavigatableBehaviour navigatable)
        {
            for (int i = 0; i < Nav.Navigatables.Count; i++)
            {
                if (Nav.Navigatables[i] == navigatable)
                {
                    return i;
                }
            }

            return 0;
        }
    }
}
