using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Audio.BASS;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Game;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Visuals;
using YARG.Helpers;
using YARG.Helpers.Extensions;
using YARG.Playback;
using YARG.Player;
using YARG.Settings;
using YARG.Themes;
using Random = UnityEngine.Random;

namespace YARG.Gameplay.Player
{
    public class FiveFretGuitarPlayer : TrackPlayer<GuitarEngine, GuitarNote>
    {
        private const double SUSTAIN_END_MUTE_THRESHOLD = 0.1;

        private const double DEDICATED_OPEN_FLASH_HOLD_DURATION = 0.05;

        private const int SHIFT_INDICATOR_MEASURES_BEFORE = 5;

        private const double FREESTYLE_MUTE_LEAD_SECONDS = 0.1;
        private const double FREESTYLE_TAP_WINDOW_SECONDS = 0.25;

        public bool UsingOpenLane { get; private set; }

        // Record of the most recent time that each BRE lane has been lit up by any of the actions that map to it
        protected Dictionary<int, double> FretToMostRecentTime;
        protected virtual Dictionary<int, double> CreateFretToMostRecentTime() => new()
        {
            { (int)FiveFretGuitarFret.Open,      0 }, // We'll also use this for driving the dedicated open fret's "flashing" effect, like on drums
            { (int)FiveFretGuitarFret.Green,     0 },
            { (int)FiveFretGuitarFret.Red,       0 },
            { (int)FiveFretGuitarFret.Yellow,    0 },
            { (int)FiveFretGuitarFret.Blue,      0 },
            { (int)FiveFretGuitarFret.Orange,    0 },
        };


        // Key is an int corresponding to a FiveFretGuitarFret (or SixFretGuitarFret on 6F)
        // Value is the fret's lateral position on the fret array
        protected Dictionary<int, int> HighwayOrdering;

        // Used to control fret brightness for the dedicated open lane
        protected bool _openSustaining = false;

        protected virtual int GetFretIndex(int action)
        {
            return action switch
            {
                (int)GuitarAction.Fret1 => (int)FiveFretGuitarFret.Green,
                (int)GuitarAction.Fret2 => (int)FiveFretGuitarFret.Red,
                (int)GuitarAction.Fret3 => (int)FiveFretGuitarFret.Yellow,
                (int)GuitarAction.Fret4 => (int)FiveFretGuitarFret.Blue,
                (int)GuitarAction.Fret5 => (int)FiveFretGuitarFret.Orange,
                YargFiveFretGuitarEngine.OPEN_BRE_INPUT => (int)FiveFretGuitarFret.Open,
                _                  => throw new ArgumentOutOfRangeException(nameof(action))
            };
        }

        protected float GetLanePositionOrCentered(int fret)
        {
            if (HighwayOrdering.ContainsKey(fret))
            {
                return HighwayOrdering[fret];
            }

            return (LaneCount - 1) / 2;
        }

        public int GetLanePosition(FiveFretGuitarFret fret)
        {
            return HighwayOrdering[(int)fret];
        }

        public override bool ShouldUpdateInputsOnResume => true;

        /// See <see cref="StarMultiplierThresholds"/>
        private static float[] GuitarStarMultiplierThresholds => new[]
        {
            0.06f, 0.12f, 0.2f, 0.47f, 0.78f, 1.15f
        };

        /// See <see cref="StarMultiplierThresholds"/>
        protected static float[] BassStarMultiplierThresholds => new[]
        {
            0.05f, 0.1f, 0.19f, 0.47f, 0.78f, 1.15f
        };

        public GuitarEngineParameters EngineParams { get; protected set; }

        private double TimeFromSpawnToStrikeline => SpawnTimeOffset - (-STRIKE_LINE_POS / NoteSpeed);

        public struct RangeShiftIndicator
        {
            public double Time;
            public bool RightSide;
            public int Offset;
            public bool RangeIndicator;
        }

        private FiveFretRangeShift[] _allRangeShiftEvents;
        private readonly Queue<FiveFretRangeShift> _rangeShiftEventQueue = new();
        private FiveFretRangeShift CurrentRange { get; set; }
        private readonly Queue<RangeShiftIndicator> _shiftIndicators = new();
        private int _shiftIndicatorIndex;
        private bool _fretPulseStarting;
        private double _fretPulseStartTime;

        private List<int> _activeFrets = new(5);

        [Header("Five Fret Specific")]
        [SerializeField]
        protected FretArray _fretArray;
        [SerializeField]
        private Pool _shiftIndicatorPool;
        [SerializeField]
        private Pool _rangeIndicatorPool;

        protected override float[] StarMultiplierThresholds { get; set; } =
            GuitarStarMultiplierThresholds;

        public float WhammyFactor { get; protected set; }

        protected int _sustainCount;

        protected SongStem _stem;
        private double _practiceSectionStartTime;
        private BassStemSoundboard _soundboard;
        private EngineTimer _freestyleHopoLeniency;
        private readonly FreestyleSoloState _freestyle = new();
        private float _whammyAxis;

        public override void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView, StemMixer mixer, int? currentHighScore)
        {
            _stem = player.Profile.CurrentInstrument.ToSongStems().First();
            if (_stem == SongStem.Bass && mixer[SongStem.Bass] == null)
            {
                _stem = SongStem.Rhythm;
            }

            base.Initialize(index, player, chart, trackView, mixer, currentHighScore);
        }

