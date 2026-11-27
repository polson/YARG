using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YARG.Assets.Script.Helpers;
using YARG.Core;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Filters;
using YARG.Menu.Navigation;
using YARG.Menu.ProfileList;
using YARG.Settings.Customization;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandColumnSettings
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

        private readonly BandColumnModel _model;
        private readonly BandSessionState _session;
        private readonly BandColumnRows _rows;
        private readonly NavigationGroup _nav;
        private readonly Dictionary<NavigatableBehaviour, (DifficultyItem Row, Action<int> Step)> _steppers = new();
        private DifficultyItem _editingRow;

        public BandColumnSettings(BandColumnModel model, BandSessionState session, BandColumnRows rows,
            NavigationGroup nav)
        {
            _model = model;
            _session = session;
            _rows = rows;
            _nav = nav;
        }

        public void Clear()
        {
            _steppers.Clear();
            _editingRow = null;
        }

        public void Confirm()
        {
            if (_nav.SelectedIndex is { } index &&
                _steppers.TryGetValue(_nav.Navigatables[index], out var entry))
            {
                if (!StopEditing())
                {
                    _editingRow = entry.Row;
                    entry.Row.SetBodyColor(BandSelectMenu.READY_COLOR);
                }

                return;
            }

            _nav.ConfirmSelection();
        }

        public void NavigateVertical(int direction, bool isRepeat)
        {
            if (_editingRow != null && _steppers.TryGetValue(_editingRow.Button, out var entry))
            {
                entry.Step(-direction);
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

        public bool AdjustSelection(int direction)
        {
            if (_nav.SelectedIndex is not { } index ||
                !_steppers.TryGetValue(_nav.Navigatables[index], out var entry))
            {
                return false;
            }

            entry.Step(direction);
            return true;
        }

        public bool StopEditing()
        {
            if (_editingRow == null)
            {
                return false;
            }

            _editingRow.SetBodyColor(null);
            _editingRow = null;
            return true;
        }

        public void OnSelectionChanged(NavigatableBehaviour selected)
        {
            if (_editingRow != null && selected != _editingRow.Button)
            {
                StopEditing();
            }
        }

        public void AddEntryRow()
        {
            var row = _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Band.Settings"),
                () => _session.PushView(_model, BandColumnState.Settings, nameof(BandColumnState.Settings)),
                !_model.Ready);
            if (_model.ViewStack.RestoreFocusKey == nameof(BandColumnState.Settings))
            {
                _rows.DefaultButton = row.Button;
            }
        }

        public void Build()
        {
            var profile = _model.Player.Profile;

            if (_session.IsOvershell)
            {
                AddChoiceRow(INPUT_MODE_KEY, profile.GameMode.ToLocalizedName(),
                    ProfileCenterPane.GAME_MODES.Select(mode =>
                        (mode.ToLocalizedName(), mode == profile.GameMode,
                            (Action) (() => _session.ChangeInputMode(_model, mode)))));
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

            _rows.AddRow(null, Localize.Key("Menu.DifficultySelect.Done"), PopView, true);
        }

        public void BuildOptions()
        {
            _rows.AddWrappable(_rows.AddLabel(_model.ViewStack.SettingTitle));

            foreach (var (label, current, pick) in _model.ViewStack.SettingOptions)
            {
                var row = _rows.AddRow(null, label, () =>
                {
                    pick();
                    PopView();
                }, true);
                _rows.AddWrappable(row);

                if (current)
                {
                    row.SetTextColor(BandSelectMenu.READY_COLOR);
                    _rows.DefaultButton = row.Button;
                }
            }
        }

        private void PopView()
        {
            _session.PopView(_model);
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
                    _rows.AddToggle(SettingLabel("LeftyFlip"), profile.LeftyFlip, on => profile.LeftyFlip = on);
                    break;
                case ProfileSettingStrings.RANGE_DISABLE:
                    _rows.AddToggle(SettingLabel("RangeDisable"), profile.RangeEnabled,
                        on => profile.RangeEnabled = on);
                    break;
                case ProfileSettingStrings.USE_CYMBAL_MODELS:
                    _rows.AddToggle(SettingLabel("UseCymbalModels"), profile.UseCymbalModels,
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
                .Where(preset => allowed == null || allowed(preset.Id))
                .ToList();

            string currentName = presets.FirstOrDefault(preset => preset.Id == current)?.Name ?? string.Empty;
            AddChoiceRow(key, currentName, presets.Select(preset =>
                (preset.Name, preset.Id == current, (Action) (() => pick(preset.Id)))));
        }

        private void AddChoiceRow(string key, string value, IEnumerable<(string Label, bool Current, Action Pick)> options)
        {
            string title = SettingLabel(key);
            var choices = options.ToList();
            var row = _rows.AddRow(title, value,
                () => _session.PushView(_model, BandColumnState.SettingOptions, key, title, choices), true,
                height: BandColumnRows.TWO_LINE_ROW_HEIGHT);
            row.AllowTinyText();
            MarkDefault(key, row);
        }

        private void AddNumberRow(string key, Func<string> format, Action<int> step)
        {
            var row = _rows.AddRow(SettingLabel(key), FormatStepper(format()), () => { }, true,
                height: BandColumnRows.TWO_LINE_ROW_HEIGHT);
            row.AllowTinyText();
            MarkDefault(key, row);

            Action<int> adjust = direction =>
            {
                step(direction);
                row.SetBody(FormatStepper(format()));
            };

            _steppers[row.Button] = (row, adjust);
            row.Button.gameObject.AddComponent<SettingStepClick>().Stepped = adjust;
        }

        private void MarkDefault(string key, DifficultyItem row)
        {
            if (key == _model.ViewStack.RestoreFocusKey)
            {
                _rows.DefaultButton = row.Button;
            }
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
