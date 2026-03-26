using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using YARG.Audio.BASS;
using YARG.Core.Logging;
using YARG.Core.Audio;
using YARG.Helpers;
using YARG.Input;
using YARG.Integration;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Player;
using YARG.Playlists;
using YARG.Replays;
using YARG.Scores;
using YARG.Settings;
using YARG.Settings.Customization;

namespace YARG
{
    public enum SceneIndex
    {
        Persistent,
        Menu,
        Gameplay,
        Calibration,
        Score
    }

    [DefaultExecutionOrder(-5000)]
    public class GlobalVariables : MonoSingleton<GlobalVariables>
    {
        private const int CLEANUP_INTERVAL_EXITS = 3;
        private const long CLEANUP_MEMORY_THRESHOLD_BYTES = 768L * 1024L * 1024L;

        public List<YargPlayer> Players { get; private set; }

        public static bool OfflineMode    { get; private set; }
        public static bool VerboseReplays { get; private set; }

        public static string PersistentDataPathOverride { get; private set; }

        public static PersistentState State = PersistentState.Default;

        public SceneIndex CurrentScene { get; private set; } = SceneIndex.Persistent;

        public string CurrentVersion { get; private set; } = "v0.14";

        private int _gameplayExitsSinceCleanup;

        protected override void SingletonAwake()
        {
            CurrentVersion = LoadVersion();
            YargLogger.LogFormatInfo("YARG {0}", CurrentVersion);

            // Command line arguments

            if (CommandLineArgs.Offline)
            {
                OfflineMode = true;
                YargLogger.LogInfo("Playing in offline mode");
            }

            if (CommandLineArgs.VerboseReplays)
            {
                VerboseReplays = true;
                YargLogger.LogInfo("Verbose replays enabled");
            }

            if (!string.IsNullOrEmpty(CommandLineArgs.DownloadLocation))
            {
                PathHelper.SetPathsFromDownloadLocation(CommandLineArgs.DownloadLocation);
            }

            // TODO: Actually respect the PersistentDataPath arg

            // Initialize important classes

            ReplayContainer.Init();
            ScoreContainer.Init();
            PlaylistContainer.Initialize();
            CustomContentManager.Initialize();
            LocalizationManager.Initialize(CommandLineArgs.Language);

            int profileCount = PlayerContainer.LoadProfiles();
            YargLogger.LogFormatInfo("Loaded {0} profiles", profileCount);

            int savedCount = PlayerContainer.SaveProfiles(false);
            YargLogger.LogFormatInfo("Saved {0} profiles", savedCount);

            GlobalAudioHandler.Initialize<BassAudioManager>();

            Players = new List<YargPlayer>();

            // Set alpha fading (on the tracks) to on
            // (this is mostly for the editor, but just in case)
            Shader.SetGlobalFloat("_IsFading", 1f);
        }

        private void Start()
        {
            SettingsManager.LoadSettings();
            InputManager.Initialize();

            LoadScene(SceneIndex.Menu);
        }

#if UNITY_EDITOR

        // For respecting the editor's mute button
        private bool _previousMute;

        private void Update()
        {
            bool muted = UnityEditor.EditorUtility.audioMasterMute;
            if (muted != _previousMute)
            {
                GlobalAudioHandler.SetMasterVolume(muted ? 0 : SettingsManager.Settings.MasterMusicVolume.Value);
                _previousMute = muted;
            }
        }

#endif

        protected override void SingletonDestroy()
        {
            SettingsManager.SaveSettings();
            PlayerContainer.SaveProfiles();
            PlaylistContainer.SaveAll();
            CustomContentManager.SaveAll();

            ReplayContainer.Destroy();
            ScoreContainer.Destroy();
            InputManager.Destroy();
            PlayerContainer.Destroy();
            GlobalAudioHandler.Close();

#if UNITY_EDITOR
            // Set alpha fading (on the tracks) to off
            Shader.SetGlobalFloat("_IsFading", 0f);
#endif
        }