        protected override InstrumentDifficulty<GuitarNote> GetNotes(SongChart chart)
        {
            var track = chart.GetFiveFretTrack(Player.Profile.CurrentInstrument).Clone();
            return track.GetDifficulty(Player.Profile.CurrentDifficulty);
        }

        /// <summary>The instrument that counts as "bass" for star multiplier threshold purposes.</summary>
        protected virtual Instrument GetBassInstrument() => Instrument.FiveFretBass;

        /// <summary>The engine preset to use for this guitar variant.</summary>
        protected virtual EnginePreset.FiveFretGuitarPreset GetEnginePreset() => Player.EnginePreset.FiveFretGuitar;

        /// <summary>Creates the concrete guitar engine instance.</summary>
        protected virtual GuitarEngine BuildEngine(GuitarEngineParameters parameters)
            => new YargFiveFretGuitarEngine(NoteTrack, SyncTrack, parameters, Player.Profile.IsBot);

        protected override GuitarEngine CreateEngine()
        {
            // If on bass, replace the star multiplier threshold
            bool isBass = Player.Profile.CurrentInstrument == GetBassInstrument();
            if (isBass)
            {
                StarMultiplierThresholds = BassStarMultiplierThresholds;
            }

            if (!Player.IsReplay)
            {
                EngineParams = GetEnginePreset().Create(StarMultiplierThresholds, SoloBonusStarMultiplierThresholds, isBass,
                    SettingsManager.Settings.FreestyleSolos.Value);
            }
            else
            {
                EngineParams = (GuitarEngineParameters)Player.EngineParameterOverride;
            }

            FretToMostRecentTime = CreateFretToMostRecentTime();
            _freestyleHopoLeniency = new EngineTimer("Freestyle HOPO Leniency", EngineParams.HopoLeniency);

            if (EngineContainer != null)
            {
                GameManager.EngineManager.Unregister(EngineContainer);
                EngineContainer = null;
            }

            var engine = BuildEngine(EngineParams);
            EngineContainer = GameManager.EngineManager.Register(engine, NoteTrack, Chart, Player.RockMeterPreset);

            HitWindow = EngineParams.HitWindow;

            YargLogger.LogFormatDebug("Note count: {0}", NoteTrack.Notes.Count);

            engine.OnNoteHit += OnNoteHit;
            engine.OnNoteMissed += OnNoteMissed;
            engine.OnOverstrum += OnOverhit;

            engine.OnSustainStart += OnSustainStart;
            engine.OnSustainEnd += OnSustainEnd;

            engine.OnSoloStart += OnSoloStart;
            engine.OnSoloEnd += OnSoloEnd;

            engine.OnCodaStart += OnCodaStart;
            engine.OnCodaEnd += OnCodaEnd;

            engine.OnStarPowerPhraseHit += OnStarPowerPhraseHit;
            engine.OnStarPowerPhraseMissed += OnStarPowerPhraseMissed;
            engine.OnStarPowerStatus += OnStarPowerStatus;
            engine.OnStarPowerReady += OnStarPowerReady;

            engine.OnCountdownChange += OnCountdownChange;

            EngineContainer.OnHappinessOverFail += OnHappinessOverFail;
            EngineContainer.OnHappinessNearFail += OnHappinessNearFail;

            return engine;
        }

        protected override void FinishInitialization()
        {
            base.FinishInitialization();

            MakeHighwayOrdering();

            InitializeIndicatorStripes();
            InitializeFretArray();

            if (Player.Profile.RangeEnabled)
            {
                _allRangeShiftEvents = FiveFretRangeShift.GetRangeShiftEvents(NoteTrack);
                InitializeRangeShift();
            }

            LaneElement.DefineLaneScale(Player.Profile.CurrentInstrument, LaneCount);

            GameManager.BeatEventHandler.Visual.Subscribe(_fretArray.PulseFretColors, BeatEventType.StrongBeat);

            _soundboard = GameManager.CreateStemSoundboard(_stem);
            if (EngineParams.FreestyleSolos)
            {
                if (_soundboard == null)
                {
                    YargLogger.LogWarning("Freestyle solos are enabled for this chart but stem replay is unavailable.");
                }
                else
                {
                    _soundboard.RequestSeeds(BuildSeedSets());
                }
            }
        }

        protected virtual void InitializeIndicatorStripes()
        {
            IndicatorStripes.Initialize(Player.EnginePreset.FiveFretGuitar);
        }

        protected virtual void InitializeFretArray()
        {
            _fretArray.Initialize(
                HighwayOrdering,
                LaneCount,
                null,
                Player.ColorProfile.FiveFretGuitar,
                Player.ThemePreset,
                VisualStyle.FiveFretGuitar
            );
        }

        protected override void ResetDifficulty(double time)
        {
            Engine.ReplaceChart(NoteTrack);
            ResetFreestyleState();

            if (Player.Profile.RangeEnabled)
            {
                _allRangeShiftEvents = FiveFretRangeShift.GetRangeShiftEvents(NoteTrack);
                ResetRangeShift(GameManager.VisualTime);
            }

            _fretArray.ResetAll();
            base.ResetDifficulty(time);
        }

        public override void ResetPracticeSection()
        {
            base.ResetPracticeSection();
            ResetRangeShift(_practiceSectionStartTime);

            _fretArray.ResetAll();
        }

        public override void SetPracticeSection(uint start, uint end)
        {
            base.SetPracticeSection(start, end);

            // This will set the current range correctly
            _practiceSectionStartTime = SyncTrack.TickToTime(start);
            ResetRangeShift(_practiceSectionStartTime);
        }

