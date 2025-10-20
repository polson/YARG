using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Gameplay;
using YARG.Gameplay.Player;
using YARG.Settings;
using YARG.Settings.Types;
using static YARG.Gameplay.Player.PlayerEvent;

namespace YARG.Audio
{
    public class PlayerAudioManager : IDisposable
    {
        private readonly Dictionary<MixerGroup, List<AudioHandler>> _handlers;
        private readonly GameManager                                _gameManager;
        private readonly bool                                       _isMultiTrack;
        private readonly MixerController                            _mixerController;

        public PlayerAudioManager(GameManager gameManager, MixerController mixerController, bool isMultiTrack)
        {
            _gameManager = gameManager;
            _handlers = new Dictionary<MixerGroup, List<AudioHandler>>();
            _isMultiTrack = true;
            _mixerController = mixerController;
            _isMultiTrack = isMultiTrack;
        }

        public void AddPlayer(MixerGroup stem, BasePlayer player)
        {
            _mixerController.AddMixerGroup(stem);
            if (!_handlers.ContainsKey(stem))
            {
                _handlers[stem] = new List<AudioHandler>();
            }

            _handlers[stem].Add(
                new AudioHandler(
                    mixerGroup: stem,
                    player: player,
                    gameManager: _gameManager,
                    playerAudioManager: this,
                    volumeSetting: GetVolumeSettingForMixerGroup(stem),
                    stemStemMixerController: _mixerController.GetGroupController(stem),
                    songMixerController: _mixerController.GetGroupController(MixerGroup.Song),
                    isMultiTrack: _isMultiTrack
                )
            );
        }

        private VolumeSetting GetVolumeSettingForMixerGroup(MixerGroup mixerGroup)
        {
            var setting = mixerGroup switch
            {
                MixerGroup.Guitar => SettingsManager.Settings.GuitarVolume,
                MixerGroup.Bass   => SettingsManager.Settings.BassVolume,
                MixerGroup.Keys   => SettingsManager.Settings.KeysVolume,
                MixerGroup.Drums  => SettingsManager.Settings.DrumsVolume,
                MixerGroup.Vocals => SettingsManager.Settings.VocalsVolume,
                _                 => throw new ArgumentOutOfRangeException(nameof(mixerGroup))
            };
            return setting;
        }

        public void Dispose()
        {
            foreach (var handlerList in _handlers.Values)
            {
                foreach (var handler in handlerList)
                {
                    handler.Dispose();
                }
            }

            _handlers.Clear();
        }

        public int GetPlayerCountForStem(MixerGroup stem)
        {
            if (_handlers.ContainsKey(stem))
            {
                return _handlers[stem].Count;
            }

            return 0;
        }

        public int GetMutedPlayerCount(MixerGroup mixerGroup)
        {
            if (_handlers.ContainsKey(mixerGroup))
            {
                return _handlers[mixerGroup].Count(handler => handler.IsMuted);
            }
            return 0;
        }
    }

    public class AudioHandler : IDisposable
    {
        private readonly MixerGroup           _mixerGroup;
        private readonly BasePlayer           _player;
        private readonly GameManager          _gameManager;
        private readonly PlayerAudioManager   _playerAudioManager;
        private          bool                 _isMuted;
        private          bool                 _isMultiTrack;
        private          VolumeSetting        _volumeSetting;
        private          MixerGroupController _stemMixerController;
        private          MixerGroupController _songMixerController;

        public bool IsMuted => _isMuted;

        public AudioHandler(MixerGroup mixerGroup, BasePlayer player, GameManager gameManager,
            PlayerAudioManager playerAudioManager, VolumeSetting volumeSetting,
            MixerGroupController stemStemMixerController, MixerGroupController songMixerController, bool isMultiTrack)
        {
            _mixerGroup = mixerGroup;
            _gameManager = gameManager;
            _playerAudioManager = playerAudioManager;
            _player = player;
            _player.Events += HandlePlayerEvent;
            _volumeSetting = volumeSetting;
            _volumeSetting.OnChange += OnVolumeSettingChanged;
            _stemMixerController = stemStemMixerController;
            _songMixerController = songMixerController;
            _isMultiTrack = isMultiTrack;
        }

        private void OnVolumeSettingChanged(float volume)
        {
            YargLogger.LogDebug("SETTING VOLUME: " + volume + " FOR MIXER GROUP: " + _mixerGroup);
            _stemMixerController.SetVolume(volume);
        }

