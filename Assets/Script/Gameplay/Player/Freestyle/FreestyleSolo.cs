using System;
using System.Collections.Generic;
using YARG.Audio.BASS;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;

namespace YARG.Gameplay.Player
{
    /// <summary>
    /// The freestyle solo input model: which frets the player is sounding over the solo, how long a
    /// tapped phrase stays alive, and who owns the stem mute while the engine plays the solo for them.
    /// </summary>
    internal sealed class FreestyleSolo
    {
        private const double MUTE_LEAD_SECONDS = 0.1;
        private const double TAP_WINDOW_SECONDS = 0.25;
        private const double STRUM_REPICK_SECONDS = 0.06;

        private readonly BassStemSoundboard _soundboard;
        private readonly Func<GuitarEngine> _currentEngine;
        private readonly Action<bool> _setStemMuted;
        private readonly Action<int> _onPlayed;
        private readonly int _fretRowMask;
        private readonly int _highestFretIndex;
        private EngineTimer _hopoLeniency;

        private bool _strumPending;
        private bool _stemMutedBySolo;
        private int _tapMask;
        private int _phraseMask;
        private bool _phraseFretless;
        private int _unpickedMask;
        private double _unpickedTime;
        private int _lastHeldMask;
        private double _lastHeldTime;

        public FreestyleSolo(BassStemSoundboard soundboard, Func<GuitarEngine> currentEngine, Action<bool> setStemMuted,
            Action<int> onPlayed, int fretRowMask, double hopoLeniency)
        {
            _soundboard = soundboard;
            _currentEngine = currentEngine;
            _setStemMuted = setStemMuted;
            _onPlayed = onPlayed;
            _fretRowMask = fretRowMask;
            _highestFretIndex = HighestFretBit(fretRowMask);
            _hopoLeniency = new EngineTimer("Freestyle HOPO Leniency", hopoLeniency);
        }

        public void QueueStrum()
        {
            _strumPending = true;
        }

        public void QueueTap(int fretMask)
        {
            _tapMask = fretMask;
        }

        public void RequestPhrases(List<GuitarNote> notes, Func<double, double> playbackTime)
        {
            _soundboard.RequestPhrases(notes, playbackTime);
        }

        public void ReleaseFrets(int fretMask)
        {
            _soundboard.ReleaseFrets(fretMask);
        }

        public void StopVoices()
        {
            _soundboard.StopAll();
        }

        public void Reset()
        {
            ClearInput();
            _soundboard.StopAll();
        }

        private void ClearInput()
        {
            _hopoLeniency.Reset();
            if (_stemMutedBySolo)
            {
                _stemMutedBySolo = false;
                _setStemMuted(false);
            }

            _strumPending = false;
            _tapMask = 0;
            _phraseMask = 0;
            _phraseFretless = false;
            _unpickedMask = 0;
            _lastHeldMask = 0;
            _lastHeldTime = 0;
        }