        protected override void ResetLastHitTimes()
        {
            foreach (var fret in HighwayOrdering.Keys)
            {
                FretToMostRecentTime[fret] = 0;
            }
        }

        public override void SetReplayTime(double time)
        {
            ResetRangeShift(time);
            base.SetReplayTime(time);
        }

        protected override void UpdateVisuals(double visualTime)
        {
            // Update coda lane emissions if necessary
            if (Engine.IsCodaActive)
            {
                UpdateBreLaneEmissions(visualTime);
            }

            base.UpdateVisuals(visualTime);
            UpdateRangeShift(visualTime);
            UpdateFretArray();
        }

        /// <summary>
        /// Sets the emission color of BRE lanes depending on the currently available score value.
        /// One BRE lane per fret, lit by the most recent time that fret was pressed.
        /// </summary>
        protected virtual void UpdateBreLaneEmissions(double visualTime)
        {
            foreach (var (breLaneIndex, highwayOrderingIndex) in HighwayOrdering)
            {
                var mostRecentTime = FretToMostRecentTime[breLaneIndex];
                var normalizedTimeSinceLastHit = CodaSection.GetNormalizedTimeSinceLastHit(visualTime, mostRecentTime);
                BRELanes[highwayOrderingIndex].SetEmissionColor(normalizedTimeSinceLastHit);
            }
        }

        public void UpdateRangeShift(double visualTime)
        {
            if (!_rangeShiftEventQueue.TryPeek(out var nextShift))
            {
                return;
            }

            if (_shiftIndicators.TryPeek(out var shiftIndicator) && shiftIndicator.Time <= visualTime + SpawnTimeOffset)
            {
                // The range indicator is dealt with in its own function
                if (shiftIndicator.RangeIndicator)
                {
                    SpawnRangeIndicator(nextShift);
                    return;
                }
                if (!_shiftIndicatorPool.CanSpawnAmount(1))
                {
                    return;
                }

                var poolable = _shiftIndicatorPool.TakeWithoutEnabling();
                if (poolable == null)
                {
                    YargLogger.LogWarning("Attempted to spawn shift indicator, but it's at its cap!");
                    return;
                }

                YargLogger.LogDebug("Shift indicator spawned!");

                ((GuitarShiftIndicatorElement) poolable).RangeShiftIndicator = shiftIndicator;
                poolable.EnableFromPool();

                _shiftIndicators.Dequeue();

                if (!_fretPulseStarting)
                {
                    _fretPulseStarting = true;
                    _fretPulseStartTime = nextShift.Time - (nextShift.BeatDuration * SHIFT_INDICATOR_MEASURES_BEFORE);
                }
            }

            if (_fretPulseStarting && _fretPulseStartTime <= visualTime)
            {
                for (var i = nextShift.Position; i < nextShift.Position + nextShift.Size; i++)
                {
                    _fretArray.SetFretColorPulse(i, true, (float) nextShift.BeatDuration);
                }

                _fretPulseStarting = false;
            }


            // Turn off the pulsing and switch active frets now that we're in the new range
            if (nextShift.Time <= visualTime)
            {
                _rangeShiftEventQueue.Dequeue();
                foreach (var fretIndex in HighwayOrdering.Keys)
                {
                    _fretArray.SetFretColorPulse(fretIndex, false, (float) nextShift.BeatDuration);
                }

                _fretPulseStarting = false;
                CurrentRange = nextShift;
                SetActiveFretsForShiftEvent(nextShift);
            }
        }

        private void ResetRangeShift(double time)
        {
            if (!Player.Profile.RangeEnabled)
            {
                return;
            }

            // Despawn shift indicators and rebuild the shift queues based on the replay time
            _rangeShiftEventQueue.Clear();
            _shiftIndicators.Clear();
            _shiftIndicatorPool.ReturnAllObjects();
            _rangeIndicatorPool.ReturnAllObjects();
            InitializeRangeShift(time);

        }

        protected virtual void UpdateFretArray()
        {
            for (var action = GuitarAction.GreenFret; action <= GetFretActionMax(); action++)
            {
                _fretArray.SetPressed(GetFretIndex((int)action), IsPlayerFretHeld(action));
            }

            if (UsingOpenLane) {
                var openInputDelta = GameManager.VisualTime - FretToMostRecentTime[(int)FiveFretGuitarFret.Open];


                _fretArray.SetPressedImpulse(
                    (int) FiveFretGuitarFret.Open,
                    _openSustaining || (openInputDelta < DEDICATED_OPEN_FLASH_HOLD_DURATION),
                    Fret.AnimType.CorrectNormal
                );
            }
        }

        protected virtual GuitarAction GetFretActionMax() => GuitarAction.OrangeFret;

        private void SpawnRangeIndicator(FiveFretRangeShift nextShift)
        {
            if (!_rangeIndicatorPool.CanSpawnAmount(1))
            {
                return;
            }

            var poolable = _rangeIndicatorPool.TakeWithoutEnabling();
            if (poolable == null)
            {
                YargLogger.LogWarning("Attempted to spawn range indicator, but it's at its cap!");
                return;
            }

            YargLogger.LogDebug("Range indicator spawned!");

            ((GuitarRangeIndicatorElement) poolable).RangeShift = nextShift;
            poolable.EnableFromPool();

            _shiftIndicators.Dequeue();
        }

        public override void SetStemMuteState(bool muted)
        {
            if (IsStemMuted != muted)
            {
                GameManager.ChangeStemMuteState(_stem, muted);
                IsStemMuted = muted;
            }
        }

