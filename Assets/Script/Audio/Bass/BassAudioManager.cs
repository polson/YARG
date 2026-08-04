using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
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

    /// <summary>
    /// Orchestrates transport switching. All transport-specific behavior (ASIO driver setup,
    /// buffer configuration, input ownership, driver notifications) lives in the transports;
    /// this class only resolves, activates, routes, and rolls back.
    /// </summary>
    public class BassAudioManager : AudioManager
    {
        private static readonly string[] FORMATS =
        {
            ".ogg", ".mogg", ".wav", ".mp3", ".aiff", ".opus",
        };

        protected override ReadOnlySpan<string> SupportedFormats => FORMATS;

        private readonly int _opusHandle = 0;
        private BassAudioTransport? _currentTransport;
        private readonly BassAudioOutput _audioOutput = new();
        private int _bufferLength;

        public BassAudioManager()
        {
            YargLogger.LogInfo("Initializing BASS...");
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
                    BassDeviceContextLease.ResetForEditor();
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

            YargLogger.LogFormatInfo("Current Device: {0}", _currentTransport?.Descriptor.DisplayName);
        }

        private void UpdatePlaybackLatency()
        {
            PlaybackLatency = _audioOutput.HeardLatencyMilliseconds;
        }

        protected override bool SetOutputDevice(string name)
        {
            int bufferLength = BassAudioTransport.GetBackend(name) == AudioOutputBackend.Asio
                ? SettingsManager.GetAsioBufferLength(name)
                : 0;
            if (_currentTransport?.Descriptor.DisplayName == name && _bufferLength == bufferLength)
            {
                return true;
            }

            int previousBufferLength = _bufferLength;
            _bufferLength = bufferLength;
            if (ApplyOutputDevice(name, previousBufferLength))
            {
                return true;
            }

            _bufferLength = previousBufferLength;

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
            _bufferLength = 0;
            bool restored = ApplyOutputDevice("Default", 0);
            if (restored && SettingsManager.SettingContainer.IsInitialized)
            {
                SettingsManager.Settings.OutputDevice.SetValueWithoutNotify("Default");
                ToastManager.ToastError($"Failed to initialize {failedOutput}. Using Default audio output.");
            }
            return restored;
        }

        private bool ApplyOutputDevice(string name, int restoreBufferLength)
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

            var transport = BassAudioTransport.Create(name, _audioOutput);
            if (transport == null)
            {
                return false;
            }

            if (_currentTransport != null)
            {
                BassAudioTransport previous = _currentTransport;
                previous.BassMixerDevice.Use();
                UnloadSfx();
                UnloadDrumSfx();
                UnloadVox();
                UnloadVenueSamples();
                UnloadMetronome();
                _audioOutput.SuspendRoutes();
                _audioOutput.DetachBackend();

                if (!transport.Activate(new AudioTransportConfiguration(_bufferLength)))
                {
                    YargLogger.LogError(
                        $"Failed to start audio output '{name}', " +
                        $"restoring '{previous.Descriptor.DisplayName}'");
                    transport.Dispose();
                    previous.BassMixerDevice.Use();
                    MoveActiveMixersTo(previous.MixerDevice);
                    _bufferLength = restoreBufferLength;
                    if (!_audioOutput.AttachBackend(previous.Backend, previous.BassDeviceId))
                    {
                        YargLogger.LogFormatError("Failed to restore audio output '{0}'",
                            previous.Descriptor.DisplayName);
                    }
                    UpdatePlaybackLatency();
                    ReloadSamples(venueSamples);
                    return false;
                }

                transport.BassMixerDevice.Use();
                MoveActiveMixersTo(transport.MixerDevice);
                if (!_audioOutput.AttachBackend(transport.Backend, transport.BassDeviceId))
                {
                    YargLogger.LogError(
                        $"Failed to start audio output '{name}', " +
                        $"restoring '{previous.Descriptor.DisplayName}'");
                    previous.BassMixerDevice.Use();
                    MoveActiveMixersTo(previous.MixerDevice);
                    transport.Deactivate();
                    transport.Dispose();
                    _bufferLength = restoreBufferLength;
                    if (!_audioOutput.AttachBackend(previous.Backend, previous.BassDeviceId))
                    {
                        YargLogger.LogFormatError("Failed to restore audio output '{0}'",
                            previous.Descriptor.DisplayName);
                    }
                    UpdatePlaybackLatency();
                    ReloadSamples(venueSamples);
                    return false;
                }

                previous.ReinitializeRequested -= OnTransportReinitializeRequested;
                _currentTransport = transport;
                previous.Deactivate();
                previous.Dispose();
                transport.ReinitializeRequested += OnTransportReinitializeRequested;
                transport.BassMixerDevice.Use();
            }
            else
            {
                if (!transport.Activate(new AudioTransportConfiguration(_bufferLength)))
                {
                    transport.Dispose();
                    return false;
                }
                _currentTransport = transport;
                transport.BassMixerDevice.Use();
                if (!_audioOutput.AttachBackend(transport.Backend, transport.BassDeviceId))
                {
                    _currentTransport = null;
                    transport.Deactivate();
                    transport.Dispose();
                    return false;
                }
                transport.ReinitializeRequested += OnTransportReinitializeRequested;
            }

            UpdatePlaybackLatency();

            YargLogger.LogFormatInfo("Current audio output: {0}", name);

            ReloadSamples(venueSamples);
            return true;
        }

        protected override OutputBufferInfo? GetOutputBufferInfo() => _currentTransport?.GetBufferInfo();

        protected override bool OpenOutputControlPanel() => _currentTransport?.OpenControlPanel() ?? false;

        protected override AudioOutputBackend GetOutputBackend(string name) => BassAudioTransport.GetBackend(name);

        protected override bool ReinitializeOutput(int bufferLength)
        {
            if (_currentTransport == null || bufferLength < 0)
            {
                return false;
            }

            int previousBufferLength = _bufferLength;
            _bufferLength = bufferLength;
            if (ApplyOutputDevice(_currentTransport.Descriptor.DisplayName, previousBufferLength))
            {
                return true;
            }

            _bufferLength = previousBufferLength;
            return false;
        }

        private void OnTransportReinitializeRequested()
        {
            if (_currentTransport == null)
            {
                return;
            }

            if (!ReinitializeOutput(_bufferLength))
            {
                YargLogger.LogError("Failed to reinitialize audio after driver settings changed");
                ToastManager.ToastError("Failed to reinitialize audio after driver settings changed.");
            }
        }

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
            return BassStemMixer.Create(name, this, speed, mixerVolume, handle, clampStemVolume: clampStemVolume,
                normalize: normalize, outputChannel: CreateOutputChannel(SettingsManager.Settings?.OutputChannelDefault.Value ?? 0));
        }

        internal BassSongPlayback CreateSongPlayback(int tempoStreamHandle)
        {
            return _audioOutput.CreateSongPlayback(tempoStreamHandle);
        }