        private async void LoadSceneAdditive(SceneIndex scene, SceneIndex previousScene)
        {
            var stopwatch = Stopwatch.StartNew();
            bool isRestarting = previousScene == SceneIndex.Gameplay && scene == SceneIndex.Gameplay;
            YargLogger.LogFormatInfo("[SCENE] LoadSceneAdditive started for scene {0} (from {1}, restarting: {2})", scene, previousScene, isRestarting);
            CurrentScene = scene;

            GameStateFetcher.SetSceneIndex(scene);

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);

            // Only cleanup assets when unloading Gameplay to menu/score.
            // Manual gameplay restarts intentionally skip cleanup to keep restart latency low.
            bool needsCleanup = previousScene == SceneIndex.Gameplay && scene != SceneIndex.Gameplay;
            if (isRestarting && State.CurrentSong != null)
            {
                // For gameplay->gameplay, check if song is actually changing
                // We need to store the old song hash before State gets updated
                // Unfortunately by this point State.CurrentSong is already the new song
                // So we'll use a different approach: check if it's a restart vs skip
                // For now, we'll skip cleanup only on manual restart (not setlist skip)
                // Setlist skip will go through the normal cleanup path
                YargLogger.LogInfo("[SCENE] Skipping cleanup for gameplay restart");
                LogGCTracking("After skip", gc0, gc1, gc2);
            }
            else if (needsCleanup && ShouldRunSceneCleanup(out var cleanupReason, out var allocatedMemoryBytes))
            {
                YargLogger.LogFormatInfo(
                    "[SCENE] Unloading unused assets after Gameplay ({0}, allocated memory: {1} MB)...",
                    cleanupReason,
                    BytesToMegabytes(allocatedMemoryBytes));
                await Resources.UnloadUnusedAssets();
                stopwatch.Stop();
                LogGCTracking("UnloadUnusedAssets", gc0, gc1, gc2);
                YargLogger.LogFormatInfo("[SCENE] UnloadUnusedAssets took {0}ms", stopwatch.ElapsedMilliseconds);
                _gameplayExitsSinceCleanup = 0;
            }
            else
            {
                LogSkippedCleanup(needsCleanup);
                LogGCTracking("After skip", gc0, gc1, gc2);
            }

            stopwatch.Restart();
            YargLogger.LogInfo("[SCENE] Loading scene async...");
            await SceneManager.LoadSceneAsync((int) scene, LoadSceneMode.Additive);
            stopwatch.Stop();
            LogGCTracking("LoadSceneAsync", gc0, gc1, gc2);
            YargLogger.LogFormatInfo("[SCENE] SceneManager.LoadSceneAsync took {0}ms", stopwatch.ElapsedMilliseconds);
            stopwatch.Restart();

            // When complete, set the newly loaded scene to the active one
            YargLogger.LogInfo("[SCENE] Setting active scene...");
            SceneManager.SetActiveScene(SceneManager.GetSceneByBuildIndex((int) scene));
            Navigator.Instance.DisableMenuInputs = false;
            stopwatch.Stop();
            LogGCTracking("SetActiveScene", gc0, gc1, gc2);
            YargLogger.LogFormatInfo("[SCENE] SetActiveScene + EnableMenuInputs took {0}ms", stopwatch.ElapsedMilliseconds);

            YargLogger.LogFormatInfo("[SCENE] LoadSceneAdditive completed for scene {0}", scene);
        }

        private void LogGCTracking(string operation, int gc0, int gc1, int gc2)
        {
            int newGc0 = GC.CollectionCount(0);
            int newGc1 = GC.CollectionCount(1);
            int newGc2 = GC.CollectionCount(2);

            if (newGc0 > gc0 || newGc1 > gc1 || newGc2 > gc2)
            {
                YargLogger.LogFormatInfo("[GC] {0}: Gen0={1} (+{2}), Gen1={3} (+{4}), Gen2={5} (+{6})",
                    operation,
                    newGc0, newGc0 - gc0,
                    newGc1, newGc1 - gc1,
                    newGc2, newGc2 - gc2);
            }
        }

