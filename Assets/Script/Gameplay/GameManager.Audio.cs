using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Playback;
using YARG.Settings;

namespace YARG.Gameplay
{
    public partial class GameManager
    {
        private const double DEFAULT_VOLUME = 1.0;
        public class StemState
        {
            private SongStem _stem;
            public double Volume => GetVolumeSetting();
            public int Total;
            public int Audible;
            public int ReverbCount;
            public float WhammyPitch;

            public StemState(SongStem stem)
            {
                _stem = stem;
            }

            public double SetMute(bool muted)
            {
                if (muted)
                {
                    --Audible;
                }
                else if (Audible < Total)
                {
                    ++Audible;
                }

                return Volume * Audible / Total;
            }

            public bool SetReverb(bool reverb)
            {
                if (reverb)
                {
                    ++ReverbCount;
                }
                else if (ReverbCount > 0)
                {
                    --ReverbCount;
                }
                return ReverbCount > 0;
            }

            public float SetWhammyPitch(float percent)
            {
                // TODO: Would be nice to handle multiple inputs
                // but for now last one wins
                WhammyPitch = Mathf.Clamp01(percent);
                return WhammyPitch;
            }

            private double GetVolumeSetting()
            {
                return _stem switch
                {
                    SongStem.Guitar               => SettingsManager.Settings.GuitarVolume.Value,
                    SongStem.Rhythm               => SettingsManager.Settings.RhythmVolume.Value,
                    SongStem.Bass                 => SettingsManager.Settings.BassVolume.Value,
                    SongStem.Keys                 => SettingsManager.Settings.KeysVolume.Value,
                    var stem when stem.IsDrums()  => SettingsManager.Settings.DrumsVolume.Value,
                    var stem when stem.IsVocals() => SettingsManager.Settings.VocalsVolume.Value,
                    SongStem.Song                 => SettingsManager.Settings.SongVolume.Value,
                    SongStem.Crowd                => SettingsManager.Settings.CrowdVolume.Value,
                    SongStem.Sfx                  => SettingsManager.Settings.SfxVolume.Value,
                    SongStem.DrumSfx              => SettingsManager.Settings.DrumSfxVolume.Value,
                    _                             => DEFAULT_VOLUME
                };
            }
        }

        private readonly Dictionary<SongStem, StemState> _stemStates = new();
        private SongStem _backgroundStem;

        private void LoadAudio()
        {
            _stemStates.Clear();
            _mixer = Song.LoadAudio(GlobalVariables.State.SongSpeed, DEFAULT_VOLUME);
            if (_mixer == null)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load audio!";
                return;
            }

            _backgroundStem = SongStem.Song;
            foreach (var channel in _mixer.Channels)
            {
                var stemState = new StemState(channel.Stem);
                var key =
                    channel.Stem.IsDrums()  ? SongStem.Drums :
                    channel.Stem.IsVocals() ? SongStem.Vocals :
                                              channel.Stem;
                _stemStates.TryAdd(key, stemState);
            }

            _backgroundStem = _stemStates.Count > 1 ? SongStem.Song : _stemStates.First().Key;
        }

        public void ChangeStarPowerStatus(bool active)
        {
            if (SettingsManager.Settings.UseCrowdFx.Value == CrowdFxMode.Disabled)
                return;

            StarPowerActivations += active ? 1 : -1;
            if (StarPowerActivations < 0)
                StarPowerActivations = 0;
        }
    }
}