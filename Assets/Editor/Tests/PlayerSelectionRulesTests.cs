using System.Collections.Generic;
using NUnit.Framework;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Menu.DifficultySelect;
using YARG.Player;

namespace YARG.Editor.Tests
{
    [TestFixture]
    public class PlayerSelectionRulesTests
    {
        [Test]
        public void SetNoRangeShifts_TogglesRangeEnabledAndRangeCompress()
        {
            var profile = new YargProfile
            {
                GameMode = GameMode.FiveFretGuitar,
                RangeEnabled = true
            };
            var possibleModifiers = new List<Modifier> { Modifier.RangeCompress, Modifier.AllStrums };

            Assert.IsFalse(PlayerSelectionRules.HasNoRangeShifts(profile, possibleModifiers));

            PlayerSelectionRules.SetNoRangeShifts(profile, possibleModifiers, true);
            Assert.IsFalse(profile.RangeEnabled);
            Assert.IsTrue(profile.IsModifierActive(Modifier.RangeCompress));
            Assert.IsTrue(PlayerSelectionRules.HasNoRangeShifts(profile, possibleModifiers));

            PlayerSelectionRules.SetNoRangeShifts(profile, possibleModifiers, false);
            Assert.IsTrue(profile.RangeEnabled);
            Assert.IsFalse(profile.IsModifierActive(Modifier.RangeCompress));
            Assert.IsFalse(PlayerSelectionRules.HasNoRangeShifts(profile, possibleModifiers));
        }

        [Test]
        public void SetNoRangeShifts_WithoutRangeCompressSupport_OnlySetsRangeEnabled()
        {
            var profile = new YargProfile
            {
                GameMode = GameMode.FourLaneDrums,
                RangeEnabled = true
            };
            var possibleModifiers = new List<Modifier> { Modifier.NoKicks };

            PlayerSelectionRules.SetNoRangeShifts(profile, possibleModifiers, true);
            Assert.IsFalse(profile.RangeEnabled);
            Assert.IsFalse(profile.IsModifierActive(Modifier.RangeCompress));
            Assert.IsTrue(PlayerSelectionRules.HasNoRangeShifts(profile, possibleModifiers));
        }

        [Test]
        public void PickInstrument_PreservesPreferredWhenUnavailable_UpdatesWhenAvailable()
        {
            var profile = new YargProfile
            {
                GameMode = GameMode.FiveFretGuitar,
                CurrentInstrument = Instrument.FiveFretGuitar,
                PreferredInstrument = Instrument.FiveFretGuitar
            };

            var onlyBass = new[] { Instrument.FiveFretBass };
            PlayerSelectionRules.PickInstrument(profile, Instrument.FiveFretBass, onlyBass, 0);
            Assert.AreEqual(Instrument.FiveFretBass, profile.CurrentInstrument);
            Assert.AreEqual(Instrument.FiveFretGuitar, profile.PreferredInstrument);

            var guitarAndBass = new[] { Instrument.FiveFretGuitar, Instrument.FiveFretBass };
            profile.CurrentInstrument = Instrument.FiveFretGuitar;
            PlayerSelectionRules.PickInstrument(profile, Instrument.FiveFretBass, guitarAndBass, 0);
            Assert.AreEqual(Instrument.FiveFretBass, profile.CurrentInstrument);
            Assert.AreEqual(Instrument.FiveFretBass, profile.PreferredInstrument);
        }

        [Test]
        public void ShareVocalModifiers_CopiesToActiveVocalsOnly()
        {
            var leaderProfile = new YargProfile
            {
                GameMode = GameMode.Vocals,
                CurrentInstrument = Instrument.Vocals
            };
            leaderProfile.AddSingleModifier(Modifier.UnpitchedOnly);

            var activeVocalProfile = new YargProfile
            {
                GameMode = GameMode.Vocals,
                CurrentInstrument = Instrument.Harmony
            };
            var sittingOutProfile = new YargProfile
            {
                GameMode = GameMode.Vocals,
                CurrentInstrument = Instrument.Vocals
            };
            var guitarProfile = new YargProfile
            {
                GameMode = GameMode.FiveFretGuitar,
                CurrentInstrument = Instrument.FiveFretGuitar
            };

            var leader = new YargPlayer(leaderProfile, null);
            var activeVocal = new YargPlayer(activeVocalProfile, null);
            var sittingOutVocal = new YargPlayer(sittingOutProfile, null) { SittingOut = true };
            var guitarist = new YargPlayer(guitarProfile, null);

            PlayerSelectionRules.ShareVocalModifiers(leader, new[] { leader, activeVocal, sittingOutVocal, guitarist });

            Assert.IsTrue(activeVocal.Profile.IsModifierActive(Modifier.UnpitchedOnly));
            Assert.IsFalse(sittingOutVocal.Profile.IsModifierActive(Modifier.UnpitchedOnly));
            Assert.IsFalse(guitarist.Profile.IsModifierActive(Modifier.UnpitchedOnly));
        }

        [Test]
        public void GetTierLabel_FormatsInactiveAndTopTiers()
        {
            var inactive = new PartValues { Intensity = -1, SubTracks = 0 };
            StringAssert.StartsWith("? - ", PlayerSelectionRules.GetTierLabel(inactive));

            var tier5 = new PartValues { Intensity = 5, SubTracks = 1 };
            StringAssert.StartsWith("<color=#FF8400>5 - ", PlayerSelectionRules.GetTierLabel(tier5));

            var tier6 = new PartValues { Intensity = 6, SubTracks = 1 };
            StringAssert.StartsWith("<color=#FB443F>6 - ", PlayerSelectionRules.GetTierLabel(tier6));
        }

        [Test]
        public void IsVocalsInstrument_MatchesAllVocalsModesOnly()
        {
            Assert.IsTrue(PlayerSelectionRules.IsVocalsInstrument(Instrument.Vocals));
            Assert.IsTrue(PlayerSelectionRules.IsVocalsInstrument(Instrument.Harmony));
            Assert.IsTrue(PlayerSelectionRules.IsVocalsInstrument(Instrument.PartyVocals));
            Assert.IsFalse(PlayerSelectionRules.IsVocalsInstrument(Instrument.FiveFretGuitar));
            Assert.IsFalse(PlayerSelectionRules.IsVocalsInstrument(Instrument.ProKeys));
        }
    }
}
