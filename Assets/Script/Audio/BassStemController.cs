using System.Collections.Generic;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using UnityEngine;
using UnityEngine.Rendering;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Playback;
using YARG.Settings;
using YARG.Settings.Types;

namespace YARG.Gameplay
{
    public class BassStemController: StemController
    {
        private StemChannel   _channel;
        private int           _numPlayers;
        private VolumeSetting _volumeSetting;
        private bool          _allowMuting;
        private bool          _isMuted;
        private int           _numReverbs;
        private bool           _isOnlyStem;

        private float MaxVolume              => _volumeSetting.Value;
        private float MuteFactor             => _isMuted ? MaxVolume / _numPlayers : MaxVolume;
        private bool  ReverbSettingEnabled   => SettingsManager.Settings.UseStarpowerFx.Value != AudioFxMode.Off;
        private bool  IsWhammySettingEnabled => SettingsManager.Settings.UseWhammyFx.Value;



        //TODO: allow muting is based on if the song has only 1 stem also
        // TODO:  Figure out background stem logic
        public BassStemController(StemChannel channel, VolumeSetting volumeSetting, int numPlayers = 1, bool isOnlyStem = false)
        {
            _channel = channel;
            _numPlayers = numPlayers;
            _isOnlyStem = isOnlyStem;
            _volumeSetting = volumeSetting;
            SubscribeToVolumeSetting(volumeSetting);
        }

        private void SubscribeToVolumeSetting(VolumeSetting volumeSetting)
        {
            UpdateVolume();
            volumeSetting.OnChange += f =>
            {
                UpdateVolume();
            };
        }

        private void UpdateVolume(float fadeDurationMs = 0.0f)
        {
            YargLogger.LogDebug($"Updating volume for {_channel.Stem} to {MuteFactor} (Max: {MaxVolume}, Muted: {_isMuted}, Players: {_numPlayers})");
            _channel.SetVolume(MaxVolume * MuteFactor, fadeDurationMs);
        }

        public void SetMute(bool muted, float duration = 0.0f)
        {
            if (_isOnlyStem)
            {
                return;
            }

            var muteOnMiss = SettingsManager.Settings.MuteOnMiss.Value;
            if (muteOnMiss == AudioFxMode.Off || (muteOnMiss == AudioFxMode.MultitrackOnly && _allowMuting))
            {
                return;
            }
            UpdateVolume(duration);
        }

        public void SetReverb(bool reverb)
        {
            if (!ReverbSettingEnabled)
            {
                return;
            }

            _numReverbs += reverb ? 1 : -1;
            bool shouldReverb = _numReverbs > 0;
            _channel.SetReverb(shouldReverb);
        }

        public void SetWhammyPercent(float percent)
        {
            // If Whammy FX is turned off, ignore.
            if (!IsWhammySettingEnabled)
            {
                return;
            }

            //TODO: Handle this shit
            // If the specified stem is the same as the background stem,
            // ignore the request. This may be a chart without separate
            // stems for each instrument. In that scenario we don't want
            // to pitch bend because we'd be bending the entire track.
            // if (stem == _backgroundStem)
            // {
            //     return;
            // }

            // Set the pitch
            var percentClamped = Mathf.Clamp01(percent);
            _channel.SetWhammyPitch(percentClamped);
        }

        //TODO: move this outside
        // private double GetVolumeSetting()
        // {
        //     return _stem switch
        //     {
        //         SongStem.Guitar               => SettingsManager.Settings.GuitarVolume.Value,
        //         SongStem.Rhythm               => SettingsManager.Settings.RhythmVolume.Value,
        //         SongStem.Bass                 => SettingsManager.Settings.BassVolume.Value,
        //         SongStem.Keys                 => SettingsManager.Settings.KeysVolume.Value,
        //         var stem when stem.IsDrum()   => SettingsManager.Settings.DrumsVolume.Value,
        //         var stem when stem.IsVocals() => SettingsManager.Settings.VocalsVolume.Value,
        //         SongStem.Song                 => SettingsManager.Settings.SongVolume.Value,
        //         SongStem.Crowd                => SettingsManager.Settings.CrowdVolume.Value,
        //         SongStem.Sfx                  => SettingsManager.Settings.SfxVolume.Value,
        //         _                             => DEFAULT_VOLUME
        //     };
        // }
    }
}