using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed partial class BandColumn
    {
        private void BuildProfilePicker()
        {
            SetHeading(Localize.Key("Menu.DifficultySelect.Band.ChooseProfile"));

            var profiles = PlayerContainer.Profiles;
            for (int i = 0; i < profiles.Count; i++)
            {
                if (!profiles[i].IsBot)
                {
                    AddProfileRow(profiles[i]);
                }
            }

            for (int i = 0; i < profiles.Count; i++)
            {
                if (profiles[i].IsBot)
                {
                    AddProfileRow(profiles[i]);
                }
            }

            AddRow(null, Localize.Key("Menu.Common.Cancel"), CancelProfilePicker, true, _smallPrefab);
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

        public void ShowProfilesInUse()
        {
            _viewStack.SetRoot(BandColumnState.ChooseProfile);
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

            var rowLayout = row.GetComponent<VerticalLayoutGroup>();
            _profileRows.Add((row, rowLayout, rowHeight));
            FitProfileRow(row, rowLayout, rowHeight);
        }

        private void FitProfileRow(DifficultyItem row, VerticalLayoutGroup rowLayout, float rowHeight)
        {
            float fullSize = rowHeight - AVATAR_EDGE * 2f;
            float size = Mathf.Min(fullSize, _layout.preferredWidth * AVATAR_WIDTH_SHARE);
            float scale = size / fullSize;
            float edge = AVATAR_EDGE * 2f * scale;
            float gap = AVATAR_MARGIN * scale;
            row.FitAvatar(rowHeight, size, edge);

            int inset = Mathf.CeilToInt(edge + size + gap);
            if (rowLayout != null)
            {
                rowLayout.padding = new RectOffset(inset, Mathf.CeilToInt(edge), 0, 0);
            }
        }

        private void ClearAvatars()
        {
            for (int i = 0; i < _avatarTextures.Count; i++)
            {
                Destroy(_avatarTextures[i]);
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
                AddSettingsRow();
                AddPositionRow();
                AddRow(null, Localize.Key("Menu.DifficultySelect.Band.DropOut"), () => _menu.Disconnect(this), true,
                    _smallPrefab);
                return;
            }

            if (_warning != null)
            {
                var warning = AddLabel(_warning);
                warning.SetTextColor(WARNING_COLOR);
            }

            _readyButton = AddRow(null,
                Localize.Key("Menu.DifficultySelect.Band", Ready ? "Unready" : "Ready"),
                ToggleReady, true, _readyPrefab).Button;

            AddInstrumentRow();

            bool canPickDifficulty = !Ready && _difficulties.Count > 0;
            var difficulty = AddStateRow(Localize.Key("Menu.DifficultySelect.Difficulty"),
                profile.CurrentDifficulty.ToLocalizedName(), BandColumnState.Difficulty,
                canPickDifficulty);
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

            AddSettingsRow();
            AddPositionRow();
            AddRow(null, Localize.Key("Menu.DifficultySelect.Band.DropOut"), () => _menu.Disconnect(this), true,
                _smallPrefab);
        }

        private void AddPositionRow()
        {
            var row = AddRow(null, Localize.Key("Menu.DifficultySelect.Band.Position"),
                () => PushView(BandColumnState.Position, nameof(BandColumnState.Position)), true);
            if (_viewStack.RestoreFocusKey == nameof(BandColumnState.Position))
            {
                _defaultButton = row.Button;
            }
        }

        private void BuildPosition()
        {
            SetPlayerHeading();

            _moveLeftButton = AddRow(null, Localize.Key("Menu.DifficultySelect.Band.MoveLeft"), () => Move(-1),
                _menu.CanMoveColumn(this, -1)).Button;
            _moveRightButton = AddRow(null, Localize.Key("Menu.DifficultySelect.Band.MoveRight"), () => Move(1),
                _menu.CanMoveColumn(this, 1)).Button;

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
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
                .Where(instrument => ContainsInstrument(_menu.Instruments, instrument))
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
            var instruments = _menu.Instruments;
            for (int i = 0; i < instruments.Count; i++)
            {
                var instrument = instruments[i];
                if (_menu.GetInstrumentBlocker(this, instrument, out var mode) != null)
                {
                    continue;
                }

                anyPlayable = true;
                var row = AddRow(null, PlayerSelectionRules.GetInstrumentLabelWithTier(song, instrument, mode),
                    () => PickInstrument(instrument), true, height: TWO_LINE_ROW_HEIGHT);

                if (instrument == profile.CurrentInstrument && _viewStack.Depth > 0)
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

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
        }

        private void BuildModifiers()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            for (int i = 0; i < _modifiers.Count; i++)
            {
                var modifier = _modifiers[i];
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

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
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

            for (int i = 0; i < _modifiers.Count; i++)
            {
                var modifier = _modifiers[i];
                if ((modifier & PlayerSelectionRules.ACCESSIBILITY_MODIFIERS) != 0 && modifier != Modifier.RangeCompress)
                {
                    AddModifierToggle(modifier);
                }
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
        }

        private void BuildOpenLane()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            foreach (var displayType in ProfileSettingsCatalog.OPEN_LANE_DISPLAY_TYPES)
            {
                var row = AddRow(null, displayType.ToLocalizedName(), () =>
                {
                    profile.OpenLaneDisplayType = displayType;
                    PopView();
                }, true);
                AddWrappableRow(row);

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

        private DifficultyItem AddLabel(string text)
        {
            var label = Instantiate(_itemPrefab, Rows);
            label.InitializeAsTitle(text);
            label.UseCompactText(SMALL_FONT_SIZE);
            return label;
        }

        private void AddWrappableRow(DifficultyItem row)
        {
            _wrappableRows.Add((row, row.GetComponent<LayoutElement>()));
        }

        private DifficultyItem AddRow(string header, string body, UnityAction action, bool interactable,
            DifficultyItem prefab = null, float height = ROW_HEIGHT)
        {
            var row = Instantiate(prefab != null ? prefab : _itemPrefab, Rows);

            if (header == null)
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
            string key = state.ToString();
            var row = AddRow(header, body, () => PushView(state, key), interactable, height: height);
            if (interactable && _viewStack.RestoreFocusKey == key)
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

                for (int i = 0; i < _modifierToggles.Count; i++)
                {
                    var (item, itemModifier) = _modifierToggles[i];
                    item.Active = profile.IsModifierActive(itemModifier);
                }
            });

            _modifierToggles.Add((toggle, modifier));
        }

        private ModifierItem AddToggle(string label, bool active, Action<bool> onChanged)
        {
            var toggle = Instantiate(_modifierPrefab, Rows);
            toggle.Initialize(label, active, onChanged);

            _nav.AddNavigatable(toggle);
            return toggle;
        }
    }
}
