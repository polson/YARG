using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YARG.Core;
using YARG.Core.Game;
using YARG.Assets.Script.Helpers;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Filters;
using YARG.Menu.Navigation;
using YARG.Menu.ProfileList;
using YARG.Settings.Customization;

namespace YARG.Menu.DifficultySelect
{
    internal sealed partial class BandColumn
    {
        private const string SETTING_LABEL_PREFIX = "Menu.ProfileList.Tooltip.Profiles.Settings";
        private const string INPUT_MODE_KEY = "InputMode";

        private static readonly HashSet<string> _settingsOutsideCard = new()
        {
            ProfileSettingStrings.INSTRUMENT_SELECT,
            ProfileSettingStrings.CONTROLLERS,
            ProfileSettingStrings.MICROPHONES,
            ProfileSettingStrings.CONFIGURE_FOUR_LANE_DRUMS_HIGHWAY,
            ProfileSettingStrings.CONFIGURE_PRO_DRUMS_HIGHWAY,
            ProfileSettingStrings.CONFIGURE_FIVE_LANE_DRUMS_HIGHWAY,
        };

        private readonly Dictionary<NavigatableBehaviour, Action<int>> _steppers = new();
        private DifficultyItem _editingRow;

        public void Confirm()
        {
            if (_nav.SelectedIndex is { } index && _steppers.ContainsKey(_nav.Navigatables[index]))
            {
                var row = _nav.Navigatables[index].GetComponentInParent<DifficultyItem>();
                if (!StopEditing())
                {
                    _editingRow = row;
                    row.SetBodyColor(BandSelectMenu.READY_COLOR);
                }

                return;
            }

            _nav.ConfirmSelection();
        }

        public void NavigateVertical(int direction, bool isRepeat)
        {
            if (_editingRow != null && _steppers.TryGetValue(_editingRow.Button, out var step))
            {
                step(-direction);
                return;
            }

            if (direction < 0)
            {
                _nav.SelectPrevious(isRepeat);
            }
            else
            {
                _nav.SelectNext(isRepeat);
            }
        }

        private bool StopEditing()
        {
            if (_editingRow == null)
            {
                return false;
            }

            _editingRow.SetBodyColor(null);
            _editingRow = null;
            return true;
        }

        public bool AdjustSelection(int direction)
        {
            if (_nav.SelectedIndex is not { } index || !_steppers.TryGetValue(_nav.Navigatables[index], out var step))
            {
                return false;
            }

            step(direction);
            return true;
        }

        private void AddSettingsRow()
        {
            var row = AddRow(null, Localize.Key("Menu.DifficultySelect.Band.Settings"),
                () => PushView(BandColumnState.Settings, nameof(BandColumnState.Settings)), !Ready);
            if (_restoreFocusKey == nameof(BandColumnState.Settings))
            {
                _defaultButton = row.Button;
            }
        }

        private void BuildSettings()
        {
            var profile = Player.Profile;

            SetPlayerHeading();

            if (_menu.IsOvershell)
            {
                AddChoiceRow(INPUT_MODE_KEY, profile.GameMode.ToLocalizedName(),
                    ProfileCenterPane.GAME_MODES.Select(mode =>
                        (mode.ToLocalizedName(), mode == profile.GameMode, (Action) (() => ChangeInputMode(mode)))));
            }

            foreach (var setting in profile.GameMode.PossibleProfileSettings(new()).Select(s => s.setting).Distinct())
            {
                if (_settingsOutsideCard.Contains(setting) ||
                    (profile.IsBot && ProfileSettingsCatalog.BOT_IRRELEVANT_SETTINGS.Contains(setting)))
                {
                    continue;
                }

                AddSetting(profile, setting);
            }

            AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
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
                    AddToggle(SettingLabel("LeftyFlip"), profile.LeftyFlip, on => profile.LeftyFlip = on);
                    break;
                case ProfileSettingStrings.RANGE_DISABLE:
                    AddToggle(SettingLabel("RangeDisable"), profile.RangeEnabled, on => profile.RangeEnabled = on);
                    break;
                case ProfileSettingStrings.USE_CYMBAL_MODELS:
                    AddToggle(SettingLabel("UseCymbalModels"), profile.UseCymbalModels,
                        on => profile.UseCymbalModels = on);
                    break;
                case ProfileSettingStrings.OPEN_LANE_DISPLAY_TYPE:
                    AddChoiceRow("DedicatedOpenNoteLane", profile.OpenLaneDisplayType.ToLocalizedName(),
                        ProfileSettingsCatalog.OPEN_LANE_DISPLAY_TYPES.Select(type => (type.ToLocalizedName(),
                            type == profile.OpenLaneDisplayType,
                            (Action) (() => profile.OpenLaneDisplayType = type))));
                    break;
                case ProfileSettingStrings.DRUM_STAR_POWER_ACTIVATION_TYPE:
                    AddChoiceRow("StarPowerActivationType", profile.StarPowerActivationType.ToLocalizedName(),
                        ProfileSettingsCatalog.STAR_POWER_ACTIVATION_TYPES.Select(type => (type.ToLocalizedName(),
                            type == profile.StarPowerActivationType,
                            (Action) (() => profile.StarPowerActivationType = type))));
                    break;
            }
        }