        private void HandlePlayerEvent(PlayerEvent playerEvent)
        {
            YargLogger.LogDebug($"Received event: {playerEvent} for mixer group {_mixerGroup}");
            switch (playerEvent)
            {
                case StarPowerChanged(var active):
                    OnStarPowerChanged(active);
                    break;
                case ReplayTimeChanged:
                    OnReplayTimeChanged();
                    break;
                case VisualsReset:
                    OnVisualsReset();
                    break;
                case NoteHit:
                    OnNoteHit();
                    break;
                case NoteMissed:
                    OnNoteMissed();
                    break;
                case SustainBroken:
                    OnSustainBroken();
                    break;
                case SustainEnded:
                    OnSustainEnded();
                    break;
                case WhammyChangedOnSustain(var whammyFactor):
                    OnWhammyChangedDuringSustain(whammyFactor);
                    break;
            }
        }

        private void OnReplayTimeChanged()
        {
            SetMuteState(false);
        }

        private void OnStarPowerChanged(bool active)
        {
            ChangeReverbState(active);
        }

        private void ChangeReverbState(bool active)
        {
            var setting = SettingsManager.Settings.UseStarpowerFx.Value;
            if (setting == AudioFxMode.Off)
            {
                return;
            }

            if (setting == AudioFxMode.MultitrackOnly && !_isMultiTrack)
            {
                return;
            }


            var controller = _songMixerController;
            if (_mixerGroup is MixerGroup.Drums or MixerGroup.Bass or MixerGroup.Guitar)
            {
                controller = _stemMixerController;
            }

            controller.SetReverb(active);
        }

        private void OnWhammyChangedDuringSustain(float whammyFactor)
        {
            ChangeWhammyPitch(whammyFactor);
        }

        private void ChangeWhammyPitch(float whammyFactor)
        {
            // If Whammy FX is turned off, ignore.
            if (!SettingsManager.Settings.UseWhammyFx.Value)
            {
                return;
            }

            // If the specified stem is the same as the background stem,
            // ignore the request. This may be a chart without separate
            // stems for each instrument. In that scenario we don't want
            // to pitch bend because we'd be bending the entire track.
            if (!_isMultiTrack)
            {
                return;
            }
            // Set the pitch
            _stemMixerController.SetPitch(whammyFactor);
        }

        private void OnSustainEnded()
        {
            ChangeWhammyPitch(0);
        }

        private void OnSustainBroken()
        {
            SetMuteState(true);
        }

        private void OnVisualsReset()
        {
            SetMuteState(false);
        }

        private void OnNoteMissed()
        {
            if (!_gameManager.IsSeekingReplay)
            {
                SetMuteState(true);
            }
        }

        private void OnNoteHit()
        {
            if (!_gameManager.IsSeekingReplay)
            {
                SetMuteState(false);
            }
        }

        private void SetMuteState(bool muted)
        {
            var setting = SettingsManager.Settings.MuteOnMiss.Value;
            var muteSettingOff = setting == AudioFxMode.Off;
            var isVocals = _mixerGroup == MixerGroup.Vocals;
            var muteSettingMultiTrack = setting == AudioFxMode.MultitrackOnly;

            var shouldNotMute =
                _isMuted == muted ||
                muteSettingOff ||
                isVocals ||
                (muteSettingMultiTrack && !_isMultiTrack);

            if (shouldNotMute)
            {
                return;
            }


            _isMuted = muted;
            var maxVolume = _volumeSetting.Value;
            var numMutedPlayers = _playerAudioManager.GetMutedPlayerCount(_mixerGroup);  // Now this count is correct
            var totalStemPlayers = _playerAudioManager.GetPlayerCountForStem(_mixerGroup);
            var newVolume = maxVolume * (1 - numMutedPlayers / totalStemPlayers);

            //TODO: wtf was the logic about Ensures the stem will still play at a minimum of 50%, even if all players mute
            //TODO: could we just clamp
            if (muted && newVolume < maxVolume * 0.5f && totalStemPlayers > 1)
            {
                newVolume = maxVolume * 0.5f;
            }
            YargLogger.LogDebug($"Setting volume for mixer group {_mixerGroup} to {newVolume} (muted: {_isMuted}, numMutedPlayers: {numMutedPlayers}, totalStemPlayers: {totalStemPlayers})");
            _stemMixerController.SetVolume(newVolume);
        }

        public void Dispose()
        {
            _player.Events -= HandlePlayerEvent;
            _volumeSetting.OnChange -= OnVolumeSettingChanged;
        }
    }
}