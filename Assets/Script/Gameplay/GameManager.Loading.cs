using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.Player;
using YARG.Menu;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.Settings;
using YARG.Playback;
using YARG.Player;
using YARG.Scores;
using YARG.Settings;
using YARG.Song;

namespace YARG.Gameplay
{
    public partial class GameManager
    {
        private enum LoadFailureState
        {
            None,
            Rescan,
            Error
        }

        [Header("Instrument Prefabs")]
        [SerializeField]
        private GameObject _fiveFretGuitarPrefab;
        [SerializeField]
        private GameObject _sixFretGuitarPrefab;
        [SerializeField]
        private GameObject _fourLaneDrumsPrefab;
        [SerializeField]
        private GameObject _fiveLaneDrumsPrefab;
        [SerializeField]
        private GameObject _proKeysPrefab;
        [SerializeField]
        private GameObject _fiveLaneKeysPrefab;
        [SerializeField]
        private GameObject _proGuitarPrefab;

        private const long LOADING_OUTLIER_THRESHOLD_MS = LoadingTrace.OutlierThresholdMilliseconds;
        private const float TRACK_SPAWN_HEIGHT = 100f;

        private LoadFailureState _loadState;
        private string _loadFailureMessage;
        private readonly Queue<PreloadedTrackObject> _preloadedTrackObjects = new();

        private sealed class PreloadedTrackObject
        {
            public GameObject Prefab { get; private set; }
            public GameObject Instance { get; private set; }

            public PreloadedTrackObject(GameObject prefab, GameObject instance)
            {
                Prefab = prefab;
                Instance = instance;
            }
        }

        // All access to chart data must be done through this event,
        // since things are loaded asynchronously
        // Players are initialized by hand and don't go through this event
        private event Action<SongChart> _chartLoaded;

        public event Action<SongChart> ChartLoaded
        {
            add
            {
                _chartLoaded += value;

                // Invoke now if already loaded, this event is only fired once
                var chart = Chart;
                if (chart != null) value?.Invoke(chart);
            }
            remove => _chartLoaded -= value;
        }

        private event Action _songLoaded;

        public event Action SongLoaded
        {
            add
            {
                _songLoaded += value;

                // Invoke now if already loaded, this event is only fired once
                if (_mixer != null)
                {
                    value?.Invoke();
                }
            }
            remove => _songLoaded -= value;
        }

        private event Action _songStarted;

        public event Action SongStarted
        {
            add
            {
                _songStarted += value;

                // Invoke now if already loaded, this event is only fired once
                if (IsSongStarted) value?.Invoke();
            }
            remove => _songStarted -= value;
        }

