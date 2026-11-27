using NUnit.Framework;
using YARG.Core;
using YARG.Core.Game;
using YARG.Menu.ProfileList;
using YARG.Player;
using YARG.Settings.Customization;

namespace YARG.Editor.Tests
{
    public class ProfileSettingsCatalogTests
    {
        [Test]
        public void IsEngineAllowed_RespectsModeRestrictions()
        {
            Assert.IsTrue(ProfileSettingsCatalog.IsEngineAllowed(GameMode.FiveFretGuitar, EnginePreset.SoloTaps.Id));
            Assert.IsFalse(ProfileSettingsCatalog.IsEngineAllowed(GameMode.FourLaneDrums, EnginePreset.SoloTaps.Id));

            Assert.IsTrue(ProfileSettingsCatalog.IsEngineAllowed(GameMode.Vocals, EnginePreset.Casual.Id));
            Assert.IsFalse(ProfileSettingsCatalog.IsEngineAllowed(GameMode.ProKeys, EnginePreset.Casual.Id));

            Assert.IsFalse(ProfileSettingsCatalog.IsEngineAllowed(GameMode.ProKeys, EnginePreset.Precision.Id));
            Assert.IsTrue(ProfileSettingsCatalog.IsEngineAllowed(GameMode.FiveFretGuitar, EnginePreset.Precision.Id));
        }

        [Test]
        public void ApplyGameModeDefaults_ResetsDisallowedEngineAndUpdatesInstrument()
        {
            var profile = new YargProfile
            {
                GameMode = GameMode.FiveFretGuitar,
                EnginePreset = EnginePreset.SoloTaps.Id,
            };

            ProfileSettingsCatalog.ApplyGameModeDefaults(profile, GameMode.ProKeys);

            Assert.AreEqual(GameMode.ProKeys, profile.GameMode);
            Assert.AreEqual(Instrument.ProKeys, profile.CurrentInstrument);
            Assert.AreEqual(EnginePreset.Default.Id, profile.EnginePreset);
        }

        [Test]
        public void ClampHelpers_RespectBounds()
        {
            Assert.AreEqual(ProfileSettingsCatalog.NOTE_SPEED_MIN, ProfileSettingsCatalog.ClampNoteSpeed(-5f));
            Assert.AreEqual(ProfileSettingsCatalog.NOTE_SPEED_MAX, ProfileSettingsCatalog.ClampNoteSpeed(999f));
            Assert.AreEqual(ProfileSettingsCatalog.HIGHWAY_LENGTH_MIN, ProfileSettingsCatalog.ClampHighwayLength(0f));
            Assert.AreEqual(ProfileSettingsCatalog.HIGHWAY_LENGTH_MAX, ProfileSettingsCatalog.ClampHighwayLength(25f));
        }
    }
}
