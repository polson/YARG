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

            return ShouldPlayMissFx() || FreestyleSolosRequested();
        }

        public static bool ShouldPrerender(SongStem stem)
        {
            return FreestyleSolosRequested() &&
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

        /// <summary>
        /// Whether freestyle solo audio is needed this session. A replay follows its recorded engine
        /// parameters, because the engine replays the solos the recording auto-played.
        /// </summary>
        private static bool FreestyleSolosRequested()
        {
            var state = GlobalVariables.State;
            bool replayRequests = (state.IsReplay || state.PlayingWithReplay) && state.ReplayFreestyleSolos;
            return replayRequests || SettingsManager.Settings.FreestyleSolos.Value;
        }
    }
}