        public void Update(double currentTime, float whammyAxis, bool isSeekingReplay)
        {
            bool strummed = _strumPending;
            _strumPending = false;
            int tappedMask = _tapMask;
            _tapMask = 0;

            var engine = _currentEngine();
            double songSpeed = engine.BaseParameters.SongSpeed;
            _hopoLeniency.SetSpeed(songSpeed);
            if (_hopoLeniency.IsExpired(currentTime))
            {
                _hopoLeniency.Reset();
            }

            double? soloTime = SoloTime(engine, currentTime, isSeekingReplay);
            if (soloTime == null)
            {
                Stop();
                return;
            }

            int heldMask = engine.PlayerButtonMask & _fretRowMask;
            bool fretsChanged = heldMask != _lastHeldMask;
            EndPhraseIfLifted(wasHeld: _lastHeldMask != 0, currentTime: currentTime, songSpeed: songSpeed);
            _lastHeldMask = heldMask;

            int playMask = heldMask != 0 ? heldMask : tappedMask;
            bool fretInput = fretsChanged || tappedMask != 0;
            bool hammerOn = _phraseMask != 0 && fretInput && playMask != 0;
            bool repick = strummed && playMask != 0 && playMask == _unpickedMask
                && currentTime - _unpickedTime <= STRUM_REPICK_SECONDS * songSpeed;
            if (strummed && !hammerOn && !repick && _hopoLeniency.IsActive)
            {
                strummed = false;
                _unpickedMask = 0;
                _hopoLeniency.Reset();
            }

            int sustainMask = heldMask | (_phraseFretless ? _phraseMask : 0);
            if (strummed || hammerOn)
            {
                sustainMask |= playMask == 0 ? GuitarEngine.OPEN_MASK : playMask;
            }

            bool soloAudio = _soundboard.TrySelectPhrase(soloTime.Value, currentTime);
            _stemMutedBySolo = soloAudio;
            _setStemMuted(soloAudio);
            _soundboard.SetHeldFrets(sustainMask);
            _soundboard.SetWhammyPitch(whammyAxis);
            if ((strummed || hammerOn) && soloAudio)
            {
                StartPhrase(strummed: strummed, hammerOn: hammerOn, playMask: playMask, fretless: heldMask == 0,
                    currentTime: currentTime);
            }
        }

        private static double? SoloTime(GuitarEngine engine, double currentTime, bool isSeekingReplay)
        {
            double? freestyleTime = engine.FreestyleSoloTime;
            if (freestyleTime == null || isSeekingReplay)
            {
                return null;
            }

            if (currentTime >= freestyleTime.Value - MUTE_LEAD_SECONDS)
            {
                return freestyleTime;
            }

            return null;
        }

        private void Stop()
        {
            ClearInput();
            _soundboard.SetHeldFrets(0);
            _soundboard.SetWhammyPitch(0);
        }

        private void EndPhraseIfLifted(bool wasHeld, double currentTime, double songSpeed)
        {
            if (_phraseMask == 0)
            {
                return;
            }

            if (wasHeld)
            {
                _lastHeldTime = currentTime;
            }
            else if (currentTime - _lastHeldTime > TAP_WINDOW_SECONDS * songSpeed)
            {
                _phraseMask = 0;
                _hopoLeniency.Reset();
            }
        }

        private void StartPhrase(bool strummed, bool hammerOn, int playMask, bool fretless, double currentTime)
        {
            if (strummed && playMask != 0)
            {
                _soundboard.PlayStrum(playMask);
                _onPlayed(playMask);
            }
            else
            {
                int playedMask = HighestFret(playMask);
                if (playedMask == 0)
                {
                    playedMask = GuitarEngine.OPEN_MASK;
                }

                if (strummed)
                {
                    _soundboard.PlayNote(playedMask);
                }
                else
                {
                    _soundboard.PlayLegato(playedMask);
                }

                _onPlayed(playedMask);
            }

            _phraseMask = playMask == 0 ? GuitarEngine.OPEN_MASK : playMask;
            _phraseFretless = fretless;
            _unpickedMask = strummed ? 0 : playMask;
            _unpickedTime = currentTime;
            _lastHeldTime = currentTime;
            if (hammerOn && !strummed)
            {
                _hopoLeniency.Start(currentTime);
            }
            else
            {
                _hopoLeniency.Reset();
            }
        }

        private int HighestFret(int noteMask)
        {
            for (int fret = _highestFretIndex; fret >= 0; fret--)
            {
                int mask = 1 << fret;
                if ((noteMask & mask) != 0)
                {
                    return mask;
                }
            }

            return 0;
        }

        private static int HighestFretBit(int fretRowMask)
        {
            int index = 0;
            for (int mask = fretRowMask >> 1; mask != 0; mask >>= 1)
            {
                index++;
            }

            return index;
        }
    }
}