        public override void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(_stem, active);
        }

        protected override void ResetVisuals()
        {
            base.ResetVisuals();
            ResetFreestyleState();

            StrikelineAnimator.SetSustaining(false);
            _fretArray.ResetAll();
        }

        protected override void InitializeSpawnedNote(IPoolable poolable, GuitarNote note)
        {
            ((FiveFretGuitarNoteElement) poolable).NoteRef = note;
        }

        protected override void InitializeSpawnedLane(LaneElement lane, GuitarNote note)
        {
            lane.SetAppearance(
                Player.Profile.CurrentInstrument,
                note.LaneNote,
                GetLanePositionOrCentered(note.Fret),
                LaneCount,
                Player.ColorProfile.FiveFretGuitar.GetNoteColor(note.Fret).ToUnityColor()
            );
        }

        // TrackPlayer doesn't know how each player subclass handles highway orderings; all it knows
        // is the number of BRE lanes. It's naively giving us laneIndices from 0 to n, where n is
        // LaneCount-1. 0 represents the left side of the highway and n represents the right. That's
        // the position that's represented by the values in _highwayOrdering, but we want to operate
        // over the dictionary keys, not values. Thus, we need to answer the question of "what fret
        // lives at the position we're being given?"
        protected override void InitializeBRELane(LaneElement lane, int laneIndex)
        {
            int lanedFret = -1;

            foreach (var (fret, position) in HighwayOrdering)
            {
                if (position == laneIndex)
                {
                    lanedFret = fret;
                    break;
                }
            }

            if (lanedFret == -1)
            {
                YargLogger.LogError("Tried to make a BRE lane for a fret with no highway position.");
                return;
            }


            lane.SetAppearance(
                Player.Profile.CurrentInstrument,
                lanedFret,
                laneIndex,
                LaneCount,
                Player.ColorProfile.FiveFretGuitar.GetNoteColor(lanedFret).ToUnityColor());
        }

        protected override void ModifyLaneFromNote(LaneElement lane, GuitarNote note)
        {
            if (NoteIsFullWidth(note))
            {
                lane.ToggleFullWidth(true);
            }
            else
            {
                lane.MultiplyScale(0.85f);
            }
        }

        protected override void RescaleLanesForBRE()
        {
            LaneElement.DefineLaneScale(Player.Profile.CurrentInstrument, LaneCount, true);
        }

        private void OnLaneHit(int action)
        {
            var asFret = GetFretIndex(action);

            FretToMostRecentTime[asFret] = GameManager.VisualTime;

            if (asFret is (int)FiveFretGuitarFret.Open && !UsingOpenLane)
            {
                _fretArray.PlayFullWidthHitAnimation();
            }
            else
            {
                _fretArray.PlayCodaHitAnimation((int) asFret);
            }
        }

        protected override void OnCodaStart(CodaSection coda)
        {
            base.OnCodaStart(coda);
            CurrentCoda.OnLaneHit += OnLaneHit;

            CurrentCoda.SetLaneIndexes(new()
            {
                // Open forwards its inputs to the green scoring zone
                { YargFiveFretGuitarEngine.OPEN_BRE_INPUT, (int)FiveFretGuitarFret.Green },
                { (int)GuitarAction.GreenFret, (int)FiveFretGuitarFret.Green },
                { (int)GuitarAction.RedFret, (int)FiveFretGuitarFret.Red },
                { (int)GuitarAction.YellowFret, (int)FiveFretGuitarFret.Yellow },
                { (int)GuitarAction.BlueFret, (int)FiveFretGuitarFret.Blue },
                { (int)GuitarAction.OrangeFret, (int)FiveFretGuitarFret.Orange },
            });

            _fretArray.SetBreMode(true);
        }

        protected override void OnCodaEnd(CodaSection coda)
        {
            base.OnCodaEnd(coda);
            CurrentCoda.OnLaneHit -= OnLaneHit;

            _fretArray.SetBreMode(false);
        }

        protected override void OnNoteHit(int index, GuitarNote chordParent)
        {
            base.OnNoteHit(index, chordParent);

            if (IsAutoPlayed(chordParent))
            {
                return;
            }

            _soundboard?.StopAll();
            RecordNote(chordParent);

            if (GameManager.Paused) return;

            foreach (var note in chordParent.AllNotes)
            {
                (NotePool.GetByKey(note) as FiveFretGuitarNoteElement)?.HitNote();

                if (NoteIsFullWidth(note))
                {
                    _fretArray.PlayFullWidthHitAnimation();
                }
                else
                {
                    if (note.Fret is (int) FiveFretGuitarFret.Open)
                    {
                        FretToMostRecentTime[(int)FiveFretGuitarFret.Open] = GameManager.VisualTime;
                    }

                    _fretArray.PlayHitAnimation(note.Fret);
                }
            }
        }

        protected override void OnDropOut()
        {
            base.OnDropOut();
            ResetFreestyleState();
        }

        private void ResetFreestyleState()
        {
            _freestyleHopoLeniency.Reset();
            RestoreStemMute();
            _freestyle.Reset();
            _soundboard?.StopAll();
        }

        protected override void OnNoteMissed(int index, GuitarNote chordParent)
        {
            base.OnNoteMissed(index, chordParent);
            RecordNote(chordParent);

            foreach (var note in chordParent.AllNotes)
            {
                (NotePool.GetByKey(note) as FiveFretGuitarNoteElement)?.MissNote();
            }
        }

        protected override void OnOverhit()
        {
            base.OnOverhit();

            if (GameManager.IsSeekingReplay)
            {
                return;
            }

            if (SettingsManager.Settings.OverstrumAndOverhitSoundEffects.Value)
            {
                const int MIN = (int) SfxSample.Overstrum1;
                const int MAX = (int) SfxSample.Overstrum4;

                var randomOverstrum = (SfxSample) Random.Range(MIN, MAX + 1);
                GlobalAudioHandler.PlaySoundEffect(randomOverstrum);
            }

            // To check if held frets are valid
            GuitarNote currentNote = null;
            if (Engine.NoteIndex < Notes.Count)
            {
                var note = Notes[Engine.NoteIndex];

                // Don't take the note if it's not within the hit window
                // TODO: Make BaseEngine.IsNoteInWindow public and use that instead
                var (frontEnd, backEnd) = Engine.CalculateHitWindow();
                if (Engine.CurrentTime >= (note.Time + frontEnd) && Engine.CurrentTime <= (note.Time + backEnd))
                {
                    currentNote = note;
                }
            }

            HandleOverstrum();

            // Play miss animation for every held fret that does not match the current note
            bool anyHeld = false;
            for (var action = GuitarAction.GreenFret; action <= GetFretActionMax(); action++)
            {
                if (!Engine.IsFretHeld(action))
                {
                    continue;
                }

                anyHeld = true;

                if (currentNote == null || (currentNote.NoteMask & (1 << (int) action)) == 0)
                {
                    _fretArray.PlayMissAnimation(GetFretIndex((int)action));
                }
            }

            // Play open-strum miss if no frets are held
            if (!anyHeld)
            {
                if (UsingOpenLane)
                {
                    _fretArray.PlayMissAnimation((int)FiveFretGuitarFret.Open);
                    FretToMostRecentTime[(int)FiveFretGuitarFret.Open] = GameManager.VisualTime;
                }
                else
                {
                    _fretArray.PlayFullWidthMissAnimation();
                }
            }
        }

        private void HandleOverstrum()
        {
            var soundboard = _soundboard;
            if (soundboard == null || !BassStemReplayPolicy.ShouldPlayMissFx())
            {
                return;
            }

            soundboard.ClearSeedSet();
            PlayFrets(soundboard, GetHeldFretMask());
        }

        private static void PlayFrets(BassStemSoundboard soundboard, int fretMask)
        {
            soundboard.Play(fretMask == 0 ? GuitarEngine.OPEN_MASK : fretMask, semitones: 0, gain: 1);
        }

        private void RecordNote(GuitarNote parent)
        {
            var soundboard = _soundboard;
            if (soundboard == null || IsAutoPlayed(parent))
            {
                return;
            }

            soundboard.Record(parent.NoteMask, GameManager.GetAudioPlaybackTime(parent.Time), GetClipDuration(parent),
                HasSustain(parent));
        }

        private static double GetClipDuration(GuitarNote parent)
        {
            return parent.NextNote != null ? parent.NextNote.Time - parent.Time : double.MaxValue;
        }

        private static bool HasSustain(GuitarNote parent)
        {
            foreach (var note in parent.AllNotes)
            {
                if (note.IsSustain)
                {
                    return true;
                }
            }

            return false;
        }

        private List<BassStemSeedSet> BuildSeedSets()
        {
            var seedSets = new List<BassStemSeedSet>();
            var notes = OriginalNoteTrack.Notes;
            int soloStart = 0;
            while (soloStart < notes.Count)
            {
                if (!notes[soloStart].IsSolo)
                {
                    soloStart++;
                    continue;
                }

                int soloEnd = soloStart;
                while (soloEnd + 1 < notes.Count && notes[soloEnd + 1].IsSolo && !notes[soloEnd + 1].IsSoloStart)
                {
                    soloEnd++;
                }

                seedSets.Add(BuildSeedSet(notes, soloStart, soloEnd));
                soloStart = soloEnd + 1;
            }

            return seedSets;
        }

        private BassStemSeedSet BuildSeedSet(List<GuitarNote> notes, int soloStart, int soloEnd)
        {
            var seedNotes = new List<BassStemSeedNote>();
            for (int index = soloStart; index <= soloEnd; index++)
            {
                seedNotes.Add(CreateSeedNote(notes[index]));
            }

            return new BassStemSeedSet(notes[soloStart].Time, notes[soloEnd].Time, seedNotes);
        }

        private BassStemSeedNote CreateSeedNote(GuitarNote note)
        {
            return new BassStemSeedNote(key: note.NoteMask, songSeconds: note.Time,
                playbackSeconds: GameManager.GetAudioPlaybackTime(note.Time),
                durationSeconds: GetClipDuration(note), sustainStartSeconds: 0, isSustain: HasSustain(note));
        }

        private bool TryGetCurrentSoloTime(out double soloTime)
        {
            int index = Engine.NoteIndex;
            if (index < Notes.Count && Notes[index].IsSolo)
            {
                soloTime = Notes[index].Time;
                return true;
            }

            if (index > 0)
            {
                soloTime = Notes[index - 1].Time;
                return true;
            }

            soloTime = 0;
            return false;
        }

        private int GetHeldFretMask()
        {
            int mask = 0;
            for (var action = GuitarAction.GreenFret; action <= GetFretActionMax(); action++)
            {
                if (IsPlayerFretHeld(action))
                {
                    mask |= 1 << (int) action;
                }
            }

            return mask;
        }

        protected bool IsPlayerFretHeld(GuitarAction action)
        {
            if (!Engine.IsFreestyleActive)
            {
                return Engine.IsFretHeld(action);
            }

            int inputMask = Engine.InputButtonMask | (Engine.InputButtonMask >> (int) GuitarAction.SoloGreenFret);
            return (inputMask & (1 << (int) action)) != 0;
        }

        protected override bool IsAutoPlayed(GuitarNote note)
        {
            return EngineParams.FreestyleSolos && note.ParentOrSelf.IsSolo;
        }

        private bool IsInFreestyleSolo()
        {
            if (!Engine.IsFreestyleActive)
            {
                return false;
            }

            int index = Engine.NoteIndex;
            if (index > 0 && Notes[index - 1].IsSolo)
            {
                return true;
            }

            return index < Notes.Count && Engine.CurrentTime >= Notes[index].Time - FREESTYLE_MUTE_LEAD_SECONDS;
        }

        protected override void UpdateInputs(double time)
        {
            base.UpdateInputs(time);

            bool strummed = _freestyle.StrumPending;
            _freestyle.StrumPending = false;
            int tappedMask = _freestyle.TapMask;
            _freestyle.TapMask = 0;

            UpdateFreestyleSolo(strummed: strummed, tappedMask: tappedMask);
        }

        private void UpdateFreestyleSolo(bool strummed, int tappedMask)
        {
            var soundboard = _soundboard;
            if (soundboard == null)
            {
                return;
            }

            double currentTime = Engine.CurrentTime;
            _freestyleHopoLeniency.SetSpeed(EngineParams.SongSpeed);
            if (_freestyleHopoLeniency.IsExpired(currentTime))
            {
                _freestyleHopoLeniency.Reset();
            }

            if (!IsInFreestyleSolo() || GameManager.IsSeekingReplay)
            {
                StopFreestyleAudio(soundboard);
                return;
            }

            int heldMask = GetHeldFretMask();
            bool fretsChanged = heldMask != _freestyle.LastHeldMask;
            EndPhraseIfLifted(wasHeld: _freestyle.LastHeldMask != 0, currentTime: currentTime);
            _freestyle.LastHeldMask = heldMask;

            int playMask = heldMask != 0 ? heldMask : tappedMask;
            bool fretInput = fretsChanged || tappedMask != 0;
            bool hammerOn = _freestyle.PhraseMask != 0 && fretInput && playMask != 0;
            if (strummed && !hammerOn && _freestyleHopoLeniency.IsActive)
            {
                strummed = false;
                _freestyleHopoLeniency.Reset();
            }

            int sustainMask = heldMask | _freestyle.PhraseMask;
            if (strummed || hammerOn)
            {
                sustainMask |= playMask == 0 ? GuitarEngine.OPEN_MASK : playMask;
            }

            bool soloAudio = TryGetCurrentSoloTime(out double soloTime) && soundboard.HasSeedSet(soloTime);
            SetFreestyleMute(soloAudio);
            soundboard.SetHeldFrets(sustainMask);
            soundboard.SetWhammyPitch(_whammyAxis);
            if ((strummed || hammerOn) && soloAudio)
            {
                StartFreestylePhrase(soundboard, soloTime: soloTime, strummed: strummed, hammerOn: hammerOn,
                    playMask: playMask, currentTime: currentTime);
            }
        }

        private void EndPhraseIfLifted(bool wasHeld, double currentTime)
        {
            if (_freestyle.PhraseMask == 0)
            {
                return;
            }

            if (wasHeld)
            {
                _freestyle.LastHeldTime = currentTime;
            }
            else if (currentTime - _freestyle.LastHeldTime > FREESTYLE_TAP_WINDOW_SECONDS * EngineParams.SongSpeed)
            {
                _freestyle.PhraseMask = 0;
                _freestyleHopoLeniency.Reset();
            }
        }

        private void StartFreestylePhrase(BassStemSoundboard soundboard, double soloTime, bool strummed,
            bool hammerOn, int playMask, double currentTime)
        {
            soundboard.SelectSeedSet(soloTime, currentTime);
            if (strummed && playMask != 0)
            {
                soundboard.PlayStrum(playMask, semitones: 0, gain: 1);
            }
            else
            {
                PlayFrets(soundboard, BassStemFretMask.Highest(playMask, (int) GetFretActionMax()));
            }

            _freestyle.PhraseMask = playMask == 0 ? GuitarEngine.OPEN_MASK : playMask;
            _freestyle.LastHeldTime = currentTime;
            if (hammerOn && !strummed)
            {
                _freestyleHopoLeniency.Start(currentTime);
            }
            else
            {
                _freestyleHopoLeniency.Reset();
            }
        }

        private void StopFreestyleAudio(BassStemSoundboard soundboard)
        {
            _freestyleHopoLeniency.Reset();
            RestoreStemMute();
            _freestyle.Reset();
            soundboard.SetHeldFrets(0);
            soundboard.SetWhammyPitch(0);
        }

        private void SetFreestyleMute(bool muted)
        {
            _freestyle.Muted = muted;
            SetStemMuteState(muted);
        }

        private void RestoreStemMute()
        {
            if (_freestyle.Muted)
            {
                SetFreestyleMute(false);
            }
        }

        protected override void UpdateMuteState(GuitarNote note, bool isMuted)
        {
            if (IsAutoPlayed(note))
            {
                return;
            }

            base.UpdateMuteState(note, isMuted);
        }

        private int GetFretMask(GuitarAction action)
        {
            if (action <= GetFretActionMax())
            {
                return 1 << (int) action;
            }

            if (action >= GuitarAction.SoloGreenFret && action <= GuitarAction.SoloOrangeFret)
            {
                return 1 << (action - GuitarAction.SoloGreenFret);
            }

            return 0;
        }

        protected virtual void OnSustainStart(GuitarNote parent)
        {
            if (IsAutoPlayed(parent))
            {
                return;
            }

            foreach (var note in parent.AllNotes)
            {
                if (parent.IsDisjoint && parent != note) continue;

                if (note.Fret is (int) FiveFretGuitarFret.Open)
                {
                    _openSustaining = true;
                }

                if (NoteIsFullWidth(note))
                {
                    if (note.Fret is (int) FiveFretGuitarFret.Open)
                    {
                        StrikelineAnimator.SetParticleColor(Player.ColorProfile.FiveFretGuitar.GetNoteColor(note.Fret).ToUnityColor());
                    }
                    else
                    {
                        StrikelineAnimator.SetParticleRainbow();
                    }

                    StrikelineAnimator.SetSustaining(true);
                }
                else
                {
                    _fretArray.SetSustained(note.Fret, true);
                }

                _sustainCount++;
            }
        }

        protected virtual void OnSustainEnd(GuitarNote parent, double timeEnded, bool finished)
        {
            if (IsAutoPlayed(parent))
            {
                return;
            }

            foreach (var note in parent.AllNotes)
            {
                if (parent.IsDisjoint && parent != note) continue;

                (NotePool.GetByKey(note) as FiveFretGuitarNoteElement)?.SustainEnd(finished);

                if (note.Fret is (int) FiveFretGuitarFret.Open)
                {
                    _openSustaining = false;
                }

                if (NoteIsFullWidth(note))
                {
                    StrikelineAnimator.SetSustaining(false);
                }
                else
                {
                    _fretArray.SetSustained(note.Fret, false);
                }

                _sustainCount--;
            }

            if (!finished)
            {
                if (!parent.IsDisjoint || _sustainCount == 0)
                {
                    SetStemMuteState(true);
                }
            }

            if (_sustainCount == 0)
            {
                WhammyFactor = 0;
                GameManager.ChangeStemWhammyPitch(_stem, 0);
            }
        }

        protected override void OnStarPowerPhraseMissed()
        {
            base.OnStarPowerPhraseMissed();
            foreach (var note in NotePool.AllSpawned)
            {
                (note as FiveFretGuitarNoteElement)?.OnStarPowerUpdated();
            }
        }

        protected override bool InterceptInput(ref GameInput input)
        {
            // Ignore SP in practice mode
            if (input.GetAction<GuitarAction>() == GuitarAction.StarPower && GameManager.IsPractice) return true;

            return false;
        }

        protected override void OnInputQueued(GameInput input)
        {
            base.OnInputQueued(input);

            var action = input.GetAction<GuitarAction>();
            int fretMask = GetFretMask(action);
            if (input.Button)
            {
                if (action is GuitarAction.StrumUp or GuitarAction.StrumDown)
                {
                    _freestyle.StrumPending = true;
                }

                if (fretMask != 0)
                {
                    _freestyle.TapMask = fretMask;
                }
            }
            else if (_soundboard != null && fretMask != 0)
            {
                _soundboard.Release(fretMask);
            }

            // Update the whammy factor
            if (action == GuitarAction.Whammy)
            {
                _whammyAxis = Mathf.Clamp01(input.Axis);
                if (_sustainCount > 0)
                {
                    WhammyFactor = _whammyAxis;
                    GameManager.ChangeStemWhammyPitch(_stem, WhammyFactor);
                }
            }
        }

        public override (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData()
        {
            var frame = new ReplayFrame(Player.Profile, EngineParams, Engine.EngineStats, ReplayInputs.ToArray());
            return (frame, Engine.EngineStats.ConstructReplayStats(Player.Profile.Name, Player.IsReplay));
        }


        private void InitializeRangeShift(double time = 0)
        {
            var firstShiftAfterFirstNote = false;
            _rangeShiftEventQueue.Clear();
            // Default to all frets on
            SetDefaultActiveFrets();

            // No range shifts, so just return
            if (_allRangeShiftEvents.Length < 1)
            {
                return;
            }

            // Now that we know there is at least one range shift, figure out if it is after the first note
            if (Notes.Count > 0 && _allRangeShiftEvents[0].Time > Notes[0].Time)
            {
                firstShiftAfterFirstNote = true;
            }

            if (_allRangeShiftEvents.Length == 1)
            {
                // There are no actual shifts (or we aren't shifting because of range compression), but we should dim unused frets
                CurrentRange = _allRangeShiftEvents[0];
                // If the range shift is after the first note, leave all the frets on because chart is broke
                if (!firstShiftAfterFirstNote)
                {
                    SetActiveFretsForShiftEvent(CurrentRange);
                }

                return;
            }

            // Turns out that we have range shifts that need indicators
            var firstEvent = _allRangeShiftEvents[0];

            FiveFretRangeShift mostRecentEvent = firstEvent;

            // Only queue range shifts that happen after time
            for (int i = 1; i < _allRangeShiftEvents.Length; i++)
            {
                FiveFretRangeShift e = _allRangeShiftEvents[i];
                // These have no visible effect on the track, so we just
                // want to make sure any that are current or in the future are queued
                // and to figure out which was the most recent event
                if (e.Time >= time)
                {
                    _rangeShiftEventQueue.Enqueue(e);
                    continue;
                }

                if (e.Time > mostRecentEvent.Time)
                {
                    mostRecentEvent = e;
                }
            }

            CurrentRange = mostRecentEvent;
            if (time < mostRecentEvent.Time)
            {
                // If we get here, the only range shifts are in the future
                SetDefaultActiveFrets();
            }
            else
            {
                SetActiveFretsForShiftEvent(CurrentRange);
            }

            // Figure out where the indicators should go
            var beatlines = Beatlines
                .Where(i => i.Type is BeatlineType.Measure or BeatlineType.Strong)
                .ToList();

            _shiftIndicators.Clear();
            var lastShiftRange = mostRecentEvent;
            int beatlineIndex = 0;

            foreach (var shift in _rangeShiftEventQueue.ToList())
            {
                if (shift.Position == lastShiftRange.Position && shift.Size == lastShiftRange.Size)
                {
                    continue;
                }

                // When shift.Position and lastShiftRange.Position are the same, this result doesn't matter because
                // the shift indicator won't be displayed, so it's OK that neither of these are <= or >=
                var shiftRight = Player.Profile.LeftyFlip
                    ? shift.Position < lastShiftRange.Position
                    : shift.Position > lastShiftRange.Position;

                double lastBeatTime = 0;
                double firstBeatTime = double.MaxValue;

                // Find the first beatline index after the range shift
                for (; beatlineIndex < beatlines.Count; beatlineIndex++)
                {
                    if (beatlines[beatlineIndex].Time > shift.Time)
                    {
                        lastBeatTime = beatlines[beatlineIndex].Time;
                        break;
                    }
                }

                // Add the indicators before the range shift
                // While we're doing this, figure out the time between beats
                for (int i = SHIFT_INDICATOR_MEASURES_BEFORE; i > 0; i--)
                {
                    var realIndex = beatlineIndex - i;

                    // If the indicator is before any measures, skip
                    if (realIndex < 0)
                    {
                        break;
                    }

                    firstBeatTime = beatlines[realIndex].Time < firstBeatTime ? beatlines[realIndex].Time : firstBeatTime;

                    _shiftIndicators.Enqueue(new RangeShiftIndicator
                    {
                        Time = beatlines[realIndex].Time,
                        RightSide = shiftRight,
                        Offset = shift.Position < lastShiftRange.Position ? shift.Position - 1 : 5 - shift.Position - shift.Size + 1,
                        RangeIndicator = i == 1 && !(shift.Position == lastShiftRange.Position && shift.Size == lastShiftRange.Size),
                    });
                }

                lastShiftRange = shift;

                // In case we have no samples for this shift event, 0.5 is a reasonable default
                shift.BeatDuration = firstBeatTime < double.MaxValue ? (lastBeatTime - firstBeatTime) / SHIFT_INDICATOR_MEASURES_BEFORE : 0.5;
            }
        }

        public bool NoteIsFullWidth(GuitarNote note)
        {
            return note.Fret switch
            {
                (int) FiveFretGuitarFret.Open => !UsingOpenLane,
                (int) FiveFretGuitarFret.Wildcard => true,
                _ => false
            };
        }

        private void SetActiveFretsForShiftEvent(FiveFretRangeShift range)
        {
            var newFrets = new List<int>();

            int start = range.Position;
            int end = start + range.Size;
            for (int i = start; i < end; i++)
            {
                newFrets.Add(i);
            }

            if (!newFrets.SequenceEqual(_activeFrets))
            {
                _activeFrets = newFrets;
                _fretArray.UpdateFretActiveState(_activeFrets);
            }
        }

        private void SetDefaultActiveFrets()
        {
            var newFrets = new List<int>();
            foreach (var fretIdx in HighwayOrdering.Keys)
            {
                newFrets.Add(fretIdx);
            }

            if (!newFrets.SequenceEqual(_activeFrets))
            {
                _activeFrets = newFrets;
                _fretArray.UpdateFretActiveState(_activeFrets);
            }
        }

        protected virtual void MakeHighwayOrdering()
        {
            UsingOpenLane = GryboHighwayHelpers.ShouldUseOpenLane(Player.Profile.OpenLaneDisplayType, NoteTrack.Notes);

            LaneCount = UsingOpenLane ? 6 : 5;
            BRELanes = new LaneElement[LaneCount];

            switch ((UsingOpenLane, Player.Profile.LeftyFlip))
            {
                case (false, false):
                    HighwayOrdering = GryboHighwayHelpers.DEFAULT_HIGHWAY_ORDERING;
                    break;
                case (false, true):
                    HighwayOrdering = GryboHighwayHelpers.LEFTY_HIGHWAY_ORDERING;
                    break;
                case (true, false):
                    HighwayOrdering = GryboHighwayHelpers.OPEN_LANE_HIGHWAY_ORDERING;
                    break;
                case (true, true):
                    HighwayOrdering = GryboHighwayHelpers.OPEN_LANE_LEFTY_HIGHWAY_ORDERING;
                    break;
            }
        }

        private sealed class FreestyleSoloState
        {
            public bool   StrumPending;
            public bool   Muted;
            public int    TapMask;
            public int    PhraseMask;
            public int    LastHeldMask;
            public double LastHeldTime;

            public void Reset()
            {
                StrumPending = false;
                Muted = false;
                TapMask = 0;
                PhraseMask = 0;
                LastHeldMask = 0;
                LastHeldTime = 0;
            }
        }
    }
}
