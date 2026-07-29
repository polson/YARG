using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Menu.Persistent;
using YARG.Settings;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YARG.Audio.BASS
{
    internal class StreamHandle : IDisposable
    {
#nullable enable
        public static StreamHandle? Create(int sourceStream, int[] indices)
        {
            const BassFlags splitFlags = BassFlags.Decode | BassFlags.SplitPosition;

            int[]? channelMap = null;
#nullable disable
            if (indices.Length > 0)
            {
                channelMap = new int[indices.Length + 1];
                for (int i = 0; i < indices.Length; ++i)
                {
                    channelMap[i] = indices[i];
                }
                channelMap[indices.Length] = -1;
            }

            int streamSplit = BassMix.CreateSplitStream(sourceStream, splitFlags, channelMap);
            if (streamSplit == 0)
            {
                YargLogger.LogFormatError("Failed to create split stream: {0}!", Bass.LastError);
                return null;
            }
            return new StreamHandle(streamSplit);
        }

        private          bool _disposed;
        public readonly  int  Stream;

#pragma warning disable CS0649
        public int CompressorFX;
        public int PitchFX;
        public int LowEQ;
        public int MidEQ;
        public int HighEQ;
#pragma warning restore CS0649

        private StreamHandle(int stream)
        {
            Stream = stream;
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                // FX handles are freed automatically, we only need to free the stream
                if (!Bass.StreamFree(Stream))
                {
                    YargLogger.LogFormatError("Failed to free channel stream (THIS WILL LEAK MEMORY): {0}!", Bass.LastError);
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~StreamHandle()
        {
            Dispose(false);
        }
    }

    public class BassAudioManager : AudioManager
    {
        private static readonly string[] FORMATS =
        {
            ".ogg", ".mogg", ".wav", ".mp3", ".aiff", ".opus",
        };

        protected override ReadOnlySpan<string> SupportedFormats => FORMATS;

        private readonly int _opusHandle = 0;
        private BassOutputDevice _currentDevice;
        private readonly BassAudioOutput _audioOutput;
        private int _asioBufferLength;

        public BassAudioManager()
        {
            YargLogger.LogInfo("Initializing BASS...");
            _audioOutput = new BassAudioOutput(OnAsioReinitializeRequested);
            string bassPath = GetBassDirectory();
            string opusLibDirectory = Path.Combine(bassPath, "bassopus");

            _opusHandle = Bass.PluginLoad(opusLibDirectory);
            if (_opusHandle == 0) YargLogger.LogFormatError("Failed to load .opus plugin: {0}!", Bass.LastError);

            Bass.Configure(Configuration.IncludeDefaultDevice, true);

            Bass.UpdatePeriod = 5;
            //Bass.PlaybackBufferLength = BassHelpers.PLAYBACK_BUFFER_LENGTH;
            Bass.DeviceNonStop = true;
            Bass.AsyncFileBufferLength = 65536;

            // This not the same as Bass.UpdatePeriod
            // If not explicitly set by the audio driver or OS, the default will be 10
            // https://www.un4seen.com/doc/#bass/BASS_CONFIG_DEV_PERIOD.html
            int devPeriod = Bass.GetConfig(Configuration.DevicePeriod);

            // Documentation recommends setting the device buffer to at least 2x the device period
            // https://www.un4seen.com/doc/#bass/BASS_CONFIG_DEV_BUFFER.html
            Bass.DeviceBufferLength = 2 * devPeriod;

            // Affects Windows only. Forces device names to be in UTF-8 on Windows rather than ANSI.
            Bass.UnicodeDeviceInformation = true;
            Bass.FloatingPointDSP = true;
            Bass.VistaTruePlayPosition = false;
            Bass.UpdateThreads = GlobalAudioHandler.MAX_THREADS;

            // Undocumented BASS_CONFIG_MP3_OLDGAPS config.
            Bass.Configure((Configuration) 68, 1);

            // Disable undocumented BASS_CONFIG_DEV_TIMEOUT config. Prevents pausing audio output if a device times out.
            Bass.Configure((Configuration) 70, false);

            int deviceCount = Bass.DeviceCount;
            YargLogger.LogFormatInfo("Devices found: {0}", deviceCount);

#if UNITY_EDITOR
            // BASS_Free only frees playback devices. Recording devices have a
            // separate lifecycle and can remain initialized across editor play-mode
            // sessions, which makes GetAllInputDevices treat them as claimed.
            // Do this independently of CurrentDevice: playback may already be freed.
            for (int deviceIndex = 0; Bass.RecordGetDeviceInfo(deviceIndex, out var recordInfo); deviceIndex++)
            {
                if (!recordInfo.IsInitialized)
                {
                    continue;
                }

                Bass.CurrentRecordingDevice = deviceIndex;
                if (!Bass.RecordFree())
                {
                    YargLogger.LogWarning(
                        $"Failed to free stale BASS recording device [{deviceIndex}] '{recordInfo.Name}': " +
                        $"{Bass.LastError}");
                }
            }

            // Free playback BASS if still initialized from previous play-mode session.
            if (Bass.CurrentDevice != -1)
            {
                YargLogger.LogInfo("BASS already initialized, cleaning up first");
                try
                {
                    Bass.Free();
                    Bass.PluginFree(0);
                }
                catch (Exception ex)
                {
                    YargLogger.LogWarning($"Exception encountered during BASS pre-initialization cleanup: {ex.Message}");
                }
            }
#endif

            string startupDevice = SettingsManager.OutputDeviceAtStartup;
            var result = SetOutputDevice(startupDevice);

            if (!result && startupDevice != "Default")
            {
                YargLogger.LogFormatWarning("Failed to initialize saved output '{0}', falling back to Default",
                    startupDevice);
                result = SetOutputDevice("Default");
            }

            if (!result)
            {
                var error = Bass.LastError;
                YargLogger.LogFormatError("BASS Initialization Failure: Failed to set default output device: {0}", error);

#if UNITY_STANDALONE_LINUX
                // Driver seems to be what we get when ALSA isn't available
                if (error == Errors.Driver)
                {
                    YargLogger.LogError("Failed to set default output device. This is likely due to a missing ALSA plugin. Install pipewire-alsa or equivalent.");
                    ToastManager.ToastError("Failed to initialize audio device. Make sure you have pipewire-alsa or equivalent installed.");
                }
#endif
                return;
            }

            var info = Bass.Info;
            UpdatePlaybackLatency();
            MinimumBufferLength = info.MinBufferLength + Bass.UpdatePeriod;
            MaximumBufferLength = 5000;

            YargLogger.LogInfo("BASS Successfully Initialized");
            YargLogger.LogFormatInfo("BASS: {0} - BASS.FX: {1} - BASS.Mix: {2}", Bass.Version, BassFx.Version, BassMix.Version);
            YargLogger.LogFormatInfo("Update Period: {0}ms. Device Buffer Length: {1}ms. Playback Buffer Length: {2}ms. Device Playback Latency: {3}ms",
                Bass.UpdatePeriod, Bass.DeviceBufferLength, Bass.PlaybackBufferLength, PlaybackLatency);

            YargLogger.LogFormatInfo("Current Device: {0}", _currentDevice.DisplayName);
        }

        private void UpdatePlaybackLatency()
        {
            PlaybackLatency = _audioOutput.HeardLatencyMilliseconds;
        }

        protected override AudioOutputMetrics OutputMetrics => _audioOutput.Metrics;

        protected override void ResetOutputMetrics() => _audioOutput.ResetMetrics();

        protected override bool SetOutputDevice(string name)
        {
            int bufferLength = SettingsManager.GetAsioBufferLength(name);
            if (_currentDevice?.DisplayName == name && _asioBufferLength == bufferLength)
            {
                return true;
            }

            int previousBufferLength = _asioBufferLength;
            _asioBufferLength = bufferLength;
            if (ApplyOutputDevice(name, previousBufferLength))
            {
                return true;
            }

            _asioBufferLength = previousBufferLength;

            return RestoreDefaultOutput(name);
        }

        private bool RestoreDefaultOutput(string failedOutput)
        {
            if (failedOutput == "Default")
            {
                return false;
            }

            YargLogger.LogFormatError("Failed to initialize audio output '{0}', falling back to Default",
                failedOutput);
            _asioBufferLength = 0;
            bool restored = ApplyOutputDevice("Default", 0);
            if (restored && SettingsManager.SettingContainer.IsInitialized)
            {
                SettingsManager.Settings.OutputDevice.SetValueWithoutNotify("Default");
                ToastManager.ToastError($"Failed to initialize {failedOutput}. Using Default audio output.");
            }
            return restored;
        }

        private bool ApplyOutputDevice(string name, int restoreAsioBufferLength)
        {

#nullable enable
            var venueSamples = new List<(string Name, byte[] Data, OutputChannel? OutputChannel)>();
#nullable disable
            foreach (var sample in VenueSamples.Values)
            {
                if (sample is BassVenueSampleChannel bassSample)
                {
                    venueSamples.Add((bassSample.SampleName, bassSample.SampleData, bassSample.OutputChannel));
                }
            }

            var device = GetOutputDevice(name);
            if (device is not BassOutputDevice bassDevice)
            {
                return false;
            }

            if (_currentDevice != null)
            {
                BassOutputDevice previousDevice = _currentDevice;
                previousDevice.Use();
                UnloadSfx();
                UnloadDrumSfx();
                UnloadVox();
                UnloadVenueSamples();
                UnloadMetronome();
                _audioOutput.Suspend();

                bassDevice.Use();
                MoveActiveMixersTo(bassDevice);
                if (!_audioOutput.Resume(bassDevice, _asioBufferLength))
                {
                    YargLogger.LogError(
                        $"Failed to start audio output '{bassDevice.DisplayName}', " +
                        $"restoring '{previousDevice.DisplayName}'");
                    previousDevice.Use();
                    MoveActiveMixersTo(previousDevice);
                    bassDevice.Dispose();
                    previousDevice.Use();
                    _asioBufferLength = restoreAsioBufferLength;
                    if (!_audioOutput.Resume(previousDevice, _asioBufferLength))
                    {
                        YargLogger.LogFormatError("Failed to restore audio output '{0}'",
                            previousDevice.DisplayName);
                    }
                    UpdatePlaybackLatency();
                    ReloadSamples(venueSamples);
                    return false;
                }

                _currentDevice = bassDevice;
                previousDevice.TransferOwnershipTo(bassDevice);
                previousDevice.Dispose();
                bassDevice.Use();
            }
            else
            {
                _currentDevice = bassDevice.Use();
                if (!_audioOutput.InitializeForDevice(bassDevice, _asioBufferLength))
                {
                    _audioOutput.ResetForDeviceChange();
                    _currentDevice.Dispose();
                    _currentDevice = null;
                    return false;
                }
            }

            UpdatePlaybackLatency();

            YargLogger.LogFormatInfo("Current audio output: {0}", bassDevice.DisplayName);

            ReloadSamples(venueSamples);
            return true;
        }

        protected override OutputBufferInfo? GetOutputBufferInfo()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_currentDevice?.IsAsio != true)
            {
                return null;
            }

            try
            {
                BassAsio.CurrentDevice = _currentDevice.AsioDeviceId;
                var info = BassAsio.Info;
                var lengths = GetBufferLengths(info);
                int sampleRate = (int) Math.Round(BassAsio.Rate);
                bool isDriverControlled = info.BufferLengthGranularity == 0 &&
                    info.MinBufferLength == info.MaxBufferLength;
                return new OutputBufferInfo(lengths, info.PreferredBufferLength, sampleRate, isDriverControlled);
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to read ASIO buffer sizes");
            }
#endif
            return null;
        }

        protected override bool OpenOutputControlPanel()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_currentDevice?.IsAsio != true)
            {
                return false;
            }

            BassAsio.CurrentDevice = _currentDevice.AsioDeviceId;
            if (BassAsio.ControlPanel())
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to open ASIO control panel: {0}", BassAsio.LastError);
#endif
            return false;
        }

        protected override bool ReinitializeOutput(int bufferLength)
        {
            if (_currentDevice?.IsAsio != true || bufferLength < 0)
            {
                return false;
            }

            int previousBufferLength = _asioBufferLength;
            _asioBufferLength = bufferLength;
            if (ApplyOutputDevice(_currentDevice.DisplayName, previousBufferLength))
            {
                return true;
            }

            _asioBufferLength = previousBufferLength;
            return false;
        }

        private void OnAsioReinitializeRequested()
        {
            if (_currentDevice?.IsAsio != true)
            {
                return;
            }

            if (!ReinitializeOutput(_asioBufferLength))
            {
                YargLogger.LogError("Failed to reinitialize audio after ASIO driver settings changed");
                ToastManager.ToastError("Failed to reinitialize audio after ASIO driver settings changed.");
            }
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private static int[] GetBufferLengths(AsioInfo info)
        {
            var lengths = new List<int>();
            int minimum = info.MinBufferLength;
            int maximum = info.MaxBufferLength;

            if (minimum <= 0 || maximum < minimum)
            {
                return Array.Empty<int>();
            }

            if (info.BufferLengthGranularity == -1)
            {
                for (long length = minimum; length <= maximum; length *= 2)
                {
                    lengths.Add((int) length);
                    if (length > int.MaxValue / 2)
                    {
                        break;
                    }
                }
            }
            else if (info.BufferLengthGranularity > 0)
            {
                for (long length = minimum; length <= maximum; length += info.BufferLengthGranularity)
                {
                    lengths.Add((int) length);
                }
            }
            else
            {
                lengths.Add(minimum);
            }

            if (info.PreferredBufferLength >= minimum && info.PreferredBufferLength <= maximum &&
                !lengths.Contains(info.PreferredBufferLength))
            {
                lengths.Add(info.PreferredBufferLength);
                lengths.Sort();
            }
            return lengths.ToArray();
        }
#endif

#nullable enable
        private void ReloadSamples(List<(string Name, byte[] Data, OutputChannel? OutputChannel)> venueSamples)
#nullable disable
        {
            LoadSfx();
            LoadDrumSfx(); // TODO: move drum sfx loading/disposal to song start/end respectively IF there are any drum players
            LoadVox();
            LoadMetronome();
            foreach (var sample in venueSamples)
            {
                LoadVenueSample(sample.Name, sample.Data, sample.OutputChannel);
            }
        }

#nullable enable
        protected override StemMixer? CreateMixer(string name, float speed, double mixerVolume, bool clampStemVolume, bool normalize)
        {
            if (GlobalAudioHandler.LogMixerStatus)
            {
                YargLogger.LogDebug("Loading song");
            }

            if (!CreateMixerHandle(out int handle))
            {
                return null;
            }
            return new BassStemMixer(name, this, speed, mixerVolume, handle, clampStemVolume: clampStemVolume,
                normalize: normalize, outputChannel: CreateOutputChannel(SettingsManager.Settings?.OutputChannelDefault.Value ?? 0));
        }

        internal BassSongPlayback CreateSongPlayback(int tempoStreamHandle)
        {
            return _audioOutput.CreateSongPlayback(tempoStreamHandle);
        }

        internal IReadOnlyList<AsioInputDescriptor> GetAsioInputDescriptors() =>
            _audioOutput.GetAsioInputDescriptors();

        internal AsioInputAcquireResult TryAcquireAsioInput(string driverId, int channelIndex,
            out BassAsioInputLease? lease) =>
            _audioOutput.TryAcquireAsioInput(driverId, channelIndex, out lease);

        internal bool TryGetAsioInputLevel(int channelIndex, out double level) =>
            _audioOutput.TryGetAsioInputLevel(channelIndex, out level);

        private const string ASIO_MIC_PREFIX = "ASIO: ";

        private static string GetAsioMicName(AsioInputDescriptor descriptor) =>
            $"{ASIO_MIC_PREFIX}{descriptor.DriverName} - {descriptor.ChannelIndex}: {descriptor.Name}";

        private AsioInputDescriptor? FindAsioInput(string name)
        {
            foreach (var descriptor in GetAsioInputDescriptors())
            {
                if (string.Equals(GetAsioMicName(descriptor), name, StringComparison.Ordinal))
                {
                    return descriptor;
                }
            }
            return null;
        }

        protected override MicDevice? GetInputDevice(string name)
        {
            if (_currentDevice?.IsAsio == true)
            {
                var asioInput = FindAsioInput(name);
                return asioInput != null
                    ? BassAsioMicDevice.Create(this, asioInput, name)
                    : null;
            }

            // ASIO inputs are valid only while their owning ASIO output driver is active.
            if (name.StartsWith(ASIO_MIC_PREFIX, StringComparison.Ordinal))
            {
                return null;
            }

            for (int deviceIndex = 0; Bass.RecordGetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled/claimed devices
                if (!info.IsEnabled || info.IsInitialized)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                // Check if type is in whitelist
                // The "Default" device is also excluded here since we want the user to explicitly pick which microphone to use
                // if (!typeWhitelist.Contains(info.Type) || info.Name == "Default") continue;
                if (info.Name == "Default" || info.Name != name)
                {
                    continue;
                }

                return CreateInputDevice(deviceIndex, name);
            }

            return null;
        }
#nullable disable

        protected override List<(int id, string name)> GetAllInputDevices()
        {
            var mics = new List<(int id, string name)>();

            if (_currentDevice?.IsAsio == true)
            {
                foreach (var descriptor in GetAsioInputDescriptors())
                {
                    mics.Add((descriptor.ChannelIndex, GetAsioMicName(descriptor)));
                }
                return mics;
            }

            // Ignored for now since it causes issues on Linux, BASS must not report device info correctly there
            // TODO: allow configuring this at runtime?
            // Also put into a static variable instead of instantiating every time
            // var typeWhitelist = new List<DeviceType>()
            // {
            //     DeviceType.Headset,
            //     DeviceType.Digital,
            //     DeviceType.Line,
            //     DeviceType.Headphones,
            //     DeviceType.Microphone,
            // };

            for (int deviceIndex = 0; Bass.RecordGetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled/claimed devices
                if (!info.IsEnabled || info.IsInitialized)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                // Check if type is in whitelist
                // The "Default" device is also excluded here since we want the user to explicitly pick which microphone to use
                // if (!typeWhitelist.Contains(info.Type) || info.Name == "Default") continue;
                if (info.Name == "Default")
                {
                    continue;
                }

                mics.Add((deviceIndex, info.Name));
            }

            return mics;
        }

#nullable enable
        protected override MicDevice? CreateInputDevice(int deviceId, string name)
#nullable disable
        {
            if (_currentDevice?.IsAsio == true)
            {
                var descriptor = FindAsioInput(name);
                if (descriptor == null || descriptor.ChannelIndex != deviceId)
                {
                    return null;
                }
                return BassAsioMicDevice.Create(this, descriptor, name);
            }

            if (name.StartsWith(ASIO_MIC_PREFIX, StringComparison.Ordinal))
            {
                return null;
            }

            var device = BassMicDevice.Create(deviceId, name, _audioOutput);
            device?.SetMonitoringLevel(SettingsManager.Settings.VocalMonitoring.Value);
            return device;
        }

#nullable enable
        protected override OutputChannel? CreateOutputChannel(int channelId)
#nullable disable
        {
            return BassOutputChannel.Create(channelId);
        }

#nullable enable
        protected override OutputDevice? CreateOutputDevice(int deviceId, string name)
#nullable disable
        {
            return BassOutputDevice.Create(deviceId, name);
        }

        protected override List<(int id, string name)> GetAllOutputDevices()
        {
            var devices = new List<(int id, string name)>();

            for (int deviceIndex = 1; Bass.GetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled devices
                if (!info.IsEnabled)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                devices.Add((deviceIndex, info.Name));
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                for (int deviceIndex = 0; deviceIndex < BassAsio.DeviceCount; deviceIndex++)
                {
                    var info = BassAsio.GetDeviceInfo(deviceIndex);
                    devices.Add((deviceIndex, BassOutputDevice.ASIO_PREFIX + info.Name));
                }
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to enumerate ASIO devices");
            }
#endif

            return devices;
        }

        protected override int GetOutputChannelCount()
        {
            return BassHelpers.GetOutputChannelCount();
        }

#nullable enable
        protected override OutputDevice? GetOutputDevice(string name)
#nullable disable
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (name.StartsWith(BassOutputDevice.ASIO_PREFIX, StringComparison.Ordinal))
            {
                string driverName = name.Substring(BassOutputDevice.ASIO_PREFIX.Length);
                try
                {
                    for (int deviceIndex = 0; deviceIndex < BassAsio.DeviceCount; deviceIndex++)
                    {
                        var info = BassAsio.GetDeviceInfo(deviceIndex);
                        if (info.Name == driverName)
                        {
                            return BassOutputDevice.CreateAsio(deviceIndex, driverName);
                        }
                    }
                }
                catch (Exception exception)
                {
                    YargLogger.LogException(exception, "Failed to find ASIO device");
                }

                return null;
            }
#endif

            for (int deviceIndex = 0; Bass.GetDeviceInfo(deviceIndex, out var info); deviceIndex++)
            {
                // Ignore disabled devices
                if (!info.IsEnabled)
                {
                    continue;
                }

                // Ignore loopback devices, they're potentially confusing and can cause feedback loops
                if (info.IsLoopback)
                {
                    continue;
                }

                // Ensure device names match
                if (info.Name != name)
                {
                    continue;
                }

                return CreateOutputDevice(deviceIndex, name);
            }

            return null;
        }

        private void LoadSfx()
        {
            YargLogger.LogInfo("Loading SFX");

            UnloadSfx();

            string sfxFolder = Path.Combine(Application.streamingAssetsPath, "sfx");

            foreach (var sample in AudioHelpers.SfxSamples)
            {
                var sfxFile = sample.File;
                string sfxBase = Path.Combine(sfxFolder, sfxFile);
                foreach (string format in SupportedFormats)
                {
                    string sfxPath = sfxBase + format;
                    if (File.Exists(sfxPath))
                    {
                        var sfxSample = sample.Kind;
                        var sfx = BassSampleChannel.Create(sfxSample, sfxPath, 8, _audioOutput,
                            CreateOutputChannel(SettingsManager.Settings?.OutputChannelSfx.Value ?? 0), sample.CanLoop);
                        if (sfx != null)
                        {
                            SfxSamples[(int) sfxSample] = sfx;
                            YargLogger.LogFormatInfo("Loaded {0}", sfxFile);
                        }
                        break;
                    }
                }
            }

            YargLogger.LogInfo("Finished loading SFX");
        }

        private void UnloadSfx()
        {
#nullable enable
            foreach (BassSampleChannel? sample in SfxSamples)
#nullable disable
            {
                sample?.Dispose();
            }

            SfxSamples = new SampleChannel[AudioHelpers.SfxSamples.Count];
        }

        private void LoadDrumSfx()
        {
            YargLogger.LogInfo("Loading Drum SFX");

            UnloadDrumSfx();

            string sfxFolder = Path.Combine(Application.streamingAssetsPath, "drumSfx");

            foreach (var sample in AudioHelpers.DrumSamples)
            {
                string sfxBase = Path.Combine(sfxFolder, sample.File);
                foreach (string format in SupportedFormats)
                {
                    string sfxPath = sfxBase + format;
                    if (File.Exists(sfxPath))
                    {
                        var sfxSample = sample.Kind;
                        var sfx = BassDrumSampleChannel.Create(sfxSample, sfxPath, 8, _audioOutput,
                            CreateOutputChannel(SettingsManager.Settings?.OutputChannelDrumSfx.Value ?? 0));
                        if (sfx != null)
                        {
                            DrumSfxSamples[(int) sfxSample] = sfx;
                        }
                        break;
                    }
                }
            }

            YargLogger.LogInfo("Finished loading Drum SFX");
        }

        private void UnloadDrumSfx()
        {
#nullable enable
            foreach (BassDrumSampleChannel? sample in DrumSfxSamples)
#nullable disable
            {
                sample?.Dispose();
            }

            DrumSfxSamples = new DrumSampleChannel[AudioHelpers.DrumSamples.Count];
        }

        private void LoadVox()
        {
            YargLogger.LogInfo("Loading VOX");

            UnloadVox();

            string voxFolder = Path.Combine(Application.streamingAssetsPath, "vox");

            foreach (var sample in AudioHelpers.VoxSamples)
            {
                string voxBase = Path.Combine(voxFolder, sample.File);
                foreach (string format in SupportedFormats)
                {
                    string voxPath = voxBase + format;
                    if (File.Exists(voxPath))
                    {
                        var voxSample = sample.Kind;
                        var vox = BassVoxSampleChannel.Create(voxSample, voxPath, _audioOutput,
                            CreateOutputChannel(SettingsManager.Settings?.OutputChannelVox.Value ?? 0));

                        if (vox != null)
                        {
                            VoxSamples[(int) voxSample] = vox;
                        }

                        break;
                    }
                }
            }

            YargLogger.LogInfo("Finished loading VOX");
        }

        private void UnloadVox()
        {
#nullable enable
            foreach (BassVoxSampleChannel? sample in VoxSamples)
#nullable disable
            {
                sample?.Dispose();
            }

            VoxSamples = new VoxSampleChannel[AudioHelpers.VoxSamples.Count];
        }

        private void LoadMetronome()
        {
            YargLogger.LogInfo("Loading Metronome");

            UnloadMetronome();

            string metronomeFolder = Path.Combine(Application.streamingAssetsPath, "metronome");

            foreach (var sample in AudioHelpers.MetronomeSamples)
            {
                string metronomeHi = Path.Combine(metronomeFolder, sample.File);
                string metronomeLo = Path.Combine(metronomeFolder, sample.AlternateFile);

                string metronomeHiPath = "";
                string metronomeLoPath = "";

                foreach (string format in SupportedFormats)
                {
                    if (File.Exists(metronomeHi + format))
                    {
                        metronomeHiPath = metronomeHi + format;
                    }

                    if (File.Exists(metronomeLo + format))
                    {
                        metronomeLoPath = metronomeLo + format;
                    }
                }

                if (!String.IsNullOrEmpty(metronomeHiPath) && !String.IsNullOrEmpty(metronomeLoPath))
                {
                    var metronomeSample = sample.Kind;
                    var metronome = BassMetronomeSampleChannel.Create(metronomeSample, metronomeHiPath,
                        metronomeLoPath, _audioOutput,
                        CreateOutputChannel(SettingsManager.Settings?.OutputChannelDefault.Value ?? 0));
                    if (metronome != null)
                    {
                        MetronomeSamples[(int) metronomeSample] = metronome;
                    }
                }
            }

            YargLogger.LogInfo("Finished loading Metronome");
        }

        private void UnloadMetronome()
        {

#nullable enable
            foreach (BassMetronomeSampleChannel? sample in MetronomeSamples)
#nullable disable
            {
                sample?.Dispose();
            }

            MetronomeSamples = new MetronomeSampleChannel[AudioHelpers.MetronomeSamples.Count];
        }

