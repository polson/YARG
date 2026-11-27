using System.Collections.Generic;
using System.Linq;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Filters;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public static class PlayerSelectionRules
    {
        // Modifiers relocated from the Modifiers menu to the Accessibility menu.
        // RangeCompress is folded into the "No Range Shifts" toggle there.
        public const Modifier ACCESSIBILITY_MODIFIERS =
            Modifier.OpensToGreens | Modifier.NoKicks | Modifier.UnpitchedOnly | Modifier.RangeCompress;

        // Instruments that use the (X-Fret) suffix and not the (X-Lane) suffix when in 6-fret mode
        private static readonly Instrument[] FRET_INSTRUMENTS = {
            Instrument.FiveFretGuitar,
            Instrument.FiveFretBass,
            Instrument.FiveFretCoopGuitar,
            Instrument.FiveFretRhythm,
            Instrument.SixFretGuitar,
            Instrument.SixFretBass,
            Instrument.SixFretRhythm,
            Instrument.SixFretCoopGuitar,
        };

        public static void PickInstrument(YargProfile profile, Instrument instrument,
            IReadOnlyCollection<Instrument> availableInstruments, int maxHarmonyIndex)
        {
            var preferredInstrument = profile.PreferredInstrument;
            profile.CurrentInstrument = instrument;

            // Re-resolve after an instrument switch in case the raw harmony index is out
            // of range for this song (ChangePlayer's check can be masked by the
            // HarmonyIndex getter returning 0 when not on Harmony).
            profile.ResolveHarmonyIndex(maxHarmonyIndex);

            // What we are doing here is resetting preferred instrument only if the current preferred instrument
            // was an option for this chart. This ensures that preferred instrument does not change when the
            // player is forced to use a different instrument.
            if (instrument != preferredInstrument && availableInstruments.Contains(preferredInstrument))
            {
                profile.PreferredInstrument = instrument;
            }

            if (UnityEngine.Application.isPlaying)
            {
                FiltersMenu.ResetIntensityFiltersForProfile(profile);
            }
        }

        // Apply the primary player's modifiers to the other vocal players
        // for this session only, so their own saved selections survive
        public static void ShareVocalModifiers(YargPlayer leader, IEnumerable<YargPlayer> players)
        {
            foreach (var player in players)
            {
                if (player.SittingOut || player == leader)
                {
                    continue;
                }

                if (player.Profile.GameMode == GameMode.Vocals)
                {
                    player.Profile.ApplySessionModifiers(leader.Profile);
                }
            }
        }

        public static void CollectPossibleDifficulties(YargProfile profile, IReadOnlyList<SongEntry> songs,
            List<Difficulty> possibleDifficulties)
        {
            possibleDifficulties.Clear();

            // Get the possible difficulties for the player's instrument in the song
            foreach (var difficulty in EnumExtensions<Difficulty>.Values)
            {
                bool invalidDifficulty = false;
                foreach (var song in songs)
                {
                    if (!HasPlayableDifficulty(song, profile.CurrentInstrument, difficulty))
                    {
                        invalidDifficulty = true;
                        break;
                    }
                }

                if (!invalidDifficulty)
                {
                    possibleDifficulties.Add(difficulty);
                }
            }

            // TODO: Handle difficulty fallback better in play a show mode

            var diff = (int) profile.DifficultyFallback;
            while (diff >= (int) Difficulty.Beginner && !possibleDifficulties.Contains((Difficulty) diff))
            {
                --diff;
            }

            if (diff < (int) Difficulty.Beginner)
            {
                diff = (int) profile.DifficultyFallback;
                while (diff < (int) Difficulty.ExpertPlus)
                {
                    ++diff;
                    if (possibleDifficulties.Contains((Difficulty) diff))
                    {
                        break;
                    }
                }
            }

            profile.CurrentDifficulty = (Difficulty) diff;
        }

        public static Modifier CollectPossibleModifiers(YargProfile profile, List<Modifier> possibleModifiers)
        {
            possibleModifiers.Clear();
            var (possible, excusable) = profile.GameMode.PossibleModifiers(profile.CurrentInstrument);

            foreach (var modifier in EnumExtensions<Modifier>.Values)
            {
                // Skip if the modifier is not a possible one
                if ((possible & modifier) == 0)
                {
                    // Also try to clear it if it isn't considered excusable yet the player somehow has it
                    if (((excusable & modifier) == 0) && profile.IsModifierActive(modifier))
                    {
                        profile.RemoveModifiers(modifier);
                    }

                    continue;
                }

                possibleModifiers.Add(modifier);
            }

            return excusable;
        }

        public static bool SupportsLeftyFlip(GameMode mode)
            => mode is GameMode.FiveFretGuitar or GameMode.SixFretGuitar
                or GameMode.FourLaneDrums or GameMode.FiveLaneDrums or GameMode.EliteDrums;

        public static string GetInstrumentDisplayName(Instrument instrument, GameMode gameMode)
        {
            var name = instrument.ToLocalizedName();
            // When in 6-fret mode, indicate when a 5-fret instrument is being used
            // (i.e., a 5-fret chart loaded in 6-fret mode)
            if (gameMode == GameMode.SixFretGuitar && !instrument.IsSixFret())
            {
                //TODO: Figure out a way to localise this
                if (FRET_INSTRUMENTS.Contains(instrument))
                {
                    return $"{name} (5-Fret)";
                }
                return $"{name} (5-Lane)";
            }
            return name;
        }

        public static bool SongHasInstrument(SongEntry entry, Instrument instrument)
        {
            return entry.HasInstrument(instrument) || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => entry.HasInstrument(Instrument.FiveLaneDrums),
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => entry.HasInstrument(Instrument.ProDrums),
                _ => false
            };
        }

        public static bool HasPlayableDifficulty(SongEntry entry, in Instrument instrument, in Difficulty difficulty)
        {
            // For vocals, insert special difficulties
            if (instrument is Instrument.Vocals or Instrument.Harmony or Instrument.PartyVocals)
            {
                return difficulty is not Difficulty.ExpertPlus;
            }

            // For PK, disallow beginner
            if (instrument is Instrument.ProKeys && difficulty is Difficulty.Beginner)
            {
                return false;
            }

            // Otherwise, we can do this
            return entry[instrument][difficulty] || instrument switch
            {
                // Allow 5 -> 4-lane conversions to be played on 4-lane
                Instrument.FourLaneDrums or
                Instrument.ProDrums      => entry[Instrument.FiveLaneDrums][difficulty],
                // Allow 4 -> 5-lane conversions to be played on 5-lane
                Instrument.FiveLaneDrums => entry[Instrument.ProDrums][difficulty],
                _ => false
            };
        }

        public static bool SupportsRangeShifts(GameMode mode)
            => mode is GameMode.FiveFretGuitar or GameMode.ProKeys;

        public static bool HasAccessibilityOptions(YargProfile profile, IReadOnlyList<Modifier> possibleModifiers)
            => SupportsLeftyFlip(profile.GameMode)
                || SupportsRangeShifts(profile.GameMode)
                || possibleModifiers.Any(m => (m & ACCESSIBILITY_MODIFIERS) != 0);

        // Summary body for the main menu's Adjustments row: the active options
        // from both nested menus (Modifiers and Accessibility) combined.
        // optionCount lets the caller pick a font size for the list.
        public static string BuildAdjustmentsSummary(YargProfile profile, IReadOnlyList<Modifier> possibleModifiers,
            Modifier excusableModifiers, out int optionCount)
        {
            string none = Modifier.None.ToLocalizedName();
            string text = "";

            string modifierSummary = BuildModifierSummary(profile, possibleModifiers, excusableModifiers);
            if (modifierSummary != none)
            {
                text += modifierSummary + "\n";
            }

            if (HasAccessibilityOptions(profile, possibleModifiers))
            {
                string accessibilitySummary = BuildAccessibilitySummary(profile, possibleModifiers);
                if (accessibilitySummary != none)
                {
                    text += accessibilitySummary + "\n";
                }
            }

            text = text.Trim();

            if (text.Length == 0)
            {
                optionCount = 0;
                return none;
            }

            optionCount = text.Count(c => c == '\n') + 1;
            return text;
        }

        // Summary body for the Adjustments menu's Modifiers row. Accessibility-relocated
        // modifiers are summarized on their own row; the keys open-lane toggles
        // live in the Modifiers menu but are backed by OpenLaneDisplayType rather
        // than a Modifier flag, so they're listed here explicitly.
        public static string BuildModifierSummary(YargProfile profile, IReadOnlyList<Modifier> possibleModifiers,
            Modifier excusableModifiers)
        {
            string text = "";

            if ((profile.CurrentModifiers & ~excusableModifiers & ~ACCESSIBILITY_MODIFIERS) != Modifier.None)
            {
                foreach (var modifier in possibleModifiers)
                {
                    if ((modifier & ACCESSIBILITY_MODIFIERS) != 0) continue;
                    if (!profile.IsModifierActive(modifier)) continue;

                    text += modifier.ToLocalizedName() + "\n";
                }
            }

            // The bare display-type names wouldn't be meaningful in a summary;
            // Never counts as "off" and isn't listed.
            if (profile.GameMode == GameMode.ProKeys)
            {
                switch (profile.OpenLaneDisplayType)
                {
                    case OpenLaneDisplayType.Always:
                        text += LocalizeHeader("OpenLaneAlways") + "\n";
                        break;
                    case OpenLaneDisplayType.IfChartContainsOpens:
                        text += LocalizeHeader("OpenLaneWhenCharted") + "\n";
                        break;
                }
            }

            text = text.Trim();
            return text.Length == 0 ? Modifier.None.ToLocalizedName() : text;
        }

        public static string BuildAccessibilitySummary(YargProfile profile, IReadOnlyList<Modifier> possibleModifiers)
        {
            string text = "";

            if (SupportsLeftyFlip(profile.GameMode) && profile.LeftyFlip)
            {
                text += LocalizeHeader("LeftyFlip") + "\n";
            }

            if (SupportsRangeShifts(profile.GameMode)
                && (!profile.RangeEnabled || profile.IsModifierActive(Modifier.RangeCompress)))
            {
                text += LocalizeHeader("NoRangeShifts") + "\n";
            }

            foreach (var modifier in possibleModifiers)
            {
                if ((modifier & ACCESSIBILITY_MODIFIERS) == 0) continue;
                if (modifier == Modifier.RangeCompress) continue; // covered above
                if (!profile.IsModifierActive(modifier)) continue;

                text += modifier.ToLocalizedName() + "\n";
            }

            text = text.Trim();
            return text.Length == 0 ? Modifier.None.ToLocalizedName() : text;
        }

        // Get the charter-rated tier values for an instrument. Harmony and party
        // vocals read from HarmonyVocals, which is empty on solo-only songs (no
        // harmony chart) — fall back to the lead vocals tier so the ring still
        // shows meaningful data instead of the dimmed state.
        public static PartValues GetTierValues(SongEntry song, Instrument instrument)
        {
            var tierValues = song[instrument];

            if (instrument is Instrument.Harmony or Instrument.PartyVocals && !tierValues.IsActive())
            {
                tierValues = song[Instrument.Vocals];
            }

            return tierValues;
        }

        // "5 - Nightmare"-style tier text for the instrument menu, using the
        // filters menu's localized tier names. "No Part" can't come up here —
        // the menu only lists playable instruments.
        public static string GetTierLabel(PartValues values)
        {
            if (!values.IsActive() || values.Intensity < 0)
            {
                return "? - " + Localize.Key("Menu.Filters.Intensities.Unknown");
            }

            // The label clamps at the top tier; the number stays exact.
            string text = $"{values.Intensity} - {IntensityLabels.GetLabelByIndex(values.Intensity)}";

            // Top-tier colors (matching the web export's tier palette): orange
            // at 5, the ring segments' red at 6+.
            return values.Intensity switch
            {
                >= 6 => $"<color=#FB443F>{text}</color>",
                5    => $"<color=#FF8400>{text}</color>",
                _    => text,
            };
        }

        // Instrument name with its charted tier on a smaller, dimmed
        // second line (mirroring the header text style).
        public static string GetInstrumentLabelWithTier(SongEntry song, Instrument instrument, GameMode gameMode)
        {
            return GetInstrumentDisplayName(instrument, gameMode)
                + $"\n<size=18><color=#FFFFFF80>{GetTierLabel(GetTierValues(song, instrument))}</color></size>";
        }

        // One positive "No Range Shifts" switch backed by both range
        // mechanisms: the profile's RangeEnabled display setting (the
        // profile editor's Range Disable toggle) and, where the game mode
        // supports it, the RangeCompress chart modifier. Shown active if
        // either says shifts are off, so it tracks the profile editor.
        public static bool HasNoRangeShifts(YargProfile profile, IReadOnlyList<Modifier> possibleModifiers)
        {
            return !profile.RangeEnabled
                || (possibleModifiers.Contains(Modifier.RangeCompress) && profile.IsModifierActive(Modifier.RangeCompress));
        }

        public static void SetNoRangeShifts(YargProfile profile, IReadOnlyList<Modifier> possibleModifiers, bool on)
        {
            profile.RangeEnabled = !on;

            if (!possibleModifiers.Contains(Modifier.RangeCompress))
            {
                return;
            }

            if (on)
            {
                profile.AddSingleModifier(Modifier.RangeCompress);
            }
            else
            {
                profile.RemoveModifiers(Modifier.RangeCompress);
            }
        }

        private static string LocalizeHeader(string key)
        {
            return Localize.Key("Menu.DifficultySelect", key);
        }

        public static bool IsVocalsInstrument(Instrument instrument)
        {
            return instrument is Instrument.Vocals or Instrument.Harmony or Instrument.PartyVocals;
        }

        public static int GetMaxHarmonyIndex(SongEntry currentSong, IReadOnlyList<SongEntry> songs)
        {
            int max = currentSong.VocalsCount;
            if (songs == null)
            {
                return max;
            }

            for (int i = 0; i < songs.Count; i++)
            {
                if (songs[i].VocalsCount < max)
                {
                    max = songs[i].VocalsCount;
                }
            }

            return max;
        }

        public static void CollectSongInstruments(SongEntry currentSong, IReadOnlyList<SongEntry> songs,
            List<Instrument> destination)
        {
            var instruments = new HashSet<Instrument>();
            foreach (var mode in EnumExtensions<GameMode>.Values)
            {
                if (mode == GameMode.Menu)
                {
                    continue;
                }

                foreach (var instrument in mode.PossibleInstrumentsForSong(currentSong))
                {
                    instruments.Add(instrument);
                }
            }

            destination.Clear();
            foreach (var instrument in instruments.OrderBy(i => i))
            {
                if (songs.All(s => SongHasInstrument(s, instrument)))
                {
                    destination.Add(instrument);
                }
            }
        }

        public static void GetCandidateModes(SongEntry song, YargProfile profile, Instrument instrument,
            List<GameMode> destination)
        {
            destination.Clear();

            void TryAdd(GameMode mode)
            {
                if (!destination.Contains(mode) && mode.PossibleInstrumentsForSong(song).Contains(instrument))
                {
                    destination.Add(mode);
                }
            }

            TryAdd(profile.GameMode);
            TryAdd(instrument.ToNativeGameMode());
            foreach (var mode in EnumExtensions<GameMode>.Values)
            {
                if (mode != GameMode.Menu)
                {
                    TryAdd(mode);
                }
            }

            if (destination.Count == 0)
            {
                destination.Add(instrument.ToNativeGameMode());
            }
        }

        public static string GetInstrumentBlocker(SongEntry song, YargPlayer player, Instrument instrument,
            IEnumerable<Instrument> otherChosenInstruments, out GameMode mode, List<GameMode> modeBuffer = null)
        {
            var profile = player.Profile;
            modeBuffer ??= new List<GameMode>();
            GetCandidateModes(song, profile, instrument, modeBuffer);
            mode = modeBuffer[0];

            if (IsVocalsInstrument(instrument))
            {
                foreach (var otherInstrument in otherChosenInstruments)
                {
                    if (IsVocalsInstrument(otherInstrument) && otherInstrument != instrument)
                    {
                        return Localize.Key("Menu.DifficultySelect.Band.SingersMustMatch");
                    }
                }

                mode = GameMode.Vocals;
                if (profile.IsBot || player.DeviceInfo.HasMicrophoneAssigned)
                {
                    return null;
                }

                return Localize.Key("Menu.DifficultySelect.Band.NeedsMicrophone");
            }

            if (profile.IsBot)
            {
                return null;
            }

            if (!player.DeviceInfo.HasControllerAssigned)
            {
                return Localize.Key("Menu.DifficultySelect.Band.NeedsController");
            }

            for (int i = 0; i < modeBuffer.Count; i++)
            {
                var candidate = modeBuffer[i];
                if (player.DeviceInfo.HasGameplayBindingsForMode(candidate))
                {
                    mode = candidate;
                    return null;
                }
            }

            return Localize.Key("Menu.DifficultySelect.Band.NoBinding");
        }
    }
}
