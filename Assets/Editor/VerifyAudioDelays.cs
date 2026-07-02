using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using ManagedBass;
using YARG.Audio.BASS;
using YARG.Core.Audio;
using YARG.Helpers;
using YARG.Settings;
using YARG.Settings.Types;
using YARG.Playback;

namespace Editor
{
    public static class VerifyAudioDelays
    {
        [MenuItem("Tests/Verify Audio Delays")]
        public static void RunVerification()
        {
            Debug.Log("Starting Audio Delays Verification Test...");

            // 0. Initialize PathHelper paths if not set
            if (string.IsNullOrEmpty(PathHelper.PersistentDataPath))
            {
                var pathHelperInit = typeof(PathHelper).GetMethod("Init", BindingFlags.Static | BindingFlags.NonPublic);
                if (pathHelperInit != null)
                {
                    pathHelperInit.Invoke(null, null);
                }
            }

            // 1. Initialize BASS Audio Manager
            GlobalAudioHandler.Initialize<BassAudioManager>();

            // 2. Backup and set up temporary settings
            var originalSettings = SettingsManager.Settings;

            var settingsProp = typeof(SettingsManager).GetProperty("Settings", BindingFlags.Static | BindingFlags.Public);
            var settingsSetter = settingsProp?.GetSetMethod(nonPublic: true);
            if (settingsSetter == null)
            {
                Debug.LogError("Could not find setter for SettingsManager.Settings");
                return;
            }

            // Create clean temporary settings for the test
            settingsSetter.Invoke(null, new object[] { new SettingsManager.SettingContainer() });

            // Get the active audio manager instance
            var instanceField = typeof(GlobalAudioHandler).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
            var audioManager = instanceField?.GetValue(null) as BassAudioManager;
            if (audioManager == null)
            {
                Debug.LogError("Failed to get active BassAudioManager instance!");
                // Restore before exiting
                settingsSetter.Invoke(null, new object[] { originalSettings });
                return;
            }

            try
            {
                // Set fixed settings for consistent test conditions
                SetSettingValue(SettingsManager.Settings.PlaybackBufferLength, 150); // 150ms buffer

                // --- TEST 1: Single Playback Mixer Mode ---
                Debug.Log("Testing Single Playback Mixer Mode...");
                SetSettingValue(SettingsManager.Settings.UseSingleBassPlaybackMixer, true);

                VerifyMixerDelays(audioManager, true, 150);

                // --- TEST 2: Non-Single Playback Mixer Mode ---
                Debug.Log("Testing Non-Single Playback Mixer Mode...");
                SetSettingValue(SettingsManager.Settings.UseSingleBassPlaybackMixer, false);

                VerifyMixerDelays(audioManager, false, 150);

                Debug.Log("SUCCESS: All audio delay verification checks passed!");
                EditorUtility.DisplayDialog("Test Passed", "All audio delay verification checks passed successfully!", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Test Failed: {ex.Message}\n{ex.StackTrace}");
                EditorUtility.DisplayDialog("Test Failed", $"Audio delay verification failed:\n{ex.Message}", "OK");
            }
            finally
            {
                // Restore original settings
                Debug.Log("Restoring original settings...");
                settingsSetter.Invoke(null, new object[] { originalSettings });
                Debug.Log("Restore complete.");
            }
        }

        private static void VerifyMixerDelays(BassAudioManager audioManager, bool expectedSingleMixer, int testBufferMs)
        {
            // Get internal property: UsesSinglePlaybackMixer
            var usesSingleProp = typeof(BassAudioManager).GetProperty("UsesSinglePlaybackMixer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (usesSingleProp == null)
            {
                throw new Exception("Could not find internal property UsesSinglePlaybackMixer on BassAudioManager");
            }

            bool usesSingleMixer = (bool) usesSingleProp.GetValue(audioManager);
            if (usesSingleMixer != expectedSingleMixer)
            {
                throw new Exception($"UsesSinglePlaybackMixer was expected to be {expectedSingleMixer}, but was {usesSingleMixer}");
            }

            // Create a temporary mixer
            var createMixerMethod = typeof(BassAudioManager).GetMethod("CreateMixer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (createMixerMethod == null)
            {
                throw new Exception("Could not find internal method CreateMixer on BassAudioManager");
            }

            var mixer = createMixerMethod.Invoke(audioManager, new object[] { "TestMixer", 1.0f, 1.0, false, false }) as BassStemMixer;
            if (mixer == null)
            {
                throw new Exception("Failed to create temporary BassStemMixer");
            }

            try
            {
                // Retrieve the configured latencies
                double deviceLatency = GlobalAudioHandler.PlaybackLatency / 1000.0;
                int minBufferLength = GlobalAudioHandler.MinimumBufferLength;
                int effectiveBufferLength = testBufferMs > 0 && minBufferLength > 0 && testBufferMs < minBufferLength ? minBufferLength : testBufferMs;
                double configuredLatency = Math.Max(0, effectiveBufferLength) / 1000.0;

                double audibleSyncLatency = mixer.GetAudibleSyncLatency();
                double commandLatency = mixer.GetCommandLatency();
                double startLatency = mixer.GetStartLatency();

                // Get SongRunner resume methods using reflection
                var getResumeStartLatencyMethod = typeof(SongRunner).GetMethod("GetResumeStartLatency", BindingFlags.Static | BindingFlags.NonPublic);
                var getResumeSeekLatencyMethod = typeof(SongRunner).GetMethod("GetResumeSeekLatency", BindingFlags.Static | BindingFlags.NonPublic);
                if (getResumeStartLatencyMethod == null || getResumeSeekLatencyMethod == null)
                {
                    throw new Exception("Could not find SongRunner.GetResumeStartLatency or GetResumeSeekLatency methods");
                }

                double resumeStartLatency = (double) getResumeStartLatencyMethod.Invoke(null, new object[] { audibleSyncLatency, startLatency });
                double resumeSeekLatency = (double) getResumeSeekLatencyMethod.Invoke(null, new object[] { audibleSyncLatency, startLatency });

                Debug.Log($"Calculated parameters: Device Latency: {deviceLatency*1000:0.0}ms, Configured Buffer Latency: {configuredLatency*1000:0.0}ms");
                Debug.Log($"Mixer reported latencies: AudibleSync: {audibleSyncLatency*1000:0.0}ms, Command: {commandLatency*1000:0.0}ms, Start: {startLatency*1000:0.0}ms");
                Debug.Log($"SongRunner resume latencies: Start: {resumeStartLatency*1000:0.0}ms, Seek: {resumeSeekLatency*1000:0.0}ms");

                const double EPSILON = 0.0001; // Tiny tolerance for floating point calculations

                if (expectedSingleMixer)
                {
                    // Under Single Playback Mixer Mode:
                    // 1. AudibleSyncLatency must be: configured buffer + device latency
                    double expectedAudible = configuredLatency + deviceLatency;
                    if (Math.Abs(audibleSyncLatency - expectedAudible) > EPSILON)
                    {
                        throw new Exception($"Single Mixer: AudibleSyncLatency did not match! Actual: {audibleSyncLatency * 1000:0.0}ms ({audibleSyncLatency}s), Expected: {expectedAudible * 1000:0.0}ms ({expectedAudible}s)");
                    }

                    // 2. CommandLatency must be: configured buffer + device latency
                    double expectedCommand = configuredLatency + deviceLatency;
                    if (Math.Abs(commandLatency - expectedCommand) > EPSILON)
                    {
                        throw new Exception($"Single Mixer: CommandLatency did not match! Actual: {commandLatency * 1000:0.0}ms ({commandLatency}s), Expected: {expectedCommand * 1000:0.0}ms ({expectedCommand}s)");
                    }

                    // 3. StartLatency must be: device latency
                    double expectedStart = GetExpectedStartLatency(expectedSingleMixer, deviceLatency);
                    if (Math.Abs(startLatency - expectedStart) > EPSILON)
                    {
                        throw new Exception($"Single Mixer: StartLatency did not match! Actual: {startLatency * 1000:0.0}ms ({startLatency}s), Expected: {expectedStart * 1000:0.0}ms ({expectedStart}s)");
                    }

                    // 4. Resume latency (start and seek) must be: configured buffer + device latency
                    double expectedResume = configuredLatency + deviceLatency;
                    if (Math.Abs(resumeStartLatency - expectedResume) > EPSILON)
                    {
                        throw new Exception($"Single Mixer: resumeStartLatency did not match! Actual: {resumeStartLatency * 1000:0.0}ms ({resumeStartLatency}s), Expected: {expectedResume * 1000:0.0}ms ({expectedResume}s)");
                    }
                    if (Math.Abs(resumeSeekLatency - expectedResume) > EPSILON)
                    {
                        throw new Exception($"Single Mixer: resumeSeekLatency did not match! Actual: {resumeSeekLatency * 1000:0.0}ms ({resumeSeekLatency}s), Expected: {expectedResume * 1000:0.0}ms ({expectedResume}s)");
                    }
                }
                else
                {
                    // Under Non-Single Playback Mixer Mode:
                    // 1. AudibleSyncLatency must be 0
                    if (Math.Abs(audibleSyncLatency) > EPSILON)
                    {
                        throw new Exception($"Non-Single Mixer: AudibleSyncLatency did not match! Actual: {audibleSyncLatency * 1000:0.0}ms ({audibleSyncLatency}s), Expected: 0.0ms (0s)");
                    }

                    // 2. CommandLatency must still be: configured buffer + device latency (stem operations are delayed)
                    double expectedCommand = configuredLatency + deviceLatency;
                    if (Math.Abs(commandLatency - expectedCommand) > EPSILON)
                    {
                        throw new Exception($"Non-Single Mixer: CommandLatency did not match! Actual: {commandLatency * 1000:0.0}ms ({commandLatency}s), Expected: {expectedCommand * 1000:0.0}ms ({expectedCommand}s)");
                    }

                    // 3. StartLatency must be: device latency (+ BASS device buffer on Windows direct channels)
                    double expectedStart = GetExpectedStartLatency(expectedSingleMixer, deviceLatency);
                    if (Math.Abs(startLatency - expectedStart) > EPSILON)
                    {
                        throw new Exception($"Non-Single Mixer: StartLatency did not match! Actual: {startLatency * 1000:0.0}ms ({startLatency}s), Expected: {expectedStart * 1000:0.0}ms ({expectedStart}s)");
                    }

                    // 4. Resume latency (start and seek) falls back to StartLatency when AudibleSyncLatency is 0
                    if (Math.Abs(resumeStartLatency - expectedStart) > EPSILON)
                    {
                        throw new Exception($"Non-Single Mixer: resumeStartLatency did not match! Actual: {resumeStartLatency * 1000:0.0}ms ({resumeStartLatency}s), Expected: {expectedStart * 1000:0.0}ms ({expectedStart}s)");
                    }
                    if (Math.Abs(resumeSeekLatency - expectedStart) > EPSILON)
                    {
                        throw new Exception($"Non-Single Mixer: resumeSeekLatency did not match! Actual: {resumeSeekLatency * 1000:0.0}ms ({resumeSeekLatency}s), Expected: {expectedStart * 1000:0.0}ms ({expectedStart}s)");
                    }
                }
            }
            finally
            {
                mixer.Dispose();
            }
        }

        private static double GetExpectedStartLatency(bool expectedSingleMixer, double deviceLatency)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!expectedSingleMixer)
            {
                return deviceLatency + Math.Max(0, Bass.DeviceBufferLength) / 1000.0;
            }
#endif

            return deviceLatency;
        }

        private static void SetSettingValue<T>(AbstractSetting<T> setting, T value)
        {
            var field = typeof(AbstractSetting<T>).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new Exception($"Could not find backing field _value on AbstractSetting<{typeof(T).Name}>");
            }
            field.SetValue(setting, value);
        }