#nullable enable
        public override void LoadVenueSample(string name, byte[] sampleData, OutputChannel? outputChannel = null)
#nullable disable
        {
            if (VenueSamples.TryGetValue(name, out var existing))
            {
                existing.Dispose();
            }

            VenueSamples[name] = BassVenueSampleChannel.Create(name, sampleData, _audioOutput, outputChannel);
        }

        public override void ClearVenueSamples()
        {
            UnloadVenueSamples();
        }

        private void UnloadVenueSamples()
        {
            foreach (var sample in VenueSamples.Values)
            {
                sample.Stop();
                sample.Dispose();
            }

            VenueSamples.Clear();
        }


        protected override void SetMasterVolume(double volume)
        {
#if UNITY_EDITOR
            if (EditorUtility.audioMasterMute)
                volume = 0;
#endif
            Bass.GlobalStreamVolume = (int) (10_000 * volume);
            Bass.GlobalSampleVolume = (int) (10_000 * volume);
            _audioOutput.SetVolume(volume);
        }


        protected override void SetBufferLength_Internal(int length)
        {
            length = BassHelpers.ClampPlaybackBufferLength(length);
            Bass.PlaybackBufferLength = length;
        }

        protected override void DisposeUnmanagedResources()
        {
            _audioOutput.Dispose();
            YargLogger.LogInfo("Unloading BASS plugins");
            Bass.Free();
            Bass.PluginFree(0);
        }

        private static string GetBassDirectory()
        {
            string pluginDirectory = Path.Combine(Application.dataPath, "Plugins");

            // Locate windows directory
            // Checks if running on 64 bit and sets the path accordingly
#if !UNITY_EDITOR && UNITY_STANDALONE_WIN
#if UNITY_64
			pluginDirectory = Path.Combine(pluginDirectory, "x86_64");
#else
			pluginDirectory = Path.Combine(pluginDirectory, "x86");
#endif
#endif

            // Unity Editor directory, Assets/Plugins/Bass/
#if UNITY_EDITOR
            pluginDirectory = Path.Combine(pluginDirectory, "BassNative");
#endif

            // Editor paths differ to standalone paths, as the project contains platform specific folders
#if UNITY_EDITOR_WIN
            pluginDirectory = Path.Combine(pluginDirectory, "Windows/x86_64");
#elif UNITY_EDITOR_OSX
			pluginDirectory = Path.Combine(pluginDirectory, "Mac");
#elif UNITY_EDITOR_LINUX
			pluginDirectory = Path.Combine(pluginDirectory, "Linux/x86_64");
#endif

            return pluginDirectory;
        }

        private static bool CreateMixerHandle(out int mixerHandle)
        {
            // The float flag allows >0dB signals.
            // Note that the compressor attempts to normalize signals >-2dB, but some mixes will pierce through.
            mixerHandle = BassMix.CreateMixerStream(44100, 2, BassFlags.Float | BassFlags.Decode);
            if (mixerHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create mixer: {0}!", Bass.LastError);
                return false;
            }

            int compressorFX = BassHelpers.AddCompressorToChannel(mixerHandle);
            if (compressorFX == 0)
            {
                YargLogger.LogError("Failed to set up compressor for mixer stream!");
            }
            return true;
        }

        internal static bool CreateSourceStream(Stream stream, out int streamHandle)
        {
            // Last flag is new BASS_SAMPLE_NOREORDER flag, which is not in the BassFlags enum,
            // as it was made as part of an update to fix <= 8 channel oggs.
            // https://www.un4seen.com/forum/?topic=20148.msg140872#msg140872
            const BassFlags streamFlags = BassFlags.Prescan | BassFlags.Decode | BassFlags.AsyncFile | (BassFlags) 64;

            var procedures = new BassStreamProcedures(stream);
            streamHandle = Bass.CreateStream(StreamSystem.NoBuffer, streamFlags, procedures);
            if (streamHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create source stream: {0}!", Bass.LastError);
                return false;
            }
            procedures.RegisterStream(streamHandle);
            return true;
        }

        internal static bool GetSpeed(int streamHandle, out float speed)
        {
            if (!Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Tempo, out float relativeSpeed))
            {
                speed = 0;
                YargLogger.LogFormatError("Failed to get channel speed: {0}", Bass.LastError);
                return false;
            }

            // Turn relative speed into percentage speed
            float percentageSpeed = relativeSpeed + 100;
            speed = percentageSpeed / 100;

            return true;
        }

        internal static void SetSpeed(float speed, int streamHandle, bool shiftPitch)
        {
            // Gets relative speed from 100% (so 1.05f = 5% increase)
            float percentageSpeed = speed * 100;
            float relativeSpeed = percentageSpeed - 100;


            if (!Bass.ChannelSetAttribute(streamHandle, ChannelAttribute.Tempo, relativeSpeed))
            {
                YargLogger.LogFormatError("Failed to set channel speed: {0}!", Bass.LastError);
            }

            if (GlobalAudioHandler.IsChipmunkSpeedup && shiftPitch)
            {
                SetChipmunking(speed, streamHandle);
            }
        }

