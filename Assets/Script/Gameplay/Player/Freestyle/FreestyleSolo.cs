using System;
using YARG.Audio.BASS;
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

        private readonly BassStemSoundboard _soundboard;
        private readonly GuitarEngine _engine;
        private readonly Action<bool> _setStemMuted;
        private readonly int _fretRowMask;
        private readonly int _maxFretIndex;
        private EngineTimer _hopoLeniency;

        private bool _strumPending;
        private bool _muted;
        private int _tapMask;
        private int _phraseMask;
        private int _lastHeldMask;
        private double _lastHeldTime;

        public FreestyleSolo(BassStemSoundboard soundboard, GuitarEngine engine, Action<bool> setStemMuted,
            int fretRowMask, int maxFretIndex, double hopoLeniency)
        {
            _soundboard = soundboard;
            _engine = engine;
            _setStemMuted = setStemMuted;
            _fretRowMask = fretRowMask;
            _maxFretIndex = maxFretIndex;
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

        public void Reset()
        {
            _hopoLeniency.Reset();
            RestoreMute();
            _strumPending = false;
            _tapMask = 0;
            _phraseMask = 0;
            _lastHeldMask = 0;
            _lastHeldTime = 0;
        }

        public void Update(double currentTime, float whammyAxis, bool isSeekingReplay)
        {
            bool strummed = _strumPending;
            _strumPending = false;
            int tappedMask = _tapMask;
            _tapMask = 0;

            double songSpeed = _engine.BaseParameters.SongSpeed;
            _hopoLeniency.SetSpeed(songSpeed);
            if (_hopoLeniency.IsExpired(currentTime))
            {
                _hopoLeniency.Reset();
            }

            double soloTime;
            if (!TryGetSoloTime(currentTime, isSeekingReplay, out soloTime))
            {
                Stop();
                return;
            }

            int heldMask = _engine.PlayerButtonMask & _fretRowMask;
            bool fretsChanged = heldMask != _lastHeldMask;
            EndPhraseIfLifted(wasHeld: _lastHeldMask != 0, currentTime: currentTime, songSpeed: songSpeed);
            _lastHeldMask = heldMask;

            int playMask = heldMask != 0 ? heldMask : tappedMask;
            bool fretInput = fretsChanged || tappedMask != 0;
            bool hammerOn = _phraseMask != 0 && fretInput && playMask != 0;
            if (strummed && !hammerOn && _hopoLeniency.IsActive)
            {
                strummed = false;
                _hopoLeniency.Reset();
            }

            int sustainMask = heldMask | _phraseMask;
            if (strummed || hammerOn)
            {
                sustainMask |= playMask == 0 ? GuitarEngine.OPEN_MASK : playMask;
            }

            bool soloAudio = _soundboard.HasSeedSet(soloTime);
            SetMuted(soloAudio);
            _soundboard.SetHeldFrets(sustainMask);
            _soundboard.SetWhammyPitch(whammyAxis);
            if ((strummed || hammerOn) && soloAudio)
            {
                StartPhrase(soloTime, strummed, hammerOn, playMask, currentTime);
            }
        }

        private bool TryGetSoloTime(double currentTime, bool isSeekingReplay, out double soloTime)
        {
            double? freestyleTime = _engine.FreestyleSoloTime;
            if (freestyleTime == null || isSeekingReplay)
            {
                soloTime = 0;
                return false;
            }

            soloTime = freestyleTime.Value;
            return currentTime >= soloTime - MUTE_LEAD_SECONDS;
        }

        private void Stop()
        {
            Reset();
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

        private void StartPhrase(double soloTime, bool strummed, bool hammerOn, int playMask, double currentTime)
        {
            _soundboard.SelectSeedSet(soloTime, currentTime);
            if (strummed && playMask != 0)
            {
                _soundboard.PlayStrum(playMask, semitones: 0, gain: 1);
            }
            else
            {
                int fretMask = HighestFret(playMask, _maxFretIndex);
                _soundboard.Play(fretMask == 0 ? GuitarEngine.OPEN_MASK : fretMask, semitones: 0, gain: 1);
            }

            _phraseMask = playMask == 0 ? GuitarEngine.OPEN_MASK : playMask;
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

        private static int HighestFret(int noteMask, int highestFret)
        {
            for (int fret = highestFret; fret >= 0; fret--)
            {
                int mask = 1 << fret;
                if ((noteMask & mask) != 0)
                {
                    return mask;
                }
            }

            return 0;
        }

        private void SetMuted(bool muted)
        {
            _muted = muted;
            _setStemMuted(muted);
        }

        private void RestoreMute()
        {
            if (_muted)
            {
                SetMuted(false);
            }
        }
    }
}
