using System;
using System.Collections.Generic;
using YARG.Assets.Script.Helpers;
using YARG.Core;
using YARG.Core.Game;
using YARG.Player;
using YARG.Settings.Customization;

namespace YARG.Menu.ProfileList
{
    public static class ProfileSettingsCatalog
    {
        public const string NUMBER_FORMAT = "0.0###";
        public const float NOTE_SPEED_MIN = 0f;
        public const float NOTE_SPEED_MAX = 100f;
        public const float NOTE_SPEED_STEP = 0.5f;
        public const float HIGHWAY_LENGTH_MIN = 0.001f;
        public const float HIGHWAY_LENGTH_STEP_MIN = 0.1f;
        public const float HIGHWAY_LENGTH_MAX = 10f;
        public const float HIGHWAY_LENGTH_STEP = 0.1f;

        public static readonly GameMode[] GAME_MODES =
        {
            GameMode.FiveFretGuitar,
            GameMode.SixFretGuitar,
            GameMode.EliteDrums,
            GameMode.FourLaneDrums,
            GameMode.FiveLaneDrums,
            GameMode.Vocals,
            GameMode.ProKeys,
        };

        public static readonly StarPowerActivationType[] STAR_POWER_ACTIVATION_TYPES =
        {
            StarPowerActivationType.RightmostNote,
            StarPowerActivationType.AllNotes,
        };

        public static readonly OpenLaneDisplayType[] OPEN_LANE_DISPLAY_TYPES =
        {
            OpenLaneDisplayType.Never,
            OpenLaneDisplayType.IfChartContainsOpens,
            OpenLaneDisplayType.Always,
        };

        public static readonly HashSet<string> BOT_IRRELEVANT_SETTINGS = new()
        {
            ProfileSettingStrings.CONTROLLERS,
            ProfileSettingStrings.MICROPHONES,
            ProfileSettingStrings.INPUT_CALIBRATION,
        };

        public static bool IsEngineAllowed(GameMode mode, Guid enginePresetId)
        {
            if (enginePresetId == EnginePreset.SoloTaps.Id)
            {
                return mode is GameMode.FiveFretGuitar;
            }

            if (enginePresetId == EnginePreset.Casual.Id)
            {
                return mode is GameMode.FiveFretGuitar or GameMode.Vocals or GameMode.SixFretGuitar;
            }

            return enginePresetId != EnginePreset.Precision.Id || mode is not GameMode.ProKeys;
        }

        public static void ApplyGameModeDefaults(YargProfile profile, GameMode mode)
        {
            profile.GameMode = mode;
            profile.CurrentInstrument = mode.PossibleInstruments()[0];
            if (!IsEngineAllowed(mode, profile.EnginePreset))
            {
                profile.EnginePreset = EnginePreset.Default.Id;
            }
        }

        public static float ClampNoteSpeed(float value)
        {
            return Math.Clamp(value, NOTE_SPEED_MIN, NOTE_SPEED_MAX);
        }

        public static float ClampHighwayLength(float value)
        {
            return Math.Clamp(value, HIGHWAY_LENGTH_MIN, HIGHWAY_LENGTH_MAX);
        }
    }
}
