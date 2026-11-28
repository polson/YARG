using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using YARG.Assets.Script.Helpers;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.ProfileList;
using YARG.Player;
using YARG.Settings.Customization;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// Runs one band card: decides what each submenu shows, hands that to the card view, and reacts when the
    /// player uses it. Rules that involve other players go through <see cref="BandSessionState"/>.
    /// </summary>
    internal sealed class BandCardController
    {
        private const float MODIFIER_LINE_HEIGHT = 34f;
        private const string SETTING_LABEL_PREFIX = "Menu.ProfileList.Tooltip.Profiles.Settings";
        private const string INPUT_MODE_KEY = "InputMode";
        private static readonly Color WARNING_COLOR = new Color32(0xFB, 0x44, 0x3F, 0xFF);

        private static readonly HashSet<string> SETTINGS_OUTSIDE_CARD = new()
        {
            ProfileSettingStrings.INSTRUMENT_SELECT,
            ProfileSettingStrings.CONTROLLERS,
            ProfileSettingStrings.MICROPHONES,
            ProfileSettingStrings.CONFIGURE_FOUR_LANE_DRUMS_HIGHWAY,
            ProfileSettingStrings.CONFIGURE_PRO_DRUMS_HIGHWAY,
            ProfileSettingStrings.CONFIGURE_FIVE_LANE_DRUMS_HIGHWAY,
        };

        private readonly BandSelectMenu _menu;
        private readonly BandSessionState _session;
        private readonly BandCardContent _content = new();

        private BandColumnState _builtState;
        private BandMenuItem _editing;
        private BandMenuItem _readyItem;
        private BandMenuItem _difficultyItem;
        private BandMenuItem _moveLeftItem;
        private BandMenuItem _moveRightItem;

        public BandCardController(BandSelectMenu menu, BandSessionState session, BandColumnModel model,
            BandColumn view)
        {
            _menu = menu;
            _session = session;
            Model = model;
            View = view;

            view.Clicked += OnClicked;
            view.SelectionChanged += OnSelectionChanged;
        }

        public BandColumnModel Model { get; }
        public BandColumn View { get; }

        public YargPlayer Player => Model.Player;
        public BandColumnState State => Model.State;
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

            View.Show(_content, sameState ? previousIndex : null, resetScroll: !sameState);
            FitRows();
        }

        /// <summary>
        /// Rebuilds the card, then slides it up from where its top edge was before.
        /// </summary>
        public void RebuildFromCurrentTop()
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
            View.Fit(Model.IsCollapsed, _menu.MaxCardHeight);
        }

        /// <summary>
        /// Shows the card after the player's state changed between ready and not ready.
        /// </summary>
        public void ApplyReadyVisuals()
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
            if (_session.IsOvershell)
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
            Model.ViewStack.SetRoot(BandColumnState.ChooseProfile);
            Rebuild();
        }

        /// <summary>
        /// Opens a collapsed overshell card. Returns false if the card was already open.
        /// </summary>
        public bool Expand()
        {
            return _session.ExpandColumn(Model);
        }

        /// <summary>
        /// Uses the selected row, or starts and stops editing it if it is a number.
        /// </summary>
        public void Confirm()
        {
            var selected = View.SelectedItem;
            if (selected is { Kind: BandMenuItemKind.Stepper })
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
            if (selected is not { Kind: BandMenuItemKind.Stepper })
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

            bool isFirstScreen = State == BandColumnState.ChooseProfile ||
                (State == BandColumnState.Instrument && Model.ViewStack.Depth == 0);
            if (isFirstScreen)
            {
                _menu.Disconnect(this);
                return;
            }

            if (State == BandColumnState.Main)
            {
                if (Ready)
                {
                    _session.ToggleReady(Model);
                }
                else if (_session.CollapseColumn(Model))
                {
                    View.SlideDownAndCollapse(Rebuild);
                }

                return;
            }

            if (State == BandColumnState.SittingOut)
            {
                _session.OpenPicker(Model, canCancel: true);
                return;
            }

            _session.PopView(Model);
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
            if (!_session.CanClickCards || State != BandColumnState.Main)
            {
                return;
            }

            if (Ready)
            {
                _session.ToggleReady(Model);
                return;
            }

            if (Expand())
            {
                return;
            }

            if (_session.IsOvershell && onHeader)
            {
                Back();
            }
        }

        private void OnSelectionChanged(BandMenuItem selected, bool fromMouse)
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

        private void PushView(BandColumnState state, string focusKey)
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

            var cancel = _content.Add(BandMenuItem.Button(Localize.Key("Menu.Common.Cancel"), CancelProfilePicker));
            cancel.Style = BandRowStyle.Small;
        }

        private void CancelProfilePicker()
        {
            if (Player == null)
            {
                _menu.CloseProfilesInUse();
            }
            else
            {
                _menu.Disconnect(this);
            }
        }

        private void AddProfileRow(YargProfile profile)
        {
            bool isCurrent = Player != null && profile == Player.Profile;
            bool isTaken = !isCurrent && PlayerContainer.IsProfileTaken(profile);

            var row = _content.Add(BandMenuItem.Button(profile.Name, () => _menu.ChooseJoinProfile(this, profile),
                !isTaken));
            row.Style = BandRowStyle.Profile;
            row.Height = BandColumn.PROFILE_ROW_HEIGHT;
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

            if (_session.IsOvershell)
            {
                AddSettingsEntryRow();
                AddPositionRow();
                AddDropOutRow();
                return;
            }

            if (Model.WarningMessage != null)
            {
                _content.Add(BandMenuItem.Label(Model.WarningMessage)).TextColor = WARNING_COLOR;
            }

            _readyItem = _content.Add(BandMenuItem.Button(
                Localize.Key("Menu.DifficultySelect.Band", Model.IsReady ? "Unready" : "Ready"),
                () => _session.ToggleReady(Model)));
            _readyItem.Style = BandRowStyle.Ready;

            AddInstrumentRow();

            bool canPickDifficulty = !Model.IsReady && Model.Difficulties.Count > 0;
            var difficulty = AddStateRow(Localize.Key("Menu.DifficultySelect.Difficulty"),
                profile.CurrentDifficulty.ToLocalizedName(), BandColumnState.Difficulty, canPickDifficulty);
            if (canPickDifficulty)
            {
                _difficultyItem = difficulty;
            }

            if (profile.CurrentInstrument == Instrument.Harmony && _session.MaxHarmonyIndex > 1)
            {
                _content.Add(BandMenuItem.Button(
                    Localize.KeyFormat("Menu.DifficultySelect.Band.Part", profile.HarmonyIndex + 1),
                    () => _session.CycleHarmonyPart(Model), !Model.IsReady));
            }

            string adjustments = PlayerSelectionRules.BuildAdjustmentsSummary(profile, Model.Modifiers,
                Model.ExcusableModifiers, out int optionCount);
            AddStateRow(Localize.Key("Menu.DifficultySelect.Adjustments"), adjustments,
                BandColumnState.Adjustments, !Model.IsReady, MultiLineHeight(optionCount));

            AddSettingsEntryRow();
            AddPositionRow();
            AddDropOutRow();
        }

        private void AddDropOutRow()
        {
            var dropOut = _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.DropOut"),
                () => _menu.Disconnect(this)));
            dropOut.Style = BandRowStyle.Small;
        }

        private void AddPositionRow()
        {
            var row = _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.Position"),
                () => PushView(BandColumnState.Position, nameof(BandColumnState.Position))));
            if (Model.ViewStack.RestoreFocusKey == nameof(BandColumnState.Position))
            {
                _content.DefaultItem = row;
            }
        }

        private void AddSettingsEntryRow()
        {
            var row = _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.Settings"),
                () => PushView(BandColumnState.Settings, nameof(BandColumnState.Settings)), !Model.IsReady));
            if (Model.ViewStack.RestoreFocusKey == nameof(BandColumnState.Settings))
            {
                _content.DefaultItem = row;
            }
        }

        private void BuildPosition()
        {
            SetPlayerHeading();

            _moveLeftItem = _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.MoveLeft"),
                () => Move(-1), _session.CanMoveColumn(Model, -1)));
            _moveRightItem = _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.MoveRight"),
                () => Move(1), _session.CanMoveColumn(Model, 1)));

            _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void Move(int direction)
        {
            if (!_menu.MoveColumn(this, direction))
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
            var song = GlobalVariables.State.CurrentSong;
            var instruments = profile.GameMode.PossibleInstrumentsForSong(song)
                .Where(instrument => _session.Instruments.ContainsInstrument(instrument))
                .ToList();
            bool showRings = _menu.RingTemplate != null && instruments.Count > 0;

            float height = BandColumn.TWO_LINE_ROW_HEIGHT;
            if (showRings)
            {
                height += DifficultyItemRings.RingRowHeight(DifficultyItemRings.INSTRUMENT_RING_SIZE);
            }

            var row = _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Instrument"),
                PlayerSelectionRules.GetInstrumentDisplayName(profile.CurrentInstrument, profile.GameMode),
                OpenPicker, !Model.IsReady));
            row.Height = height;

            if (showRings)
            {
                row.RingTemplate = _menu.RingTemplate;
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

                var row = _content.Add(BandMenuItem.Button(difficulty.ToLocalizedName(),
                    () => ConfirmDifficulty(difficulty)));
                if (difficulty == current)
                {
                    row.TextColor = BandSelectMenu.READY_COLOR;
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
            var song = GlobalVariables.State.CurrentSong;

            bool anyPlayable = false;
            foreach (var instrument in _session.Instruments)
            {
                if (_session.GetInstrumentBlocker(Model, instrument, out var mode) != null)
                {
                    continue;
                }

                anyPlayable = true;
                var row = _content.Add(BandMenuItem.Button(
                    PlayerSelectionRules.GetInstrumentLabelWithTier(song, instrument, mode),
                    () => _session.PickInstrument(Model, instrument)));
                row.Height = BandColumn.TWO_LINE_ROW_HEIGHT;

                if (instrument == profile.CurrentInstrument && Model.ViewStack.Depth > 0)
                {
                    _content.FocusItem = row;
                }
            }

            if (!anyPlayable)
            {
                var label = _content.Add(BandMenuItem.Label(
                    Localize.Key("Menu.DifficultySelect.Band.NoPlayableInstruments")));
                label.WrapText = true;
                label.Height = BandColumn.TWO_LINE_ROW_HEIGHT;
            }
        }

        private void BuildAdjustments()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            AddStateRow(Localize.Key("Menu.DifficultySelect.Modifiers"),
                PlayerSelectionRules.BuildModifierSummary(profile, Model.Modifiers, Model.ExcusableModifiers),
                BandColumnState.Modifiers);

            if (PlayerSelectionRules.HasAccessibilityOptions(profile, Model.Modifiers))
            {
                AddStateRow(Localize.Key("Menu.DifficultySelect.Accessibility"),
                    PlayerSelectionRules.BuildAccessibilitySummary(profile, Model.Modifiers),
                    BandColumnState.Accessibility);
            }

            _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
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
                    profile.OpenLaneDisplayType.ToLocalizedName(), BandColumnState.OpenLane);
            }

            _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void BuildAccessibility()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (PlayerSelectionRules.SupportsLeftyFlip(profile.GameMode))
            {
                _content.Add(BandMenuItem.Toggle(Localize.Key("Menu.DifficultySelect.LeftyFlip"),
                    () => profile.LeftyFlip, on => profile.LeftyFlip = on));
            }

            if (PlayerSelectionRules.SupportsRangeShifts(profile.GameMode))
            {
                _content.Add(BandMenuItem.Toggle(Localize.Key("Menu.DifficultySelect.NoRangeShifts"),
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

            _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void AddModifierToggle(YargProfile profile, Modifier modifier)
        {
            _content.Add(BandMenuItem.Toggle(modifier.ToLocalizedName(), () => profile.IsModifierActive(modifier),
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
                var row = _content.Add(BandMenuItem.Button(displayType.ToLocalizedName(), () =>
                {
                    profile.OpenLaneDisplayType = displayType;
                    PopView();
                }));
                row.WrapIfLong = true;

                if (displayType == profile.OpenLaneDisplayType)
                {
                    row.TextColor = BandSelectMenu.READY_COLOR;
                    _content.DefaultItem = row;
                }
            }
        }

        private void BuildSittingOut()
        {
            SetPlayerHeading();
            _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Band.SittingOut"), OpenPicker,
                !Model.IsLocked));
        }

        private void BuildSettings()
        {
            SetPlayerHeading();
            var profile = Player.Profile;

            if (_session.IsOvershell)
            {
                AddChoiceRow(INPUT_MODE_KEY, profile.GameMode.ToLocalizedName(),
                    ProfileCenterPane.GAME_MODES.Select(mode =>
                        (mode.ToLocalizedName(), mode == profile.GameMode,
                            (Action) (() => _session.ChangeInputMode(Model, mode)))));
            }

            foreach (var setting in profile.GameMode.PossibleProfileSettings(new()).Select(s => s.setting).Distinct())
            {
                bool isIrrelevantForBot = profile.IsBot &&
                    ProfileSettingsCatalog.BOT_IRRELEVANT_SETTINGS.Contains(setting);
                if (SETTINGS_OUTSIDE_CARD.Contains(setting) || isIrrelevantForBot)
                {
                    continue;
                }

                AddSetting(profile, setting);
            }

            _content.Add(BandMenuItem.Button(Localize.Key("Menu.DifficultySelect.Done"), PopView));
        }

        private void BuildSettingOptions()
        {
            SetPlayerHeading();

            _content.Add(BandMenuItem.Label(Model.ViewStack.SettingTitle)).WrapIfLong = true;

            foreach (var option in Model.ViewStack.SettingOptions)
            {
                var row = _content.Add(BandMenuItem.Button(option.Label, () =>
                {
                    option.Pick();
                    PopView();
                }));
                row.WrapIfLong = true;

                if (option.Current)
                {
                    row.TextColor = BandSelectMenu.READY_COLOR;
                    _content.DefaultItem = row;
                }
            }
        }

        private void AddSetting(YargProfile profile, string setting)
        {
            switch (setting)
            {
                case ProfileSettingStrings.ENGINE_PRESET:
                    AddPresetRow("EnginePreset", CustomContentManager.EnginePresets, profile.EnginePreset,
                        id => profile.EnginePreset = id, id => ProfileSettingsCatalog.IsEngineAllowed(profile.GameMode, id));
                    break;
                case ProfileSettingStrings.THEME_SELECT:
                    AddPresetRow("NoteTheme", CustomContentManager.ThemePresets, profile.ThemePreset,
                        id => profile.ThemePreset = id);
                    break;
                case ProfileSettingStrings.COLOR_PROFILE_SELECT:
                    AddPresetRow("ColorProfile", CustomContentManager.ColorProfiles, profile.ColorProfile,
                        id => profile.ColorProfile = id);
                    break;
                case ProfileSettingStrings.CAMERA_PRESET:
                    AddPresetRow("CameraPreset", CustomContentManager.CameraSettings, profile.CameraPreset,
                        id => profile.CameraPreset = id);
                    break;
                case ProfileSettingStrings.HIGHWAY_PRESET:
                    AddPresetRow("HighwayPreset", CustomContentManager.HighwayPresets, profile.HighwayPreset,
                        id => profile.HighwayPreset = id);
                    break;
                case ProfileSettingStrings.ROCK_METER_PRESET:
                    AddPresetRow("RockMeterPreset", CustomContentManager.RockMeterPresets, profile.RockMeterPreset,
                        id => profile.RockMeterPreset = id);
                    break;
                case ProfileSettingStrings.NOTE_SPEED_AND_HIGHWAY_LENGTH:
                    AddNumberRow("NoteSpeed", () => FormatNumber(profile.NoteSpeed), direction =>
                        profile.NoteSpeed = Step(profile.NoteSpeed, direction, ProfileSettingsCatalog.NOTE_SPEED_STEP,
                            ProfileSettingsCatalog.NOTE_SPEED_MIN, ProfileSettingsCatalog.NOTE_SPEED_MAX));
                    AddNumberRow("HighwayLength", () => FormatNumber(profile.HighwayLength), direction =>
                        profile.HighwayLength = Step(profile.HighwayLength, direction,
                            ProfileSettingsCatalog.HIGHWAY_LENGTH_STEP,
                            ProfileSettingsCatalog.HIGHWAY_LENGTH_STEP_MIN,
                            ProfileSettingsCatalog.HIGHWAY_LENGTH_MAX));
                    break;
                case ProfileSettingStrings.INPUT_CALIBRATION:
                    AddNumberRow("InputCalibration", () => $"{profile.InputCalibrationMilliseconds} ms",
                        direction => profile.InputCalibrationMilliseconds += direction);
                    break;
                case ProfileSettingStrings.LEFTY_FLIP:
                    _content.Add(BandMenuItem.Toggle(SettingLabel("LeftyFlip"), () => profile.LeftyFlip,
                        on => profile.LeftyFlip = on));
                    break;
                case ProfileSettingStrings.RANGE_DISABLE:
                    _content.Add(BandMenuItem.Toggle(SettingLabel("RangeDisable"), () => profile.RangeEnabled,
                        on => profile.RangeEnabled = on));
                    break;
                case ProfileSettingStrings.USE_CYMBAL_MODELS:
                    _content.Add(BandMenuItem.Toggle(SettingLabel("UseCymbalModels"), () => profile.UseCymbalModels,
                        on => profile.UseCymbalModels = on));
                    break;
                case ProfileSettingStrings.OPEN_LANE_DISPLAY_TYPE:
                    AddChoiceRow("DedicatedOpenNoteLane", profile.OpenLaneDisplayType.ToLocalizedName(),
                        ProfileSettingsCatalog.OPEN_LANE_DISPLAY_TYPES.Select(type => new BandSettingOption(
                            type.ToLocalizedName(),
                            type == profile.OpenLaneDisplayType,
                            () => profile.OpenLaneDisplayType = type)));
                    break;
                case ProfileSettingStrings.DRUM_STAR_POWER_ACTIVATION_TYPE:
                    AddChoiceRow("StarPowerActivationType", profile.StarPowerActivationType.ToLocalizedName(),
                        ProfileSettingsCatalog.STAR_POWER_ACTIVATION_TYPES.Select(type => new BandSettingOption(
                            type.ToLocalizedName(),
                            type == profile.StarPowerActivationType,
                            () => profile.StarPowerActivationType = type)));
                    break;
            }
        }

        private void AddPresetRow(string key, CustomContent container, Guid current, Action<Guid> pick,
            Func<Guid, bool> allowed = null)
        {
            var presets = container.DefaultBasePresets
                .Concat(container.CustomBasePresets)
                .Where(preset => allowed == null || allowed(preset.Id))
                .ToList();

            string currentName = presets.FirstOrDefault(preset => preset.Id == current)?.Name ?? string.Empty;
            AddChoiceRow(key, currentName, presets.Select(preset =>
                new BandSettingOption(preset.Name, preset.Id == current, () => pick(preset.Id))));
        }

        private void AddChoiceRow(string key, string value, IEnumerable<BandSettingOption> options)
        {
            string title = SettingLabel(key);
            var choices = options.ToList();
            var row = _content.Add(BandMenuItem.Button(title, value,
                () => _session.PushView(Model, BandColumnState.SettingOptions, key, title, choices)));
            row.Height = BandColumn.TWO_LINE_ROW_HEIGHT;
            row.AllowTinyText = true;
            MarkDefault(key, row);
        }

        private void AddNumberRow(string key, Func<string> format, Action<int> step)
        {
            var row = _content.Add(BandMenuItem.Stepper(SettingLabel(key), format, step));
            row.Height = BandColumn.TWO_LINE_ROW_HEIGHT;
            row.AllowTinyText = true;
            MarkDefault(key, row);
        }

        private BandMenuItem AddStateRow(string header, string body, BandColumnState state, bool enabled = true,
            float height = BandColumn.TWO_LINE_ROW_HEIGHT)
        {
            string key = state.ToString();
            var row = _content.Add(BandMenuItem.Button(header, body, () => PushView(state, key), enabled));
            row.Height = height;
            if (enabled && Model.ViewStack.RestoreFocusKey == key)
            {
                _content.DefaultItem = row;
            }

            return row;
        }

        private void MarkDefault(string key, BandMenuItem row)
        {
            if (key == Model.ViewStack.RestoreFocusKey)
            {
                _content.DefaultItem = row;
            }
        }

        private static float MultiLineHeight(int lineCount)
        {
            return BandColumn.TWO_LINE_ROW_HEIGHT + Mathf.Max(0, lineCount - 1) * MODIFIER_LINE_HEIGHT;
        }

        private static string SettingLabel(string key)
        {
            return Localize.Key(SETTING_LABEL_PREFIX, key, "Title");
        }

        private static string FormatNumber(float value)
        {
            return value.ToString(ProfilesMenu.NUMBER_FORMAT, CultureInfo.CurrentCulture);
        }

        private static float Step(float value, int direction, float step, float min, float max)
        {
            float stepped = (float) Math.Round(value + direction * step, 3);
            return Math.Clamp(stepped, min, max);
        }
    }
}