        [MenuItem("Tests/Measure Real Seek Latency")]
        public static async void RunRealSeekLatencyMeasurement()
        {
            Debug.Log("Starting Real Seek Latency Measurement...");

            // 0. Initialize PathHelper paths if not set
            if (string.IsNullOrEmpty(PathHelper.PersistentDataPath))
            {
                var pathHelperInit = typeof(PathHelper).GetMethod("Init", BindingFlags.Static | BindingFlags.NonPublic);
                if (pathHelperInit != null)
                {
                    pathHelperInit.Invoke(null, null);
                }
            }

            // 1. Initialize BASS Audio Manager
            GlobalAudioHandler.Initialize<BassAudioManager>();

            // 2. Backup and set up temporary settings
            var originalSettings = SettingsManager.Settings;

            var settingsProp = typeof(SettingsManager).GetProperty("Settings", BindingFlags.Static | BindingFlags.Public);
            var settingsSetter = settingsProp?.GetSetMethod(nonPublic: true);
            if (settingsSetter == null)
            {
                Debug.LogError("Could not find setter for SettingsManager.Settings");
                return;
            }

            settingsSetter.Invoke(null, new object[] { new SettingsManager.SettingContainer() });

            var instanceField = typeof(GlobalAudioHandler).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
            var audioManager = instanceField?.GetValue(null) as BassAudioManager;
            if (audioManager == null)
            {
                Debug.LogError("Failed to get active BassAudioManager instance!");
                settingsSetter.Invoke(null, new object[] { originalSettings });
                return;
            }

            try
            {
                SetSettingValue(SettingsManager.Settings.PlaybackBufferLength, 150); // 150ms buffer

                // --- TEST 1: Single Playback Mixer Mode ---
                Debug.Log("Testing Single Playback Mixer Mode...");
                SetSettingValue(SettingsManager.Settings.UseSingleBassPlaybackMixer, true);
                GlobalAudioHandler.SetBufferLength(150);
                await MeasureMixerSeekLatency(audioManager, true);

                // --- TEST 2: Non-Single Playback Mixer Mode ---
                Debug.Log("Testing Non-Single Playback Mixer Mode...");
                SetSettingValue(SettingsManager.Settings.UseSingleBassPlaybackMixer, false);
                GlobalAudioHandler.SetBufferLength(150);
                await MeasureMixerSeekLatency(audioManager, false);

                Debug.Log("Real Seek Latency Measurement complete!");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Measurement Failed: {ex.Message}\n{ex.StackTrace}");
                EditorUtility.DisplayDialog("Measurement Failed", $"Error:\n{ex.Message}", "OK");
            }
            finally
            {
                // Restore original settings and buffer
                Debug.Log("Restoring original settings and buffer...");
                settingsSetter.Invoke(null, new object[] { originalSettings });

                if (originalSettings != null)
                {
                    GlobalAudioHandler.SetBufferLength(originalSettings.PlaybackBufferLength.Value);
                }
                Debug.Log("Restore complete.");
            }
        }