#nullable enable
        protected override MicDevice? GetInputDevice(string name) => _currentTransport?.CreateInputByName(name);
#nullable disable

        protected override List<(int id, string name)> GetAllInputDevices()
        {
            var mics = new List<(int id, string name)>();
            if (_currentTransport == null)
            {
                return mics;
            }

            foreach (var descriptor in _currentTransport.GetInputs())
            {
                mics.Add((descriptor.ChannelId, descriptor.DisplayName));
            }
            return mics;
        }

#nullable enable
        protected override MicDevice? CreateInputDevice(int deviceId, string name) =>
            _currentTransport?.CreateInputByChannel(deviceId, name);
#nullable disable

#nullable enable
        protected override OutputChannel? CreateOutputChannel(int channelId)
#nullable disable
        {
            return BassOutputChannel.Create(channelId);
        }

        protected override List<(int id, string name)> GetAllOutputDevices()
        {
            var devices = BassSharedAudioTransport.EnumerateDevices();
            devices.AddRange(BassAsioAudioTransport.EnumerateDevices());
            return devices;
        }

        protected override int GetOutputChannelCount()
        {
            return BassHelpers.GetOutputChannelCount();
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
            _currentTransport?.Deactivate();
            _currentTransport?.Dispose();
            _currentTransport = null;
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
            streamHandle = BassX.Stream.CreateSourceUnchecked(stream);
            if (streamHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create source stream: {0}!", Bass.LastError);
                return false;
            }
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