        private bool ShouldRunSceneCleanup(out string reason, out long allocatedMemoryBytes)
        {
            allocatedMemoryBytes = Profiler.GetTotalAllocatedMemoryLong();
            if (allocatedMemoryBytes >= CLEANUP_MEMORY_THRESHOLD_BYTES)
            {
                reason = "memory threshold exceeded";
                return true;
            }

            _gameplayExitsSinceCleanup++;
            if (_gameplayExitsSinceCleanup >= CLEANUP_INTERVAL_EXITS)
            {
                reason = "cleanup interval reached";
                return true;
            }

            reason = "cleanup deferred";
            return false;
        }

        private void LogSkippedCleanup(bool needsCleanup)
        {
            if (!needsCleanup)
            {
                YargLogger.LogInfo("[SCENE] Skipping cleanup (not unloading Gameplay)");
                return;
            }

            long allocatedMemoryBytes = Profiler.GetTotalAllocatedMemoryLong();
            YargLogger.LogFormatInfo(
                "[SCENE] Deferring cleanup after Gameplay (allocated memory: {0} MB, exits since cleanup: {1}/{2})",
                BytesToMegabytes(allocatedMemoryBytes),
                _gameplayExitsSinceCleanup,
                CLEANUP_INTERVAL_EXITS);
        }

        private static long BytesToMegabytes(long bytes)
        {
            const long BYTES_PER_MEGABYTE = 1024L * 1024L;
            return bytes / BYTES_PER_MEGABYTE;
        }

        public void LoadScene(SceneIndex scene)
        {
            var stopwatch = Stopwatch.StartNew();
            var callTime = DateTime.Now;
            YargLogger.LogFormatInfo("[SCENE] LoadScene called with scene {0}, current scene is {1} at {2:O}", scene, CurrentScene, callTime);
            Navigator.Instance.DisableMenuInputs = true;
            stopwatch.Stop();
            YargLogger.LogFormatInfo("[SCENE] DisableMenuInputs took {0}ms", stopwatch.ElapsedMilliseconds);
            stopwatch.Restart();

            // Unload the current scene and load in the new one, or just load in the new one
            if (CurrentScene != SceneIndex.Persistent)
            {
                YargLogger.LogFormatInfo("[SCENE] Unloading current scene {0}...", CurrentScene);
                // Unload the current scene
                var asyncOp = SceneManager.UnloadSceneAsync((int) CurrentScene);

                // The load the new scene
                asyncOp.completed += _ => {
                    stopwatch.Stop();
                    YargLogger.LogFormatInfo("[SCENE] Scene unload completed, took {0}ms (total since LoadScene call: {1}ms)", stopwatch.ElapsedMilliseconds, stopwatch.ElapsedMilliseconds);
                    LoadSceneAdditive(scene, CurrentScene);
                };
            }
            else
            {
                YargLogger.LogInfo("[SCENE] Current scene is Persistent, skipping unload");
                LoadSceneAdditive(scene, CurrentScene);
            }
        }

        // Due to the preprocessor, it doesn't know that an instance variable is being used
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private string LoadVersion()
        {
#if UNITY_EDITOR
            return LoadVersionFromGit();
#elif YARG_TEST_BUILD || YARG_NIGHTLY_BUILD
            var versionFile = Resources.Load<TextAsset>("version");
            if (versionFile != null)
            {
                return versionFile.text;
            }
            else
            {
                return CurrentVersion;
            }
#else
            return CurrentVersion;
#endif
        }

        public static string LoadVersionFromGit()
        {
            var process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            // Branch
            process.StartInfo.Arguments = "rev-parse --abbrev-ref HEAD";
            process.Start();
            string branch = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            // Commit Count
            process.StartInfo.Arguments = "rev-list --count HEAD";
            process.Start();
            string commitCount = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            // Commit
            process.StartInfo.Arguments = "rev-parse --short HEAD";
            process.Start();
            string commit = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

#if YARG_NIGHTLY_BUILD
            return $"b{commitCount} ({commit})";
#else
            return $"{branch} b{commitCount} ({commit})";
#endif
        }
    }
}
