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

    internal sealed class BandColumn : MonoBehaviour
    {
        private const float HEADER_MARGIN = 40f;
        private const float PROFILE_ROW_HEIGHT = 64f;
        private const float AVATAR_MARGIN = 6f;
        private const float ROW_HEIGHT = 52f;
        private const float TWO_LINE_ROW_HEIGHT = 72f;
        private const float SMALL_FONT_SIZE = 20f;
        private const float LABEL_FONT_SIZE = 18f;
        private const float VALUE_FONT_SIZE = 28f;
        private const float MODIFIER_LINE_HEIGHT = 34f;

        private const float UNFOCUSED_BORDER_BRIGHTNESS = 0.5f;
        private static readonly Color WARNING_COLOR = new Color32(0xFB, 0x44, 0x3F, 0xFF);

        [SerializeField]
        private RectTransform _card;
        [SerializeField]
        private VerticalLayoutGroup _cardLayout;
        [SerializeField]
        private Image _border;
        [SerializeField]
        private HorizontalLayoutGroup _headerLayout;
        [SerializeField]
        private TextMeshProUGUI _heading;
        [SerializeField]
        private LayoutElement _headingLayout;
        [SerializeField]
        private Image _icon;
        [SerializeField]
        private LayoutElement _iconLayout;
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

        private AsyncOperationHandle<Sprite> _iconHandle;
        private string _iconKey;
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

        public NavigationGroup Nav => _nav;
        public RectTransform Card => _card;

        private RectTransform Rows => _scroll.content;

        public void Initialize(BandSelectMenu menu, YargPlayer player, bool slideIn)
        {
            _menu = menu;
            Player = player;
            _borderColor = _border.color;
            _headingColor = _heading.color;

            _nav.SelectionChanged += OnSelectionChanged;

            if (slideIn)
            {
                RiseFrom(-BandSelectMenu.SLIDE_DISTANCE);
            }
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
            ReleaseIcon();
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

        public void SetFocused(bool focused)
        {
            _border.color = focused
                ? _borderColor
                : (_borderColor * UNFOCUSED_BORDER_BRIGHTNESS).WithAlpha(_borderColor.a);
        }

        public void SetWidth(float width)
        {
            _layout.preferredWidth = width;
            _card.sizeDelta = new Vector2(width, _card.sizeDelta.y);
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
            int? previousIndex = _nav.SelectedIndex;
            bool sameState = _builtState == State;

            _nav.ClearNavigatables();
            _difficultyItems.Clear();
            ClearAvatars();
            _modifierToggles.Clear();
            _readyButton = null;
            _difficultyButton = null;
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

            if (_nav.Count == 0 || _nav.SelectedIndex is not null)
            {
                return;
            }

            var currentDifficulty = Player.Profile.CurrentDifficulty;
            if (sameState && previousIndex is { } index)
            {
                _nav.SelectAt(Mathf.Clamp(index, 0, _nav.Count - 1));
            }
            else if (_readyButton != null && (Ready || selectReady))
            {
                _nav.SelectAt(IndexOf(_readyButton));
            }
            else if (_difficultyButton != null && !Ready)
            {
                _nav.SelectAt(IndexOf(_difficultyButton));
            }
            else if (State == BandColumnState.Difficulty && _difficulties.Contains(currentDifficulty) &&
                _difficultyItems.TryGetValue(currentDifficulty, out var current))
            {
                _nav.SelectAt(IndexOf(current.Button));
            }
            else
            {
                _nav.SelectFirst();
            }
        }

        private void FitRows(bool resetScroll)
        {
            var rows = Rows;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rows);

            float contentHeight = LayoutUtility.GetPreferredHeight(rows);
            float maxHeight = Mathf.Max(ROW_HEIGHT, _menu.MaxCardHeight - _cardLayout.padding.vertical);
            bool scrollable = contentHeight > maxHeight;

            var scrollbar = (RectTransform) _scroll.verticalScrollbar.transform;
            _viewportLayout.preferredHeight = Mathf.Min(contentHeight, maxHeight);
            scrollbar.gameObject.SetActive(scrollable);
            _rowLayout.padding.right = scrollable ? Mathf.CeilToInt(scrollbar.sizeDelta.x) : 0;

            if (resetScroll)
            {
                rows.anchoredPosition = Vector2.zero;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card);
            ScrollToSelection();
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
                () => _menu.ChooseJoinProfile(this, profile), !taken, _profilePrefab, height);
            AddAvatar(row, profile, height);
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

            int inset = Mathf.CeilToInt(rowHeight + AVATAR_MARGIN);
            row.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(inset, inset, 0, 0);
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

            if (_warning is not null)
            {
                var warning = AddLabel(_warning);
                warning.SetTextColor(WARNING_COLOR);
            }


            AddRow(Localize.Key("Menu.DifficultySelect.Instrument"),
                DifficultySelectMenu.GetInstrumentDisplayName(profile.CurrentInstrument, profile.GameMode),
                () => OpenPicker(true), !Ready, height: TWO_LINE_ROW_HEIGHT);

            var difficulty = AddRow(Localize.Key("Menu.DifficultySelect.Difficulty"),
                profile.CurrentDifficulty.ToLocalizedName(), () => SetState(BandColumnState.Difficulty),
                !Ready && _difficulties.Count > 0, height: TWO_LINE_ROW_HEIGHT);
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
            AddRow(Localize.Key("Menu.DifficultySelect.Modifiers"),
                modifiers.Count == 0 ? Modifier.None.ToLocalizedName() : string.Join("\n", modifiers),
                () => SetState(BandColumnState.Modifiers), !Ready,
                height: TWO_LINE_ROW_HEIGHT + Mathf.Max(0, modifiers.Count - 1) * MODIFIER_LINE_HEIGHT);

            var ready = AddRow(null,
                Localize.Key("Menu.DifficultySelect.Band", Ready ? "Unready" : "Ready"),
                ToggleReady, true, _readyPrefab);
            _readyButton = ready.Button;
        }

        private void BuildDifficultyPicker()
        {
            SetPlayerHeading();

            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                var capture = difficulty;
                _difficultyItems[difficulty] = AddRow(null, difficulty.ToLocalizedName(),
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
                    _nav.SelectAt(IndexOf(row.Button));
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

            AddRow(null, Localize.Key("Menu.DifficultySelect.SitOut"), SitOut, true, _smallPrefab);
            AddRow(null, Localize.Key("Menu.DifficultySelect.Disconnect"), () => _menu.Disconnect(this), true,
                _smallPrefab);
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
                _nav.SelectAt(IndexOf(_readyButton));
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
            SetHeading(Player.Profile.Name, true);
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
            float width = _layout.preferredWidth;
            if (width <= 0f)
            {
                return;
            }

            float maxWidth = width - HEADER_MARGIN;
            if (_icon.gameObject.activeSelf)
            {
                maxWidth -= _iconLayout.preferredWidth + _headerLayout.spacing;
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