        private static async Task MeasureMixerSeekLatency(BassAudioManager audioManager, bool expectedSingleMixer)
        {
            var createMixerMethod = typeof(BassAudioManager).GetMethod("CreateMixer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (createMixerMethod == null)
            {
                throw new Exception("Could not find internal method CreateMixer on BassAudioManager");
            }

            var mixer = createMixerMethod.Invoke(audioManager, new object[] { "TestMixer", 1.0f, 1.0, false, false }) as BassStemMixer;
            if (mixer == null)
            {
                throw new Exception("Failed to create temporary BassStemMixer");
            }

            FileStream fileStream = null;
            try
            {
                // Open sine_hi.ogg
                string path = Path.Combine(Application.streamingAssetsPath, "metronome", "sine_hi.ogg");
                if (!File.Exists(path))
                {
                    throw new Exception($"Audio file not found: {path}");
                }

                fileStream = File.OpenRead(path);
                if (!mixer.AddChannel(fileStream, SongStem.Song))
                {
                    throw new Exception("Failed to add channel to mixer");
                }

                // Get _tempoStream and its Handle
                var tempoStreamField = typeof(BassStemMixer).GetField("_tempoStream", BindingFlags.Instance | BindingFlags.NonPublic);
                var tempoStream = tempoStreamField?.GetValue(mixer) as BassTempoStream;
                if (tempoStream == null || tempoStream.Handle == 0)
                {
                    throw new Exception("Failed to retrieve valid tempo stream handle");
                }

                int tempoStreamHandle = tempoStream.Handle;

                // Start playback
                mixer.Play();

                // Wait 500ms to stabilize
                await Task.Delay(500);

                long totalBytes = Bass.ChannelGetLength(tempoStreamHandle);
                double fileLength = Bass.ChannelBytes2Seconds(tempoStreamHandle, totalBytes);
                Debug.Log($"Loaded sine_hi.ogg, length: {fileLength:0.000}s ({totalBytes} bytes)");

                // We seek to 0.0 seconds, and measure how long it takes to reach syncTarget seconds audibly.
                double seekTarget = 0.0;
                double syncTarget = Math.Min(0.05, fileLength * 0.5); // 50ms or half the file length
                double playbackDuration = syncTarget - seekTarget;

                long syncTargetBytes = Bass.ChannelSeconds2Bytes(tempoStreamHandle, syncTarget);

                var tcs = new TaskCompletionSource<long>();
                var stopwatch = new System.Diagnostics.Stopwatch();

                SyncProcedure syncCallback = (handle, channel, data, user) =>
                {
                    stopwatch.Stop();
                    tcs.TrySetResult(stopwatch.ElapsedMilliseconds);
                };

                int syncHandle = Bass.ChannelSetSync(
                    tempoStreamHandle,
                    SyncFlags.Position | SyncFlags.Onetime,
                    syncTargetBytes,
                    syncCallback,
                    IntPtr.Zero
                );

                if (syncHandle == 0)
                {
                    throw new Exception($"Failed to set BASS position sync: {Bass.LastError}");
                }

                // Call seek and start stopwatch
                stopwatch.Start();
                mixer.SetPosition(seekTarget);

                var timeoutTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == tcs.Task)
                {
                    long totalElapsedMs = await tcs.Task;
                    long actualSeekLatencyMs = totalElapsedMs - (long)(playbackDuration * 1000);
                    
                    // Retrieve reported values for comparison
                    var info = Bass.Info;
                    int infoLatency = info.Latency;
                    int deviceBufferLength = Bass.DeviceBufferLength;
                    int devPeriod = Bass.GetConfig(Configuration.DevicePeriod);
                    int minBufferLength = info.MinBufferLength;
                    double deviceLatency = GlobalAudioHandler.PlaybackLatency;
                    double audibleSync = mixer.GetAudibleSyncLatency() * 1000.0;
                    double start = mixer.GetStartLatency() * 1000.0;
                    double expectedSongRunnerSeek = audibleSync > 0
                        ? audibleSync
                        : GetExpectedStartLatency(expectedSingleMixer, deviceLatency / 1000.0) * 1000.0;

                    Debug.Log($"<b>[Real Seek Latency] Single Mixer: {expectedSingleMixer}</b>\n" +
                              $"  - Measured Total Elapsed (to 200ms mark): {totalElapsedMs}ms\n" +
                              $"  - Calculated Actual Seek Latency: <b>{actualSeekLatencyMs}ms</b>\n" +
                              $"  - Expected SongRunner Seek Latency: {expectedSongRunnerSeek:0.0}ms\n" +
                              $"  - Reported AudibleSync Latency: {audibleSync:0.0}ms\n" +
                              $"  - Reported Device Latency: {deviceLatency:0.0}ms\n" +
                              $"  - Reported Start Latency: {start:0.0}ms\n" +
                              $"  - BASS Latency Components: info.Latency={infoLatency}ms, " +
                              $"DeviceBufferLength={deviceBufferLength}ms, devPeriod={devPeriod}ms, " +
                              $"MinBuf={minBufferLength}ms");
                }
                else
                {
                    Bass.ChannelRemoveSync(tempoStreamHandle, syncHandle);
                    throw new Exception("Timeout waiting for BASS position sync (audio did not play or seek failed)");
                }

                GC.KeepAlive(syncCallback);
            }
            finally
            {
                mixer.Dispose();
                fileStream?.Dispose();
            }
        }
    }
}