#nullable enable
        internal static (StreamHandle Stream, StreamHandle Reverb)? CreateSplitStreams(int sourceStream, int[] channelMap)
#nullable disable
        {
            var streamHandles = StreamHandle.Create(sourceStream, channelMap);
            if (streamHandles == null)
            {
                return null;
            }

            var reverbHandles = StreamHandle.Create(sourceStream, channelMap);
            if (reverbHandles == null)
            {
                streamHandles.Dispose();
                return null;
            }
            return (streamHandles, reverbHandles);
        }

        internal static PitchShiftParametersStruct SetPitchParams(SongStem stem, float speed, StreamHandle streamHandles, StreamHandle reverbHandles)
        {
            PitchShiftParametersStruct pitchParams = new(1, 0, GlobalAudioHandler.WHAMMY_FFT_DEFAULT, GlobalAudioHandler.WHAMMY_OVERSAMPLE_DEFAULT);
            // Set whammy pitch bending if enabled
            if (GlobalAudioHandler.UseWhammyFx && AudioHelpers.PitchBendAllowedStems.Contains(stem))
            {
                // Setting the FFT size causes a crash in BASS_FX :/
                // _pitchParams.FFTSize = _manager.Options.WhammyFFTSize;
                pitchParams.OversampleFactor = GlobalAudioHandler.WhammyOversampleFactor;
                if (SetupPitchBend(pitchParams, streamHandles))
                {
                    SetupPitchBend(pitchParams, reverbHandles);
                }
            }
            return pitchParams;
        }

        internal static void SetChipmunking(float speed, int streamHandle)
        {
            double accurateSemitoneShift = 12 * Math.Log(speed, 2);
            float finalSemitoneShift = (float) Math.Clamp(accurateSemitoneShift, -60, 60);
            if (!Bass.ChannelSetAttribute(streamHandle, ChannelAttribute.Pitch, finalSemitoneShift))
            {
                YargLogger.LogFormatError("Failed to set channel pitch: {0}!", Bass.LastError);
            }
        }

        internal static bool SetupPitchBend(in PitchShiftParametersStruct pitchParams, StreamHandle handles)
        {
            handles.PitchFX = BassHelpers.FXAddParameters(handles.Stream, EffectType.PitchShift, pitchParams);
            if (handles.PitchFX == 0)
            {
                YargLogger.LogError("Failed to set up pitch bend for main stream!");
                return false;
            }

            return true;
        }

        internal static double GetLengthInSeconds(int handle)
        {
            long length = Bass.ChannelGetLength(handle);
            if (length < 0)
            {
                YargLogger.LogFormatError("Failed to get channel length in bytes: {0}!", Bass.LastError);
                return -1;
            }

            double seconds = Bass.ChannelBytes2Seconds(handle, length);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to get channel length in seconds: {0}!", Bass.LastError);
                return -1;
            }

            return seconds;
        }

        private const double BASE = 2;
        private const double FACTOR = BASE - 1;
        internal static double ExponentialVolume(double volume)
        {
            return (Math.Pow(BASE, volume) - 1) / FACTOR;
        }

        internal static double LogarithmicVolume(double volume)
        {
            return Math.Log(FACTOR * volume + 1, BASE);
        }
    }
}
