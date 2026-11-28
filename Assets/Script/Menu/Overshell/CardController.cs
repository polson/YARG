using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.DifficultySelect;
using YARG.Menu.MusicLibrary;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.Overshell
{
    /// <summary>
    /// Runs one band card: decides what each submenu shows, hands that to the card view, and reacts when the
    /// player uses it. Rules that involve other players go through <see cref="OvershellSession"/>.
    /// </summary>
    internal sealed class CardController
    {
        private const float MODIFIER_LINE_HEIGHT = 34f;
        private static readonly Color WARNING_COLOR = new Color32(0xFB, 0x44, 0x3F, 0xFF);

        private readonly OvershellSession _session;
        private readonly Func<float> _getMaxCardHeight;
        private readonly DifficultyRing _ringTemplate;
        private readonly CardContent _content = new();
        private readonly SettingsRows _settingsRows;

        private ColumnState _builtState;
        private CardMenuItem _editing;
        private CardMenuItem _readyItem;
        private CardMenuItem _difficultyItem;
        private CardMenuItem _moveLeftItem;
        private CardMenuItem _moveRightItem;

        public event Action<CardController> DisconnectRequested;
        public event Action<CardController> CloseProfilesInUseRequested;
        public event Action<CardController, YargProfile> ProfileChosen;
        public event Func<CardController, int, bool> MoveRequested;

        public CardController(OvershellSession session, OvershellColumnModel model, OvershellColumn view,
            Func<float> getMaxCardHeight, DifficultyRing ringTemplate = null)
        {
            _session = session;
            _getMaxCardHeight = getMaxCardHeight;
            _ringTemplate = ringTemplate;
            Model = model;
            View = view;

            view.Clicked += OnClicked;
            view.SelectionChanged += OnSelectionChanged;
            model.RebuildRequested += Rebuild;

            _settingsRows = new SettingsRows(_content,
                (key, title, choices) => _session.PushView(Model, ColumnState.SettingOptions, key, title, choices),
                mode => _session.ChangeInputMode(Model, mode));
        }

        public OvershellColumnModel Model { get; }
        public OvershellColumn View { get; }

        public YargPlayer Player => Model.Player;
        public ColumnState State => Model.State;
        public bool Ready => Model.IsReady;
        public bool IsJoining => Model.IsJoining;
        public bool IsExpanded => Model.IsExpanded;
        public bool IsAtTopLevel => Model.IsAtTopLevel;

        /// <summary>
        /// Builds the current screen again and shows it, keeping the selection if the screen did not change.
        /// </summary>
        public void Rebuild()
        {
            int? previousIndex = View.SelectedIndex;
            bool sameState = _builtState == State;

            _editing = null;
            BuildContent();

            _builtState = State;
            Model.ViewStack.RestoreFocusKey = null;

            int? indexToRestore = sameState ? previousIndex : null;
            bool resetScroll = !sameState;
            View.Show(_content, indexToRestore, resetScroll);
            FitRows();
        }

        /// <summary>
        /// Rebuilds the card, then slides it up from where its top edge was before.
        /// </summary>
        private void RebuildFromCurrentTop()
        {
            float top = View.TopEdge;
            Rebuild();
            View.RiseFrom(top - View.Card.rect.height);
        }

        /// <summary>
        /// Sizes the card to its rows, up to the height of the band area.
        /// </summary>
        public void FitRows()
        {
            View.Fit(Model.IsCollapsed, _getMaxCardHeight());
        }

        /// <summary>
        /// Shows the card after the player's state changed between ready and not ready.
        /// </summary>
        private void ApplyReadyVisuals()
        {
            if (Ready)
            {
                View.SlideDownAndCollapse(Rebuild);
                return;
            }

            RebuildFromCurrentTop();
            if (_readyItem != null)
            {
                View.Select(_readyItem);
            }
        }

        /// <summary>
        /// Finishes a join once the player has a profile.
        /// </summary>
        public void CompleteJoin()
        {
            if (_session.IsEverywhere)
            {
                RebuildFromCurrentTop();
            }
            else
            {
                Rebuild();
            }
        }

        /// <summary>
        /// Turns this card into the profile picker shown when every free profile is in use.
        /// </summary>
        public void ShowProfilesInUse()
        {
            Model.ViewStack.SetRoot(ColumnState.ChooseProfile);
            Rebuild();
        }

        /// <summary>
        /// Opens a collapsed overshell card. Returns false if the card was already open.
        /// </summary>
        public bool Expand()
        {
            if (!_session.ExpandColumn(Model))
            {
                return false;
            }

            RebuildFromCurrentTop();
            return true;
        }

        /// <summary>
        /// Uses the selected row, or starts and stops editing it if it is a number.
        /// </summary>
        public void Confirm()
        {
            var selected = View.SelectedItem;
            if (selected is { Kind: CardMenuItemKind.Stepper })
            {
                if (!StopEditing())
                {
                    _editing = selected;
                    View.SetEditing(selected, true);
                }

                return;
            }

            View.ConfirmSelection();
        }

        /// <summary>
        /// Moves the selection up or down, or changes the number being edited.
        /// </summary>
        public void NavigateVertical(int direction, bool isRepeat)
        {
            if (_editing != null)
            {
                View.Step(_editing, -direction);
                return;
            }

            if (direction < 0)
            {
                View.SelectPrevious(isRepeat);
            }
            else
            {
                View.SelectNext(isRepeat);
            }
        }

        /// <summary>
        /// Changes the selected number with left and right. Returns false if the selected row is not a number.
        /// </summary>
        public bool AdjustSelection(int direction)
        {
            var selected = View.SelectedItem;
            if (selected is not { Kind: CardMenuItemKind.Stepper })
            {
                return false;
            }

            View.Step(selected, direction);
            return true;
        }

        /// <summary>
        /// Goes back one step: stops editing, closes a submenu, unreadies, collapses, or leaves the band.
        /// </summary>
        public void Back()
        {
            if (StopEditing())
            {
                return;
            }

            bool isFirstScreen = State == ColumnState.ChooseProfile ||
                (State == ColumnState.Instrument && Model.ViewStack.Depth == 0);
            if (isFirstScreen)
            {
                DisconnectRequested?.Invoke(this);
                return;
            }

            if (State == ColumnState.Main)
            {
                if (Ready)
                {
                    ToggleReady();
                }
                else if (_session.CollapseColumn(Model))
                {
                    View.SlideDownAndCollapse(Rebuild);
                }

                return;
            }

            if (State == ColumnState.SittingOut)
            {
                _session.OpenPicker(Model, canCancel: true);
                return;
            }

            _session.PopView(Model);
        }

        private void ToggleReady()
        {
            if (_session.ToggleReady(Model))
            {
                ApplyReadyVisuals();
            }
        }

        private bool StopEditing()
        {
            if (_editing == null)
            {
                return false;
            }

            View.SetEditing(_editing, false);
            _editing = null;
            return true;
        }

        private void OnClicked(bool onHeader)
        {
            if (!_session.CanClickCards || State != ColumnState.Main)
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

            if (_session.IsEverywhere && onHeader)
            {
                Back();
            }
        }

        private void OnSelectionChanged(CardMenuItem selected, bool fromMouse)
        {
            if (_editing != null && selected != _editing)
            {
                StopEditing();
            }

            if (fromMouse)
            {
                _session.FocusColumn(Model);
            }
        }

        private void BuildContent()
        {
            _content.Clear();
            _readyItem = null;
            _difficultyItem = null;
            _moveLeftItem = null;
            _moveRightItem = null;

            switch (State)
            {
                case ColumnState.ChooseProfile:
                    BuildProfilePicker();
                    break;
                case ColumnState.Main:
                    BuildMain();
                    break;
                case ColumnState.Difficulty:
                    BuildDifficultyPicker();
                    break;
                case ColumnState.Instrument:
                    BuildInstrumentPicker();
                    break;
                case ColumnState.Adjustments:
                    BuildAdjustments();
                    break;
                case ColumnState.Modifiers:
                    BuildModifiers();
                    break;
                case ColumnState.Accessibility:
                    BuildAccessibility();
                    break;
                case ColumnState.OpenLane:
                    BuildOpenLane();
                    break;
                case ColumnState.SittingOut:
                    BuildSittingOut();
                    break;
                case ColumnState.Position:
                    BuildPosition();
                    break;
                case ColumnState.Settings:
                    BuildSettings();
                    break;
                case ColumnState.SettingOptions:
                    BuildSettingOptions();
                    break;
            }

            if (_content.DefaultItem == null && Ready)
            {
                _content.DefaultItem = _readyItem;
            }

            _content.DefaultItem ??= _difficultyItem;
        }

        private void SetPlayerHeading()
        {
            _content.HeaderProfile = Player.Profile;
            _content.HeaderReady = Model.IsReady;
        }

        private void PushView(ColumnState state, string focusKey)
        {
            _session.PushView(Model, state, focusKey);
        }

        private void PopView()
        {
            _session.PopView(Model);
        }

        private void OpenPicker()
        {
            _session.OpenPicker(Model, canCancel: true);
        }

        private void BuildProfilePicker()
        {
            _content.HeaderText = Localize.Key("Menu.DifficultySelect.Band.ChooseProfile");

            var profiles = PlayerContainer.Profiles;
            foreach (var profile in profiles)
            {
                if (!profile.IsBot)
                {
                    AddProfileRow(profile);
                }
            }

            foreach (var profile in profiles)
            {
                if (profile.IsBot)
                {
                    AddProfileRow(profile);
                }
            }

            var cancel = _content.Add(CardMenuItem.Button(Localize.Key("Menu.Common.Cancel"), CancelProfilePicker));
            cancel.Style = CardRowStyle.Small;
        }

        private void CancelProfilePicker()
        {
            if (Player == null)
            {
                CloseProfilesInUseRequested?.Invoke(this);
            }
            else
            {
                DisconnectRequested?.Invoke(this);
            }
        }

        private void AddProfileRow(YargProfile profile)
        {
            bool isCurrent = Player != null && profile == Player.Profile;
            bool isTaken = !isCurrent && PlayerContainer.IsProfileTaken(profile);

            var row = _content.Add(CardMenuItem.Button(profile.Name, () => ProfileChosen?.Invoke(this, profile),
                !isTaken));
            row.Style = CardRowStyle.Profile;
            row.Height = OvershellColumn.PROFILE_ROW_HEIGHT;
            row.Avatar = profile;

            if (isCurrent)
            {
                _content.DefaultItem = row;
            }
        }

        private void BuildMain()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (Model.IsCollapsed)
            {
                return;
            }

            if (_session.IsEverywhere)
            {
                AddSettingsEntryRow();
                AddPositionRow();
                AddDropOutRow();
                return;
            }

            if (Model.WarningMessage != null)
            {
                _content.Add(CardMenuItem.Label(Model.WarningMessage)).TextColor = WARNING_COLOR;
            }

            _readyItem = _content.Add(CardMenuItem.Button(
                Localize.Key("Menu.DifficultySelect.Band", Model.IsReady ? "Unready" : "Ready"),
                ToggleReady));
            _readyItem.Style = CardRowStyle.Ready;

            AddInstrumentRow();

            bool canPickDifficulty = !Model.IsReady && Model.Difficulties.Count > 0;
            var difficulty = AddStateRow(Localize.Key("Menu.DifficultySelect.Difficulty"),
                profile.CurrentDifficulty.ToLocalizedName(), ColumnState.Difficulty, canPickDifficulty);
            if (canPickDifficulty)
            {
                _difficultyItem = difficulty;
            }

            if (profile.CurrentInstrument == Instrument.Harmony && _session.MaxHarmonyIndex > 1)
            {
                _content.Add(CardMenuItem.Button(
                    Localize.KeyFormat("Menu.DifficultySelect.Band.Part", profile.HarmonyIndex + 1),
                    () => _session.CycleHarmonyPart(Model), !Model.IsReady));
            }

            string adjustments = PlayerSelectionRules.BuildAdjustmentsSummary(profile, Model.Modifiers,
                Model.ExcusableModifiers, out int optionCount);
            AddStateRow(Localize.Key("Menu.DifficultySelect.Adjustments"), adjustments,
                ColumnState.Adjustments, !Model.IsReady, MultiLineHeight(optionCount));

            AddSettingsEntryRow();
            AddPositionRow();
            AddDropOutRow();
        }

        private void AddDropOutRow()
        {
            var dropOut = _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.DropOut"),
                () => DisconnectRequested?.Invoke(this)));
            dropOut.Style = CardRowStyle.Small;
        }

        private void AddPositionRow()
        {
            var row = _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.Position"),
                () => PushView(ColumnState.Position, nameof(ColumnState.Position))));
            if (Model.ViewStack.RestoreFocusKey == nameof(ColumnState.Position))
            {
                _content.DefaultItem = row;
            }
        }

        private void AddSettingsEntryRow()
        {
            var row = _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.Settings"),
                () => PushView(ColumnState.Settings, nameof(ColumnState.Settings)), !Model.IsReady));
            if (Model.ViewStack.RestoreFocusKey == nameof(ColumnState.Settings))
            {
                _content.DefaultItem = row;
            }
        }

        private void BuildPosition()
        {
            SetPlayerHeading();

            _moveLeftItem = _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.MoveLeft"),
                () => Move(-1), _session.CanMoveColumn(Model, -1)));
            _moveRightItem = _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.MoveRight"),
                () => Move(1), _session.CanMoveColumn(Model, 1)));

            _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void Move(int direction)
        {
            if (MoveRequested?.Invoke(this, direction) != true)
            {
                return;
            }

            Rebuild();

            var sameWay = direction < 0 ? _moveLeftItem : _moveRightItem;
            var otherWay = direction < 0 ? _moveRightItem : _moveLeftItem;
            View.TrySelect(_session.CanMoveColumn(Model, direction) ? sameWay : otherWay);
        }

        private void AddInstrumentRow()
        {
            var profile = Player.Profile;
            var song = _session.CurrentSong;
            var instruments = profile.GameMode.PossibleInstrumentsForSong(song)
                .Where(instrument => _session.Instruments.ContainsInstrument(instrument))
                .ToList();
            bool showRings = _ringTemplate != null && instruments.Count > 0;

            float height = OvershellColumn.TWO_LINE_ROW_HEIGHT;
            if (showRings)
            {
                height += DifficultyItemRings.RingRowHeight(DifficultyItemRings.INSTRUMENT_RING_SIZE);
            }

            var row = _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Instrument"),
                PlayerSelectionRules.GetInstrumentDisplayName(profile.CurrentInstrument, profile.GameMode),
                OpenPicker, !Model.IsReady));
            row.Height = height;

            if (showRings)
            {
                row.RingTemplate = _ringTemplate;
                row.RingInstruments = instruments;
                row.RingCurrent = profile.CurrentInstrument;
                row.RingSong = song;
            }
        }

        private void BuildDifficultyPicker()
        {
            SetPlayerHeading();

            var current = Player.Profile.CurrentDifficulty;
            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                if (!Model.Difficulties.Contains(difficulty))
                {
                    continue;
                }

                var row = _content.Add(CardMenuItem.Button(difficulty.ToLocalizedName(),
                    () => ConfirmDifficulty(difficulty)));
                if (difficulty == current)
                {
                    row.TextColor = OvershellColumn.READY_COLOR;
                    _content.DefaultItem = row;
                }
            }
        }

        private void ConfirmDifficulty(Difficulty difficulty)
        {
            _session.ConfirmDifficulty(Model, difficulty);
            View.Select(_readyItem);
        }

        private void BuildInstrumentPicker()
        {
            var profile = Player.Profile;

            SetPlayerHeading();
            var song = _session.CurrentSong;

            bool anyPlayable = false;
            foreach (var instrument in _session.Instruments)
            {
                if (_session.GetInstrumentBlocker(Model, instrument, out var mode) != null)
                {
                    continue;
                }

                anyPlayable = true;
                var row = _content.Add(CardMenuItem.Button(
                    PlayerSelectionRules.GetInstrumentLabelWithTier(song, instrument, mode),
                    () => _session.PickInstrument(Model, instrument)));
                row.Height = OvershellColumn.TWO_LINE_ROW_HEIGHT;

                if (instrument == profile.CurrentInstrument && Model.ViewStack.Depth > 0)
                {
                    _content.FocusItem = row;
                }
            }

            if (!anyPlayable)
            {
                var label = _content.Add(CardMenuItem.Label(
                    Localize.Key("Menu.DifficultySelect.Band.NoPlayableInstruments")));
                label.WrapText = true;
                label.Height = OvershellColumn.TWO_LINE_ROW_HEIGHT;
            }
        }

        private void BuildAdjustments()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            AddStateRow(Localize.Key("Menu.DifficultySelect.Modifiers"),
                PlayerSelectionRules.BuildModifierSummary(profile, Model.Modifiers, Model.ExcusableModifiers),
                ColumnState.Modifiers);

            if (PlayerSelectionRules.HasAccessibilityOptions(profile, Model.Modifiers))
            {
                AddStateRow(Localize.Key("Menu.DifficultySelect.Accessibility"),
                    PlayerSelectionRules.BuildAccessibilitySummary(profile, Model.Modifiers),
                    ColumnState.Accessibility);
            }

            _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void BuildModifiers()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            foreach (var modifier in Model.Modifiers)
            {
                if ((modifier & PlayerSelectionRules.ACCESSIBILITY_MODIFIERS) == 0)
                {
                    AddModifierToggle(profile, modifier);
                }
            }

            if (profile.GameMode == GameMode.ProKeys)
            {
                AddStateRow(Localize.Key("Menu.DifficultySelect.DedicatedOpenLane"),
                    profile.OpenLaneDisplayType.ToLocalizedName(), ColumnState.OpenLane);
            }

            _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void BuildAccessibility()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (PlayerSelectionRules.SupportsLeftyFlip(profile.GameMode))
            {
                _content.Add(CardMenuItem.Toggle(Localize.Key("Menu.DifficultySelect.LeftyFlip"),
                    () => profile.LeftyFlip, on => profile.LeftyFlip = on));
            }

            if (PlayerSelectionRules.SupportsRangeShifts(profile.GameMode))
            {
                _content.Add(CardMenuItem.Toggle(Localize.Key("Menu.DifficultySelect.NoRangeShifts"),
                    () => PlayerSelectionRules.HasNoRangeShifts(profile, Model.Modifiers),
                    on => PlayerSelectionRules.SetNoRangeShifts(profile, Model.Modifiers, on)));
            }

            foreach (var modifier in Model.Modifiers)
            {
                bool isAccessibility = (modifier & PlayerSelectionRules.ACCESSIBILITY_MODIFIERS) != 0;
                if (isAccessibility && modifier != Modifier.RangeCompress)
                {
                    AddModifierToggle(profile, modifier);
                }
            }

            _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void AddModifierToggle(YargProfile profile, Modifier modifier)
        {
            _content.Add(CardMenuItem.Toggle(modifier.ToLocalizedName(), () => profile.IsModifierActive(modifier),
                on =>
                {
                    if (on)
                    {
                        profile.AddSingleModifier(modifier);
                    }
                    else
                    {
                        profile.RemoveModifiers(modifier);
                    }
                }));
        }

        private void BuildOpenLane()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            foreach (var displayType in ProfileSettingsCatalog.OPEN_LANE_DISPLAY_TYPES)
            {
                var row = _content.Add(CardMenuItem.Button(displayType.ToLocalizedName(), () =>
                {
                    profile.OpenLaneDisplayType = displayType;
                    PopView();
                }));
                row.WrapIfLong = true;

                if (displayType == profile.OpenLaneDisplayType)
                {
                    row.TextColor = OvershellColumn.READY_COLOR;
                    _content.DefaultItem = row;
                }
            }
        }

        private void BuildSittingOut()
        {
            SetPlayerHeading();
            _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.SittingOut"), OpenPicker,
                !Model.IsLocked));
        }

        private void BuildSettings()
        {
            SetPlayerHeading();
            _settingsRows.Add(Player.Profile, Model.ViewStack.RestoreFocusKey,
                includeInputMode: _session.IsEverywhere);

            _content.Add(CardMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void BuildSettingOptions()
        {
            SetPlayerHeading();

            _content.Add(CardMenuItem.Label(Model.ViewStack.SettingTitle)).WrapIfLong = true;

            foreach (var option in Model.ViewStack.SettingOptions)
            {
                var row = _content.Add(CardMenuItem.Button(option.Label, () =>
                {
                    option.Pick();
                    PopView();
                }));
                row.WrapIfLong = true;

                if (option.Current)
                {
                    row.TextColor = OvershellColumn.READY_COLOR;
                    _content.DefaultItem = row;
                }
            }
        }

        private CardMenuItem AddStateRow(string header, string body, ColumnState state, bool enabled = true,
            float height = OvershellColumn.TWO_LINE_ROW_HEIGHT)
        {
            string key = state.ToString();
            var row = _content.Add(CardMenuItem.Button(header, body, () => PushView(state, key), enabled));
            row.Height = height;
            if (enabled && Model.ViewStack.RestoreFocusKey == key)
            {
                _content.DefaultItem = row;
            }

            return row;
        }

        private static float MultiLineHeight(int lineCount)
        {
            return OvershellColumn.TWO_LINE_ROW_HEIGHT + Mathf.Max(0, lineCount - 1) * MODIFIER_LINE_HEIGHT;
        }

    }
}
