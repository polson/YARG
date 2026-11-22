using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
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
        Instrument,
        Modifiers,
        SittingOut,
    }

    internal sealed class BandColumn
    {
        private const float TITLE_HEIGHT = 56f;
        private const float ROW_HEIGHT = 44f;
        private const float TWO_LINE_ROW_HEIGHT = 60f;
        private const float TITLE_FONT_SIZE = 24f;
        private const float ROW_FONT_SIZE = 20f;
        private const float SMALL_FONT_SIZE = 16f;

        private static readonly Color COLUMN_COLOR = new(0f, 0f, 0f, 0.6f);
        private static readonly Color FOCUSED_COLUMN_COLOR = new(0.02f, 0.16f, 0.26f, 0.8f);
        private static readonly Color WARNING_COLOR = new Color32(0xFB, 0x44, 0x3F, 0xFF);

        private readonly BandSelectMenu _menu;

        private readonly Image _background;
        private readonly List<Modifier> _modifiers = new();
        private readonly List<Difficulty> _difficulties = new();
        private readonly Dictionary<NavigatableBehaviour, Difficulty> _difficultyRows = new();
        private readonly Dictionary<Difficulty, DifficultyItem> _difficultyItems = new();
        private readonly List<(ModifierItem Item, Modifier Modifier)> _modifierToggles = new();

        private BandColumnState _builtState;
        private BandColumnState _pickerReturnState;
        private bool _pickerCanCancel;
        private bool _locked;
        private string _warning;
        private NavigatableBehaviour _readyButton;

        public YargPlayer Player;
        public InputDevice JoinDevice;
        public bool JoinDeviceOwned;
        public YargProfile CreatedProfile;

        public readonly GameObject Root;
        public readonly NavigationGroup Nav;
        public readonly LayoutElement Layout;

        public BandColumnState State { get; private set; }
        public bool Ready { get; private set; }

        public bool IsJoining => State == BandColumnState.ChooseProfile;
        public bool IsEditingInstrument => State == BandColumnState.Instrument;
        public bool IsSittingOut => State == BandColumnState.SittingOut;
        public bool IsDecided => IsSittingOut || (State == BandColumnState.Main && Ready);

        public BandColumn(BandSelectMenu menu, Transform parent, YargPlayer player)
        {
            _menu = menu;
            Player = player;

            Root = new GameObject("Column", typeof(RectTransform));
            Root.transform.SetParent(parent, false);

            _background = Root.AddComponent<Image>();
            _background.color = COLUMN_COLOR;

            var layout = Root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 6, 6);
            layout.spacing = 2f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Layout = Root.AddComponent<LayoutElement>();

            Nav = Root.AddComponent<NavigationGroup>();
            Nav.SelectionChanged += OnSelectionChanged;
        }

        public void Destroy()
        {
            Nav.SelectionChanged -= OnSelectionChanged;
            Object.Destroy(Root);
        }

        public void SetFocused(bool focused)
        {
            _background.color = focused ? FOCUSED_COLUMN_COLOR : COLUMN_COLOR;
        }

        public void BeginJoin(InputDevice device, bool deviceOwned, YargProfile createdProfile)
        {
            JoinDevice = device;
            JoinDeviceOwned = deviceOwned;
            CreatedProfile = createdProfile;
            Ready = false;
            State = BandColumnState.ChooseProfile;
            Rebuild();
        }

        public void OpenForSong()
        {
            JoinDevice = null;
            CreatedProfile = null;
            Ready = false;
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
                    _menu.CancelJoin(this);
                    break;
                case BandColumnState.Main when Ready:
                    ToggleReady();
                    break;
                case BandColumnState.Main:
                case BandColumnState.SittingOut:
                    OpenPicker(true);
                    break;
                case BandColumnState.Instrument when _pickerCanCancel:
                    SetState(_pickerReturnState);
                    break;
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
            _difficultyRows.Clear();
            _difficultyItems.Clear();
            _modifierToggles.Clear();
            _readyButton = null;
            for (int i = Root.transform.childCount - 1; i >= 0; i--)
            {
                var child = Root.transform.GetChild(i).gameObject;
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

            if (Nav.Count == 0 || Nav.SelectedIndex is not null)
            {
                return;
            }

            if (sameState && previousIndex is { } index)
            {
                Nav.SelectAt(Mathf.Clamp(index, 0, Nav.Count - 1));
            }
            else if (_readyButton != null && Ready)
            {
                Nav.SelectAt(IndexOf(_readyButton));
            }
            else if (State == BandColumnState.Main &&
                _difficultyItems.TryGetValue(Player.Profile.CurrentDifficulty, out var current) &&
                _difficultyRows.ContainsKey(current.Button))
            {
                Nav.SelectAt(IndexOf(current.Button));
            }
            else
            {
                Nav.SelectFirst();
            }
        }

        private void SetState(BandColumnState state)
        {
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
            AddTitle(Localize.Key("Menu.DifficultySelect.Band.ChooseProfile"));

            foreach (var profile in PlayerContainer.Profiles)
            {
                if (profile.IsBot)
                {
                    continue;
                }

                bool current = profile == Player.Profile;
                bool taken = !current && PlayerContainer.IsProfileTaken(profile);
                var capture = profile;

                var row = AddRow(taken ? Localize.Key("Menu.DifficultySelect.Band.InUse") : null, profile.Name,
                    () => _menu.ChooseJoinProfile(this, capture), !taken);

                if (current)
                {
                    Nav.SelectAt(IndexOf(row.Button));
                }
            }

            if (CreatedProfile is null)
            {
                AddRow(null, Localize.Key("Menu.DifficultySelect.Band.NewProfile"),
                    () => _menu.ChooseJoinProfile(this, null), true, _menu.Prefabs.SmallPrefab);
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.Band.Cancel"),
                () => _menu.CancelJoin(this), true, _menu.Prefabs.SmallPrefab);
        }

        private void BuildMain()
        {
            var profile = Player.Profile;

            var title = AddTitle(GetPlayerTitle());
            if (Ready)
            {
                title.SetTextColor(BandSelectMenu.READY_COLOR);
            }

            if (_warning is not null)
            {
                var warning = AddLabel(_warning);
                warning.SetTextColor(WARNING_COLOR);
            }

            AddRow(_menu.ShortLabels ? null : Localize.Key("Menu.DifficultySelect.Instrument"),
                DifficultySelectMenu.GetInstrumentDisplayName(profile.CurrentInstrument, profile.GameMode),
                () => OpenPicker(true), !Ready, height: _menu.ShortLabels ? ROW_HEIGHT : TWO_LINE_ROW_HEIGHT);

            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                var capture = difficulty;
                bool available = _difficulties.Contains(difficulty);
                var row = AddRow(null, GetDifficultyLabel(difficulty), () => ConfirmDifficulty(capture),
                    available && !Ready);

                _difficultyItems[difficulty] = row;
                if (available && !Ready)
                {
                    _difficultyRows[row.Button] = difficulty;
                }
            }

            UpdateDifficultyHighlight();

            if (profile.CurrentInstrument == Instrument.Harmony && _menu.MaxHarmonyIndex > 1)
            {
                AddRow(null,
                    Localize.KeyFormat("Menu.DifficultySelect.Band.Part", profile.HarmonyIndex + 1),
                    CycleHarmonyPart, !Ready);
            }

            AddRow(_menu.ShortLabels ? null : Localize.Key("Menu.DifficultySelect.Modifiers"), GetModifierSummary(),
                () => SetState(BandColumnState.Modifiers), !Ready,
                height: _menu.ShortLabels ? ROW_HEIGHT : TWO_LINE_ROW_HEIGHT);

            var ready = AddRow(null,
                Localize.Key("Menu.DifficultySelect.Band", Ready ? "Unready" : "Ready"),
                ToggleReady, true, _menu.Prefabs.ReadyPrefab);
            _readyButton = ready.Button;
        }

        private void BuildInstrumentPicker()
        {
            var profile = Player.Profile;

            AddTitle(Localize.Key("Menu.DifficultySelect.Band.ChooseInstrument"));

            if (!_menu.Instruments.Contains(profile.PreferredInstrument))
            {
                AddRow(Localize.Key("Menu.DifficultySelect.Band.NotInSong"),
                    DifficultySelectMenu.GetInstrumentDisplayName(profile.PreferredInstrument, profile.GameMode),
                    () => { }, false, height: TWO_LINE_ROW_HEIGHT);
            }

            foreach (var instrument in _menu.Instruments)
            {
                var capture = instrument;
                string blocker = _menu.GetInstrumentBlocker(this, instrument, out var mode);
                string name = DifficultySelectMenu.GetInstrumentDisplayName(instrument, mode);

                var row = AddRow(blocker, name, () => PickInstrument(capture, mode), blocker is null,
                    height: blocker is null ? ROW_HEIGHT : TWO_LINE_ROW_HEIGHT);

                if (blocker is null && instrument == profile.CurrentInstrument && _pickerCanCancel)
                {
                    Nav.SelectAt(IndexOf(row.Button));
                }
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.SitOut"), SitOut, true, _menu.Prefabs.SmallPrefab);
            AddRow(null, Localize.Key("Menu.DifficultySelect.Disconnect"), () => _menu.Disconnect(this), true,
                _menu.Prefabs.SmallPrefab);

            if (_pickerCanCancel)
            {
                AddRow(null, Localize.Key("Menu.DifficultySelect.Band.Cancel"), Back, true,
                    _menu.Prefabs.SmallPrefab);
            }
        }

        private void BuildModifiers()
        {
            var profile = Player.Profile;

            AddTitle(Localize.Key("Menu.DifficultySelect.Modifiers"));

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
            AddTitle(GetPlayerTitle());
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
            if (_readyButton != null)
            {
                Nav.SelectAt(IndexOf(_readyButton));
            }
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

            if (origin == SelectionOrigin.Navigation && selected != null &&
                _difficultyRows.TryGetValue(selected, out var difficulty))
            {
                SetDifficulty(difficulty);
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

        private string GetPlayerTitle()
        {
            var profile = Player.Profile;
            string icon = $"<sprite name=\"{profile.GameMode.ToResourceName()}\">";
            return _menu.HideNames ? icon : $"{icon} {profile.Name}";
        }

        private string GetDifficultyLabel(Difficulty difficulty)
        {
            return _menu.ShortLabels
                ? Localize.Key("Menu.DifficultySelect.Band.ShortDifficulty", difficulty.ToString())
                : difficulty.ToLocalizedName();
        }

        private string GetModifierSummary()
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

            return active.Count == 0 ? Modifier.None.ToLocalizedName() : string.Join(", ", active);
        }

        private DifficultyItem AddTitle(string text)
        {
            var title = Object.Instantiate(_menu.Prefabs.ItemPrefab, Root.transform);
            title.InitializeAsTitle(text);
            PrepareRow(title, TITLE_HEIGHT, TITLE_FONT_SIZE);
            return title;
        }

        private DifficultyItem AddLabel(string text)
        {
            var label = Object.Instantiate(_menu.Prefabs.ItemPrefab, Root.transform);
            label.InitializeAsTitle(text);
            PrepareRow(label, ROW_HEIGHT, SMALL_FONT_SIZE);
            return label;
        }

        private DifficultyItem AddRow(string header, string body, UnityAction action, bool interactable,
            DifficultyItem prefab = null, float height = ROW_HEIGHT)
        {
            var row = Object.Instantiate(prefab != null ? prefab : _menu.Prefabs.ItemPrefab, Root.transform);

            if (header is null)
            {
                row.Initialize(body, action);
            }
            else
            {
                row.Initialize(header, body, action);
            }

            PrepareRow(row, height, ROW_FONT_SIZE);
            row.SetInteractable(interactable);

            if (interactable)
            {
                Nav.AddNavigatable(row.Button);
            }

            return row;
        }

        private ModifierItem AddToggle(string label, bool active, System.Action<bool> onChanged)
        {
            var toggle = Object.Instantiate(_menu.Prefabs.ModifierPrefab, Root.transform);
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
                layout.padding = new RectOffset(4, 4, layout.padding.top, layout.padding.bottom);
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
