using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YARG.Assets.Script.Helpers;
using YARG.Core;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.ProfileList;
using YARG.Settings.Customization;

namespace YARG.Menu.Overshell
{
    /// <summary>
    /// Builds the profile Settings submenu rows for a band card: decides which profile settings the card can edit,
    /// and turns each into a preset, choice, number, or toggle row bound to the profile.
    /// </summary>
    internal sealed class SettingsRows
    {
        private const string SETTING_LABEL_PREFIX = "Menu.ProfileList.Tooltip.Profiles.Settings";
        private const string INPUT_MODE_KEY = "InputMode";

        private static readonly HashSet<string> SETTINGS_OUTSIDE_CARD = new()
        {
            ProfileSettingStrings.INSTRUMENT_SELECT,
            ProfileSettingStrings.CONTROLLERS,
            ProfileSettingStrings.MICROPHONES,
            ProfileSettingStrings.CONFIGURE_FOUR_LANE_DRUMS_HIGHWAY,
            ProfileSettingStrings.CONFIGURE_PRO_DRUMS_HIGHWAY,
            ProfileSettingStrings.CONFIGURE_FIVE_LANE_DRUMS_HIGHWAY,
        };

        private readonly CardContent _content;
        private readonly Action<string, string, List<SettingOption>> _openOptions;
        private readonly Action<GameMode> _changeInputMode;
        private string _restoreFocusKey;

        public SettingsRows(CardContent content, Action<string, string, List<SettingOption>> openOptions,
            Action<GameMode> changeInputMode)
        {
            _content = content;
            _openOptions = openOptions;
            _changeInputMode = changeInputMode;
        }

        /// <summary>
        /// Adds a row for every profile setting the card can edit, plus the input mode picker when asked.
        /// The row whose key matches <paramref name="restoreFocusKey"/> becomes the default selection.
        /// </summary>
        public void Add(YargProfile profile, string restoreFocusKey, bool includeInputMode)
        {
            _restoreFocusKey = restoreFocusKey;

            if (includeInputMode)
            {
                AddChoiceRow(INPUT_MODE_KEY, profile.GameMode.ToLocalizedName(),
                    ProfileSettingsCatalog.GAME_MODES.Select(mode => new SettingOption(
                        mode.ToLocalizedName(),
                        mode == profile.GameMode,
                        () => _changeInputMode(mode))));
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
                    _content.Add(CardMenuItem.Toggle(SettingLabel("LeftyFlip"), () => profile.LeftyFlip,
                        on => profile.LeftyFlip = on));
                    break;
                case ProfileSettingStrings.RANGE_DISABLE:
                    _content.Add(CardMenuItem.Toggle(SettingLabel("RangeDisable"), () => profile.RangeEnabled,
                        on => profile.RangeEnabled = on));
                    break;
                case ProfileSettingStrings.USE_CYMBAL_MODELS:
                    _content.Add(CardMenuItem.Toggle(SettingLabel("UseCymbalModels"), () => profile.UseCymbalModels,
                        on => profile.UseCymbalModels = on));
                    break;
                case ProfileSettingStrings.OPEN_LANE_DISPLAY_TYPE:
                    AddChoiceRow("DedicatedOpenNoteLane", profile.OpenLaneDisplayType.ToLocalizedName(),
                        ProfileSettingsCatalog.OPEN_LANE_DISPLAY_TYPES.Select(type => new SettingOption(
                            type.ToLocalizedName(),
                            type == profile.OpenLaneDisplayType,
                            () => profile.OpenLaneDisplayType = type)));
                    break;
                case ProfileSettingStrings.DRUM_STAR_POWER_ACTIVATION_TYPE:
                    AddChoiceRow("StarPowerActivationType", profile.StarPowerActivationType.ToLocalizedName(),
                        ProfileSettingsCatalog.STAR_POWER_ACTIVATION_TYPES.Select(type => new SettingOption(
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
                new SettingOption(preset.Name, preset.Id == current, () => pick(preset.Id))));
        }

        private void AddChoiceRow(string key, string value, IEnumerable<SettingOption> options)
        {
            string title = SettingLabel(key);
            var choices = options.ToList();
            var row = _content.Add(CardMenuItem.Button(title, value,
                () => _openOptions(key, title, choices)));
            row.Height = OvershellColumn.TWO_LINE_ROW_HEIGHT;
            row.AllowTinyText = true;
            MarkDefault(key, row);
        }

        private void AddNumberRow(string key, Func<string> format, Action<int> step)
        {
            var row = _content.Add(CardMenuItem.Stepper(SettingLabel(key), format, step));
            row.Height = OvershellColumn.TWO_LINE_ROW_HEIGHT;
            row.AllowTinyText = true;
            MarkDefault(key, row);
        }

        private void MarkDefault(string key, CardMenuItem row)
        {
            if (key == _restoreFocusKey)
            {
                _content.DefaultItem = row;
            }
        }

        private static string SettingLabel(string key)
        {
            return Localize.Key(SETTING_LABEL_PREFIX, key, "Title");
        }

        private static string FormatNumber(float value)
        {
            return value.ToString(ProfileSettingsCatalog.NUMBER_FORMAT, CultureInfo.CurrentCulture);
        }

        private static float Step(float value, int direction, float step, float min, float max)
        {
            float stepped = (float) Math.Round(value + direction * step, 3);
            return Math.Clamp(stepped, min, max);
        }
    }
}
