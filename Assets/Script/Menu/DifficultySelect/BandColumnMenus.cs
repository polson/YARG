using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandColumnMenus
    {
        private const float MODIFIER_LINE_HEIGHT = 34f;
        private static readonly Color WARNING_COLOR = new Color32(0xFB, 0x44, 0x3F, 0xFF);

        private readonly BandColumn _owner;
        private readonly BandSelectMenu _menu;
        private readonly BandSessionState _session;
        private readonly BandColumnRows _rows;
        private readonly BandColumnHeader _header;
        private readonly BandColumnSettings _settings;

        private NavigatableBehaviour _difficultyButton;
        private NavigatableBehaviour _moveLeftButton;
        private NavigatableBehaviour _moveRightButton;

        public BandColumnMenus(BandColumn owner, BandSelectMenu menu, BandSessionState session, BandColumnRows rows,
            BandColumnHeader header, BandColumnSettings settings)
        {
            _owner = owner;
            _menu = menu;
            _session = session;
            _rows = rows;
            _header = header;
            _settings = settings;
        }

        public NavigatableBehaviour ReadyButton { get; private set; }

        private BandColumnModel Model => _owner.Model;
        private YargPlayer Player => Model.Player;
        private NavigationGroup Nav => _owner.Nav;

        public void Build(BandColumnState state)
        {
            ReadyButton = null;
            _difficultyButton = null;
            _moveLeftButton = null;
            _moveRightButton = null;

            switch (state)
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
                    SetPlayerHeading();
                    _settings.Build();
                    break;
                case BandColumnState.SettingOptions:
                    SetPlayerHeading();
                    _settings.BuildOptions();
                    break;
            }
        }

        public void SelectDefault(int? restoreIndex)
        {
            var nav = Nav;
            if (nav.Count == 0 || nav.SelectedIndex is not null)
            {
                return;
            }

            if (restoreIndex is { } index)
            {
                nav.SelectAt(Mathf.Clamp(index, 0, nav.Count - 1));
            }
            else if (_rows.DefaultButton != null)
            {
                nav.SelectButton(_rows.DefaultButton);
            }
            else if (ReadyButton != null && Model.Ready)
            {
                nav.SelectButton(ReadyButton);
            }
            else if (_difficultyButton != null)
            {
                nav.SelectButton(_difficultyButton);
            }
            else
            {
                nav.SelectFirst();
            }
        }

        private void SetPlayerHeading()
        {
            _header.SetPlayer(Player.Profile, Model.Ready);
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
            _header.SetText(Localize.Key("Menu.DifficultySelect.Band.ChooseProfile"));

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

            _rows.AddRow(null, Localize.Key("Menu.Common.Cancel"), CancelProfilePicker, true, BandRowStyle.Small);
        }

        private void CancelProfilePicker()
        {
            if (Player == null)
            {
                _menu.CloseProfilesInUse();
            }
            else
            {
                _menu.Disconnect(_owner);
            }
        }

        private void AddProfileRow(YargProfile profile)
        {
            bool current = Player != null && profile == Player.Profile;
            bool taken = !current && PlayerContainer.IsProfileTaken(profile);

            var row = _rows.AddRow(null, profile.Name, () => _menu.ChooseJoinProfile(_owner, profile), !taken,
                BandRowStyle.Profile, BandColumnRows.PROFILE_ROW_HEIGHT);
            _rows.AddAvatar(row, profile, BandColumnRows.PROFILE_ROW_HEIGHT);

            if (current)
            {
                _rows.DefaultButton = row.Button;
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
                _settings.AddEntryRow();
                AddPositionRow();
                AddDropOutRow();
                return;
            }

            if (Model.Warning != null)
            {
                var warning = _rows.AddLabel(Model.Warning);
                warning.SetTextColor(WARNING_COLOR);
            }

            ReadyButton = _rows.AddRow(null,
                Localize.Key("Menu.DifficultySelect.Band", Model.Ready ? "Unready" : "Ready"),
                () => _session.ToggleReady(Model), true, BandRowStyle.Ready).Button;

            AddInstrumentRow();

            bool canPickDifficulty = !Model.Ready && Model.Difficulties.Count > 0;
            var difficulty = AddStateRow(Localize.Key("Menu.DifficultySelect.Difficulty"),
                profile.CurrentDifficulty.ToLocalizedName(), BandColumnState.Difficulty,
                canPickDifficulty);
            if (canPickDifficulty)
            {
                _difficultyButton = difficulty.Button;
            }

            if (profile.CurrentInstrument == Instrument.Harmony && _session.MaxHarmonyIndex > 1)
            {
                _rows.AddRow(null,
                    Localize.KeyFormat("Menu.DifficultySelect.Band.Part", profile.HarmonyIndex + 1),
                    () => _session.CycleHarmonyPart(Model), !Model.Ready);
            }

            string adjustments = PlayerSelectionRules.BuildAdjustmentsSummary(profile, Model.Modifiers,
                Model.ExcusableModifiers, out int optionCount);
            AddStateRow(Localize.Key("Menu.DifficultySelect.Adjustments"), adjustments,
                BandColumnState.Adjustments, !Model.Ready, MultiLineHeight(optionCount));

            _settings.AddEntryRow();
            AddPositionRow();
            AddDropOutRow();
        }

        private void AddDropOutRow()
        {
            _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Band.DropOut"), () => _menu.Disconnect(_owner),
                true, BandRowStyle.Small);
        }

        private void AddPositionRow()
        {
            var row = _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Band.Position"),
                () => PushView(BandColumnState.Position, nameof(BandColumnState.Position)), true);
            if (Model.ViewStack.RestoreFocusKey == nameof(BandColumnState.Position))
            {
                _rows.DefaultButton = row.Button;
            }
        }

        private void BuildPosition()
        {
            SetPlayerHeading();

            _moveLeftButton = _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Band.MoveLeft"), () => Move(-1),
                _session.CanMoveColumn(Model, -1)).Button;
            _moveRightButton = _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Band.MoveRight"),
                () => Move(1), _session.CanMoveColumn(Model, 1)).Button;

            _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
        }

        private void Move(int direction)
        {
            if (!_menu.MoveColumn(_owner, direction))
            {
                return;
            }

            _owner.Rebuild();
            var button = _session.CanMoveColumn(Model, direction)
                ? (direction < 0 ? _moveLeftButton : _moveRightButton)
                : (direction < 0 ? _moveRightButton : _moveLeftButton);
            if (Nav.Navigatables.Contains(button))
            {
                Nav.SelectButton(button);
            }
        }

        private void AddInstrumentRow()
        {
            var profile = Player.Profile;
            var song = GlobalVariables.State.CurrentSong;
            var instruments = profile.GameMode.PossibleInstrumentsForSong(song)
                .Where(instrument => _session.Instruments.ContainsInstrument(instrument))
                .ToList();
            bool showRings = _menu.RingTemplate != null && instruments.Count > 0;

            float height = BandColumnRows.TWO_LINE_ROW_HEIGHT;
            if (showRings)
            {
                height += DifficultyItemRings.RingRowHeight(DifficultyItemRings.INSTRUMENT_RING_SIZE);
            }

            var row = _rows.AddRow(Localize.Key("Menu.DifficultySelect.Instrument"),
                PlayerSelectionRules.GetInstrumentDisplayName(profile.CurrentInstrument, profile.GameMode),
                OpenPicker, !Model.Ready, height: height);

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
                if (!Model.Difficulties.Contains(difficulty))
                {
                    continue;
                }

                var row = _rows.AddRow(null, difficulty.ToLocalizedName(), () => ConfirmDifficulty(difficulty), true);
                if (difficulty == current)
                {
                    row.SetTextColor(BandSelectMenu.READY_COLOR);
                    _rows.DefaultButton = row.Button;
                }
            }
        }

        private void ConfirmDifficulty(Difficulty difficulty)
        {
            _session.ConfirmDifficulty(Model, difficulty);
            Nav.SelectButton(ReadyButton);
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
                var row = _rows.AddRow(null, PlayerSelectionRules.GetInstrumentLabelWithTier(song, instrument, mode),
                    () => _session.PickInstrument(Model, instrument), true,
                    height: BandColumnRows.TWO_LINE_ROW_HEIGHT);

                if (instrument == profile.CurrentInstrument && Model.ViewStack.Depth > 0)
                {
                    Nav.SelectButton(row.Button);
                }
            }

            if (!anyPlayable)
            {
                var label = _rows.AddLabel(Localize.Key("Menu.DifficultySelect.Band.NoPlayableInstruments"));
                label.UseWrappedText();
                label.GetComponent<LayoutElement>().preferredHeight = BandColumnRows.TWO_LINE_ROW_HEIGHT;
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

            _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
        }

        private void BuildModifiers()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            foreach (var modifier in Model.Modifiers)
            {
                if ((modifier & PlayerSelectionRules.ACCESSIBILITY_MODIFIERS) == 0)
                {
                    _rows.AddModifierToggle(profile, modifier);
                }
            }

            if (profile.GameMode == GameMode.ProKeys)
            {
                AddStateRow(Localize.Key("Menu.DifficultySelect.DedicatedOpenLane"),
                    profile.OpenLaneDisplayType.ToLocalizedName(), BandColumnState.OpenLane);
            }

            _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
        }

        private void BuildAccessibility()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (PlayerSelectionRules.SupportsLeftyFlip(profile.GameMode))
            {
                _rows.AddToggle(Localize.Key("Menu.DifficultySelect.LeftyFlip"), profile.LeftyFlip,
                    on => profile.LeftyFlip = on);
            }

            if (PlayerSelectionRules.SupportsRangeShifts(profile.GameMode))
            {
                _rows.AddToggle(Localize.Key("Menu.DifficultySelect.NoRangeShifts"),
                    PlayerSelectionRules.HasNoRangeShifts(profile, Model.Modifiers),
                    on => PlayerSelectionRules.SetNoRangeShifts(profile, Model.Modifiers, on));
            }

            foreach (var modifier in Model.Modifiers)
            {
                if ((modifier & PlayerSelectionRules.ACCESSIBILITY_MODIFIERS) != 0 && modifier != Modifier.RangeCompress)
                {
                    _rows.AddModifierToggle(profile, modifier);
                }
            }

            _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
        }

        private void BuildOpenLane()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            foreach (var displayType in ProfileSettingsCatalog.OPEN_LANE_DISPLAY_TYPES)
            {
                var row = _rows.AddRow(null, displayType.ToLocalizedName(), () =>
                {
                    profile.OpenLaneDisplayType = displayType;
                    PopView();
                }, true);
                _rows.AddWrappable(row);

                if (displayType == profile.OpenLaneDisplayType)
                {
                    row.SetTextColor(BandSelectMenu.READY_COLOR);
                    _rows.DefaultButton = row.Button;
                }
            }
        }

        private void BuildSittingOut()
        {
            SetPlayerHeading();
            _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Band.SittingOut"), OpenPicker, !Model.Locked);
        }

        private DifficultyItem AddStateRow(string header, string body, BandColumnState state,
            bool interactable = true, float height = BandColumnRows.TWO_LINE_ROW_HEIGHT)
        {
            string key = state.ToString();
            var row = _rows.AddRow(header, body, () => PushView(state, key), interactable, height: height);
            if (interactable && Model.ViewStack.RestoreFocusKey == key)
            {
                _rows.DefaultButton = row.Button;
            }

            return row;
        }

        private static float MultiLineHeight(int lineCount)
        {
            return BandColumnRows.TWO_LINE_ROW_HEIGHT + Mathf.Max(0, lineCount - 1) * MODIFIER_LINE_HEIGHT;
        }
    }
}