        private void AddPresetRow(string key, CustomContent container, Guid current, Action<Guid> pick,
            Func<Guid, bool> allowed = null)
        {
            var presets = container.DefaultBasePresets
                .Concat(container.CustomBasePresets)
                .Where(preset => allowed is null || allowed(preset.Id))
                .ToList();

            string currentName = presets.FirstOrDefault(preset => preset.Id == current)?.Name ?? string.Empty;
            AddChoiceRow(key, currentName, presets.Select(preset =>
                (preset.Name, preset.Id == current, (Action) (() => pick(preset.Id)))));
        }

        private void AddChoiceRow(string key, string value, IEnumerable<(string Label, bool Current, Action Pick)> options)
        {
            string title = SettingLabel(key);
            var choices = options.ToList();
            var row = AddRow(title, value, () => OpenSettingOptions(key, title, choices), true,
                height: TWO_LINE_ROW_HEIGHT);
            row.AllowTinyText();
            MarkSettingDefault(key, row);
        }

        private void AddNumberRow(string key, Func<string> format, Action<int> step)
        {
            var row = AddRow(SettingLabel(key), FormatStepper(format()), () => { }, true,
                height: TWO_LINE_ROW_HEIGHT);
            row.AllowTinyText();
            MarkSettingDefault(key, row);

            Action<int> adjust = direction =>
            {
                step(direction);
                row.SetBody(FormatStepper(format()));
            };

            _steppers[row.Button] = adjust;
            row.Button.gameObject.AddComponent<SettingStepClick>().Stepped = adjust;
        }

        private void MarkSettingDefault(string key, DifficultyItem row)
        {
            if (key == _restoreFocusKey)
            {
                _defaultButton = row.Button;
            }
        }

        private void OpenSettingOptions(string key, string title, List<(string Label, bool Current, Action Pick)> options)
        {
            PushView(BandColumnState.SettingOptions, key, title, options);
        }

        private void BuildSettingOptions()
        {
            SetPlayerHeading();
            _wrappableRows.Add(AddLabel(_currentView.SettingTitle));

            foreach (var (label, current, pick) in _currentView.SettingOptions)
            {
                var row = AddRow(null, label, () =>
                {
                    pick();
                    PopView();
                }, true);
                _wrappableRows.Add(row);

                if (current)
                {
                    row.SetTextColor(BandSelectMenu.READY_COLOR);
                    _defaultButton = row.Button;
                }
            }
        }

        private void ChangeInputMode(GameMode mode)
        {
            var profile = Player.Profile;
            if (profile.GameMode == mode)
            {
                return;
            }

            ProfileSettingsCatalog.ApplyGameModeDefaults(profile, mode);
            SetGameMode(mode);
            FiltersMenu.ResetIntensityFiltersForProfile(profile);
        }

        private static string SettingLabel(string key)
        {
            return Localize.Key(SETTING_LABEL_PREFIX, key, "Title");
        }

        private static string FormatNumber(float value)
        {
            return value.ToString(ProfilesMenu.NUMBER_FORMAT, CultureInfo.CurrentCulture);
        }

        private static string FormatStepper(string value)
        {
            return $"<noparse><</noparse>  {value}  <noparse>></noparse>";
        }

        private static float Step(float value, int direction, float step, float min, float max)
        {
            float stepped = (float) Math.Round(value + direction * step, 3);
            return Math.Clamp(stepped, min, max);
        }
    }
}