        private async void Start()
        {
            // Displays the loading screen
            using var context = new LoadingContext(runGarbageCollection: false);
            var global = GlobalVariables.Instance;

            // Disable until everything's loaded
            enabled = false;

            YargLogger.LogFormatInfo("Loading song {0} - {1}", Song.Name, Song.Artist);

            if (ReplayInfo != null)
            {
                if (!SongContainer.SongsByHash.TryGetValue(GlobalVariables.State.CurrentReplay.SongChecksum, out var songs))
                {
                    ToastManager.ToastWarning("Song not present in library");
                    global.LoadScene(SceneIndex.Menu);
                    return;
                }
                Song = songs[0];

                context.SetLoadingText("Loading replay...");
                if (!LoadReplay())
                {
                    ToastManager.ToastError("Failed to load replay!");
                    global.LoadScene(SceneIndex.Menu);
                    return;
                }

                if (!GlobalVariables.State.PlayingWithReplay)
                {
                    _replayController.gameObject.SetActive(true);
                }
                else
                {
                    _replayController.gameObject.SetActive(false);
                    var players = new List<YargPlayer>();
                    players.AddRange(PlayerContainer.Players);
                    for (int i = 0; i < YargPlayers.Count; i++)
                    {
                         players.Add(YargPlayers[i]);
                    }

                    YargPlayers = players.ToArray();
                }

                var replayIndex = 0;
                foreach (var player in YargPlayers)
                {
                    if (player.IsReplay)
                    {
                        player.ReplayIndex = replayIndex;
                        replayIndex++;
                    }
                }
            }

            context.Queue(UniTask.RunOnThreadPool(LoadChart), "Loading chart...");
            var parallelLoadStopwatch = System.Diagnostics.Stopwatch.StartNew();
            context.Queue(UniTask.RunOnThreadPool(LoadAudio), "Loading audio...");
            PreloadTrackPlayers();
            await context.Wait();
            parallelLoadStopwatch.Stop();
            YargLogger.LogFormatInfo("[LOADING] Parallel chart/audio wait took {0}ms", parallelLoadStopwatch.ElapsedMilliseconds);

            if (_loadState == LoadFailureState.Rescan)
            {
                ToastManager.ToastWarning("Chart requires a rescan!", () =>
                {
                    MenuManager.Instance.DisableCurrentMenu();
                    SettingsMenu.Instance.gameObject.SetActive(true);
                    SettingsMenu.Instance.SelectTabByName("SongManager");
                });

                global.LoadScene(SceneIndex.Menu);
                return;
            }

            if (_loadState == LoadFailureState.Error)
            {
                YargLogger.LogError(_loadFailureMessage);
                ToastManager.ToastError(_loadFailureMessage);

                global.LoadScene(SceneIndex.Menu);
                return;
            }

            var finalizeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            FinalizeChart();
            finalizeStopwatch.Stop();
            YargLogger.LogFormatInfo("[LOADING] FinalizeChart() took {0}ms", finalizeStopwatch.ElapsedMilliseconds);

            // Initialize song runner
            _songRunner = new SongRunner(
                _mixer,
                startTime: 0,
                SONG_START_DELAY,
                GlobalVariables.State.SongSpeed,
                Song.SongOffsetSeconds);

            // Spawn players
            CreatePlayers();

            // Set up the crowd stem so it can be restored after muting (if it exists)
            if (_stemStates.TryGetValue(SongStem.Crowd, out var state))
            {
                state.Total = 1;
                state.Audible = 1;
            }

            if (_loadState == LoadFailureState.Error)
            {
                ToastManager.ToastError(_loadFailureMessage);

                global.LoadScene(SceneIndex.Menu);
                return;
            }

            // Listen for menu inputs
            Navigator.Instance.NavigationEvent += OnNavigationEvent;

            // Debug info
            InitializeDebug();
#if UNITY_EDITOR
            SetDebugEnabled(true);
#endif

            // Initialize/destroy practice mode
            if (IsPractice)
            {
                PracticeManager.DisplayPracticeMenu();
            }
            else
            {
                Destroy(PracticeManager);
            }

            _failMeter.Initialize(EngineManager, this);

            if (SettingsManager.Settings.NoFailMode.Value || IsPractice)
            {
                _failMeter.SetActive(false);
            }

            // This is not an else because we still want to subscribe in case the user disables no fail during the song
            // We check in the callback to determine whether we should actually run the fail routine
            if (ReplayInfo == null || GlobalVariables.State.PlayingWithReplay)
            {
                EngineManager.OnSongFailed += OnSongFailed;

                EngineManager.InitializeHappiness();

                SettingsManager.Settings.NoFailMode.OnChange += OnNoFailModeChanged;
                SettingsManager.Settings.AutoCalibrateAudio.Value = false;
                SettingsManager.Settings.AutoCalibrateVideo.Value = false;
            }

            // Log constant values
            YargLogger.LogFormatDebug("Audio calibration: {0}, video calibration: {1}, song offset: {2}",
                _songRunner.AudioCalibration, _songRunner.VideoCalibration, _songRunner.SongOffset);

            // Loaded, enable updates
            enabled = true;
            IsSongStarted = true;
            _songStarted?.Invoke();
        }

        private bool LoadReplay()
        {
            var readOptions = new ReplayReadOptions { KeepFrameTimes = GlobalVariables.VerboseReplays };
            var (result, data) = ReplayIO.TryLoadData(ReplayInfo, readOptions);
            if (result != ReplayReadResult.Valid)
            {
                YargLogger.LogFormatError("Failed to load replay! Result: {0}", result);
                return false;
            }

            // Create YargPlayers from the replay frames
            var players = new YargPlayer[data.Frames.Length];
            for (int i = 0; i < data.Frames.Length; ++i)
            {
                players[i] = new YargPlayer(data.Frames[i], data);
            }

            ReplayData = data;
            YargPlayers = players;
            return true;
        }

