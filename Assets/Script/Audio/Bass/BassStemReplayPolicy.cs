#nullable enable
using YARG.Core.Audio;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    internal static class BassStemReplayPolicy
    {
        public static bool ShouldCapture(SongStem stem)
        {
            if (!AudioHelpers.PitchBendAllowedStems.Contains(stem))
            {
                return false;
            }

            var settings = SettingsManager.Settings;
            return settings.UseRealisticMissFx.Value || settings.FreestyleSolos.Value;
        }

        public static bool ShouldPrerender(SongStem stem)
        {
            return SettingsManager.Settings.FreestyleSolos.Value &&
                AudioHelpers.PitchBendAllowedStems.Contains(stem);
        }

        public static bool ShouldPlayMissFx()
        {
            return SettingsManager.Settings.UseRealisticMissFx.Value;
        }

        public static bool ShouldStretch()
        {
            return BassStretchRender.IsAvailable() &&
                SettingsManager.Settings.EffectsMode.Value == EffectsMode.Quality;
        }
    }
}