        private void LoadChart()
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var chartStopwatch = System.Diagnostics.Stopwatch.StartNew();
                Chart = Song.LoadChart();
                chartStopwatch.Stop();
                YargLogger.LogFormatInfo("[LOADING] Song.LoadChart() took {0}ms", chartStopwatch.ElapsedMilliseconds);

                if (Chart != null)
                {
                    var venueStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    GenerateVenueTrack();
                    venueStopwatch.Stop();
                    LoadingTrace.LogIfSlow(venueStopwatch.ElapsedMilliseconds,
                        "[LOADING] GenerateVenueTrack() took {0}ms", venueStopwatch.ElapsedMilliseconds);

                    var lipsyncStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    GenerateLipsyncTrack();
                    lipsyncStopwatch.Stop();
                    LoadingTrace.LogIfSlow(lipsyncStopwatch.ElapsedMilliseconds,
                        "[LOADING] GenerateLipsyncTrack() took {0}ms", lipsyncStopwatch.ElapsedMilliseconds);
                }
                else
                {
                    _loadState = LoadFailureState.Rescan;
                }
            }
            catch (Exception ex)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load chart!";
                YargLogger.LogException(ex, "Failed to load chart!");
            }

            totalStopwatch.Stop();
            YargLogger.LogFormatInfo("[LOADING] LoadChart() total took {0}ms", totalStopwatch.ElapsedMilliseconds);
        }

        private void GenerateVenueTrack()
        {
            // If we have no venue events, attempt to load from milo
            if (Chart.VenueTrack.IsEmpty)
            {
                    SongChart.LoadVenueFromMilo(Chart, Song);

                    YargLogger.LogFormatDebug("Loaded {0} lighting events from milo", Chart.VenueTrack.Lighting.Count);
            }

            if (File.Exists(VenueAutoGenerationPreset.DefaultPath))
            {
                var preset = new VenueAutoGenerationPreset(VenueAutoGenerationPreset.DefaultPath);
                if (!preset.ChartHasFog(Chart)) // This is separate because we may want to add fog even if venue is authored
                {
                    Chart = preset.GenerateFogEvents(Chart);
                }

                if (Chart.VenueTrack.Lighting.Count == 0)
                {
                    Chart = preset.GenerateLightingEvents(Chart);
                }
            }
        }

        private void GenerateLipsyncTrack()
        {
            SongChart.LoadLipsyncFromMilo(Chart, Song);

            YargLogger.LogFormatDebug("Loaded {0} lipsync events from milo", Chart.LipsyncEvents.Count);
        }

        private void FinalizeChart()
        {
            double audioLength = _mixer.Length;
            double chartLength = Chart.GetEndTime();
            double endTime = Chart.GetEndEvent()?.Time ?? -1;

            // - Chart < Audio < [end] -> Audio
            // - Chart < [end] < Audio -> [end]
            // - [end] < Chart < Audio -> Audio
            // - Audio < Chart         -> Chart
            if (audioLength <= chartLength)
            {
                SongLength = chartLength;
            }
            else if (endTime <= chartLength || audioLength <= endTime)
            {
                SongLength = audioLength;
            }
            else
            {
                SongLength = endTime;
            }

            // Get the first and last note times for the chart
            FirstNoteTime = Chart.GetFirstNoteStartTime();
            LastNoteTime = Chart.GetLastNoteEndTime();

            // Make sure enough beatlines have been generated to cover the song end delay
            Chart.SyncTrack.GenerateBeatlines(SongLength + SONG_END_DELAY, true);

            BeatEventHandler = new BeatEventHandler(Chart.SyncTrack);
            CrowdEventHandler = new CrowdEventHandler(Chart, this);

            var chartLoadedStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _chartLoaded?.Invoke(Chart);
            chartLoadedStopwatch.Stop();
            LoadingTrace.LogIfSlow(chartLoadedStopwatch.ElapsedMilliseconds,
                "[LOADING] _chartLoaded subscribers took {0}ms", chartLoadedStopwatch.ElapsedMilliseconds);

            _songLoaded?.Invoke();
        }

        private void CreatePlayers()
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            long highScoreLookupMilliseconds = 0;
            long instantiateMilliseconds = 0;
            long trackViewMilliseconds = 0;
            long trackInitializeMilliseconds = 0;
            long vocalTrackInitializeMilliseconds = 0;
            long vocalsPlayerInitializeMilliseconds = 0;
            
            try
            {
                _players = new List<BasePlayer>();

                bool vocalTrackInitialized = false;

                int index = -1;
                int highwayIndex = -1;
                int vocalIndex = -1;
                foreach (var player in YargPlayers)
                {
                    player.IsScoreValid = true;

                    if (!player.IsReplay)
                    {
                        // Reset microphone (resets channel buffers)
                        // We probably wanna do this no matter what, so put it up here
                        player.Bindings.Microphone?.Reset();
                    }

                    // Skip if the player is sitting out
                    if (player.SittingOut)
                    {
                        continue;
                    }
                    index++;

                    if (!player.IsReplay)
                    {
                        // Don't do this if it's a replay, because the replay
                        // would've already set its own presets at this point
                        player.RefreshPresets();
                    }

                    var highScoreStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var lastHighScore = ScoreContainer.GetHighScore(Song.Hash, player.Profile.Id, player.Profile.CurrentInstrument, false)?.Score;
                    highScoreStopwatch.Stop();
                    highScoreLookupMilliseconds += highScoreStopwatch.ElapsedMilliseconds;
                    LoadingTrace.LogIfSlow(
                        highScoreStopwatch.ElapsedMilliseconds,
                        LOADING_OUTLIER_THRESHOLD_MS,
                        "[LOADING] High score lookup for {0} ({1}) took {2}ms",
                        player.Profile.Name,
                        player.Profile.CurrentInstrument,
                        highScoreStopwatch.ElapsedMilliseconds);
                    YargLogger.LogFormatInfo("Current high score for player {0} on {1}: {2}",
                        player.Profile.Name, player.Profile.CurrentInstrument, lastHighScore ?? 0);

                    if (player.Profile.GameMode != GameMode.Vocals)
                    {
                        highwayIndex++;
                        var prefab = GetTrackPrefab(player);

                        // Skip if there's no prefab for the game mode
                        if (prefab == null)
                        {
                            continue;
                        }

                        var playerStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var playerObject = CreateTrackPlayerObject(prefab, highwayIndex, out var reusedPreloadedObject);
                        playerStopwatch.Stop();
                        instantiateMilliseconds += playerStopwatch.ElapsedMilliseconds;
                        LoadingTrace.LogIfSlow(
                            playerStopwatch.ElapsedMilliseconds,
                            LOADING_OUTLIER_THRESHOLD_MS,
                            reusedPreloadedObject
                                ? "[LOADING] Activate preloaded highway prefab for {0} ({1}) took {2}ms"
                                : "[LOADING] Instantiate highway prefab for {0} ({1}) took {2}ms",
                            player.Profile.Name,
                            player.Profile.GameMode,
                            playerStopwatch.ElapsedMilliseconds);

                        // Setup player
                        var trackPlayer = playerObject.GetComponent<TrackPlayer>();
                        playerStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var trackView = _trackViewManager.CreateTrackView();
                        playerStopwatch.Stop();
                        trackViewMilliseconds += playerStopwatch.ElapsedMilliseconds;
                        LoadingTrace.LogIfSlow(
                            playerStopwatch.ElapsedMilliseconds,
                            LOADING_OUTLIER_THRESHOLD_MS,
                            "[LOADING] CreateTrackView for {0} took {1}ms",
                            player.Profile.Name,
                            playerStopwatch.ElapsedMilliseconds);
                        
                        playerStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        trackPlayer.Initialize(highwayIndex, player, Chart, trackView, _mixer, lastHighScore);
                        playerStopwatch.Stop();
                        trackInitializeMilliseconds += playerStopwatch.ElapsedMilliseconds;
                        LoadingTrace.LogIfSlow(
                            playerStopwatch.ElapsedMilliseconds,
                            LOADING_OUTLIER_THRESHOLD_MS,
                            "[LOADING] TrackPlayer.Initialize for {0} ({1}) took {2}ms",
                            player.Profile.Name,
                            player.Profile.GameMode,
                            playerStopwatch.ElapsedMilliseconds);

                        _players.Add(trackPlayer);
                        _trackViewManager.AddTrackPlayer(trackPlayer);
                    }
                    else
                    {
                        // Initialize the vocal track if it hasn't been already, and hide lyric bar
                        if (!vocalTrackInitialized)
                        {
                            var vocalStopwatch = System.Diagnostics.Stopwatch.StartNew();
                            VocalTrack.gameObject.SetActive(true);
                            _trackViewManager.CreateVocalTrackView();

                            // Since all players have to select the same vocals
                            // type (solo/harmony) this works no problem.
                            var chart = player.Profile.CurrentInstrument == Instrument.Vocals
                                ? Chart.Vocals
                                : Chart.Harmony;
                            VocalTrack.Initialize(chart, player, Song.VocalScrollSpeedScalingFactor);

                            _lyricBar.gameObject.SetActive(false);
                            vocalTrackInitialized = true;
                            vocalStopwatch.Stop();
                            vocalTrackInitializeMilliseconds += vocalStopwatch.ElapsedMilliseconds;
                            LoadingTrace.LogIfSlow(vocalStopwatch.ElapsedMilliseconds,
                                "[LOADING] VocalTrack.Initialize took {0}ms", vocalStopwatch.ElapsedMilliseconds);
                        }

                        // Create the player on the vocal track

                        var vocalsPlayer = VocalTrack.CreatePlayer();
                        vocalIndex++;
                        var playerHud = _trackViewManager.CreateVocalsPlayerHUD();

                        var percussionTrack = VocalTrack.CreatePercussionTrack();
                        percussionTrack.TrackSpeed = VocalTrack.TrackSpeed;
                        
                        var playerStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        vocalsPlayer.Initialize(index, vocalIndex, player, Chart, playerHud, percussionTrack, lastHighScore, VocalTrack.TrackSpeed);
                        playerStopwatch.Stop();
                        vocalsPlayerInitializeMilliseconds += playerStopwatch.ElapsedMilliseconds;
                        LoadingTrace.LogIfSlow(
                            playerStopwatch.ElapsedMilliseconds,
                            LOADING_OUTLIER_THRESHOLD_MS,
                            "[LOADING] VocalsPlayer.Initialize for {0} took {1}ms",
                            player.Profile.Name,
                            playerStopwatch.ElapsedMilliseconds);

                        _players.Add(vocalsPlayer);
                    }

                    // Add (or increase total of) the stem state
                    var stem = player.Profile.CurrentInstrument.ToSongStem();
                    if (stem == SongStem.Bass && !_stemStates.ContainsKey(SongStem.Bass))
                    {
                        stem = SongStem.Rhythm;
                    }

                    if (stem != _backgroundStem && _stemStates.TryGetValue(stem, out var state))
                    {
                        ++state.Total;
                        ++state.Audible;
                    }
                    else if (_stemStates.TryGetValue(_backgroundStem, out state))
                    {
                        // Ensures the stem will still play at a minimum of 50%, even if all players mute
                        state.Total += 2;
                        state.Audible += 2;
                    }
                }
            }
            catch (Exception ex)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load song!";
                YargLogger.LogException(ex, "Failed to load song!");
            }
            finally
            {
                ClearPreloadedTrackObjects();
            }
            
            totalStopwatch.Stop();
            YargLogger.LogFormatInfo(
                "[LOADING] CreatePlayers() total took {0}ms for {1} player(s): instantiate={2}ms, trackViews={3}ms, trackInit={4}ms, vocalTrack={5}ms, vocalsPlayer={6}ms, highScores={7}ms",
                totalStopwatch.ElapsedMilliseconds,
                _players?.Count ?? 0,
                instantiateMilliseconds,
                trackViewMilliseconds,
                trackInitializeMilliseconds,
                vocalTrackInitializeMilliseconds,
                vocalsPlayerInitializeMilliseconds,
                highScoreLookupMilliseconds);
        }

        private void PreloadTrackPlayers()
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            ClearPreloadedTrackObjects();

            var preloadedCount = 0;
            foreach (var player in YargPlayers)
            {
                if (player.SittingOut || player.Profile.GameMode == GameMode.Vocals)
                {
                    continue;
                }

                var prefab = GetTrackPrefab(player);
                if (prefab == null)
                {
                    continue;
                }

                var trackObject = Instantiate(prefab, GetTrackSpawnPosition(0), prefab.transform.rotation);
                trackObject.SetActive(false);
                var trackPlayer = trackObject.GetComponent<TrackPlayer>();
                if (trackPlayer != null)
                {
                    trackPlayer.PrewarmForLoad(player);
                }

                _preloadedTrackObjects.Enqueue(new PreloadedTrackObject(prefab, trackObject));
                preloadedCount++;
            }

            totalStopwatch.Stop();
            LoadingTrace.LogIfSlow(
                totalStopwatch.ElapsedMilliseconds,
                "[LOADING] PreloadTrackPlayers() took {0}ms for {1} highway(s)",
                totalStopwatch.ElapsedMilliseconds,
                preloadedCount);
        }

        private GameObject GetTrackPrefab(YargPlayer player)
        {
            return player.Profile.GameMode switch
            {
                GameMode.FiveFretGuitar => _fiveFretGuitarPrefab,
                GameMode.SixFretGuitar  => _sixFretGuitarPrefab,
                GameMode.FourLaneDrums  => _fourLaneDrumsPrefab,
                GameMode.FiveLaneDrums  => _fiveLaneDrumsPrefab,
                GameMode.EliteDrums     => Song.HasInstrument(Instrument.FiveLaneDrums) ? _fiveLaneDrumsPrefab : _fourLaneDrumsPrefab,
                GameMode.ProKeys        => player.Profile.CurrentInstrument is Instrument.ProKeys ? _proKeysPrefab : _fiveLaneKeysPrefab,
                GameMode.ProGuitar      => _proGuitarPrefab,
                _                       => null
            };
        }

        private GameObject CreateTrackPlayerObject(GameObject prefab, int highwayIndex, out bool reusedPreloadedObject)
        {
            if (_preloadedTrackObjects.Count > 0)
            {
                var preloadedTrackObject = _preloadedTrackObjects.Dequeue();
                if (preloadedTrackObject.Prefab == prefab && preloadedTrackObject.Instance != null)
                {
                    reusedPreloadedObject = true;

                    var trackTransform = preloadedTrackObject.Instance.transform;
                    trackTransform.SetPositionAndRotation(GetTrackSpawnPosition(highwayIndex), prefab.transform.rotation);
                    preloadedTrackObject.Instance.SetActive(true);
                    return preloadedTrackObject.Instance;
                }

                if (preloadedTrackObject.Instance != null)
                {
                    Destroy(preloadedTrackObject.Instance);
                }

                var discardedPrefabName = preloadedTrackObject.Prefab != null ? preloadedTrackObject.Prefab.name : "null";
                YargLogger.LogWarning($"Discarded preloaded highway prefab {discardedPrefabName} while requesting {prefab.name}");
            }

            reusedPreloadedObject = false;
            return Instantiate(prefab, GetTrackSpawnPosition(highwayIndex), prefab.transform.rotation);
        }

        private static Vector3 GetTrackSpawnPosition(int highwayIndex)
        {
            return new Vector3(highwayIndex * TRACK_SPACING_X, TRACK_SPAWN_HEIGHT, 0f);
        }

        private void ClearPreloadedTrackObjects()
        {
            while (_preloadedTrackObjects.Count > 0)
            {
                var preloadedTrackObject = _preloadedTrackObjects.Dequeue();
                if (preloadedTrackObject.Instance != null)
                {
                    Destroy(preloadedTrackObject.Instance);
                }
            }
        }
    }
}
