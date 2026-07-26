using System;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEditor;
using UnityEngine;
using YARG.Audio.BASS;

namespace YARG.Editor
{
    /// <summary>
    /// Standalone full-duplex ASIO test. Captures one ASIO input into a BASS push stream,
    /// mixes it to stereo, then returns it to an ASIO output pair.
    /// </summary>
    internal sealed class AsioMicMonitorTestTab
    {
        private const double STATUS_UPDATE_INTERVAL = 0.1;
        private const float MAX_MONITOR_GAIN = 4f;

        private readonly Action _repaint;
        private readonly AsioProcedure _outputCallback;

        private string[] _deviceNames = Array.Empty<string>();
        private string[] _inputNames = Array.Empty<string>();
        private string[] _outputPairNames = Array.Empty<string>();
        private int[] _outputPairChannels = Array.Empty<int>();
        private int _device;
        private int _inputChannel;
        private int _outputPair;
        private int _sampleRate = 48000;
        private int _bufferLength;
        private float _monitorVolume;
        private bool _headphonesConfirmed;
        private bool _ownsAsio;
        private bool _ownsBass;
        private bool _running;
        private int _inputStreamHandle;
        private int _masterMixerHandle;
        private BassFreeverbDsp _reverbDsp;
        private int _inputLatencyFrames;
        private int _outputLatencyFrames;
        private double _inputLevel;
        private double _displayedInputLevel;
        private double _asioCpuUsage;
        private double _nextStatusUpdate;
        private AsioInfo _asioInfo;
        private string _status = "Select an ASIO driver, then open it.";
        private Vector2 _scrollPosition;

        public AsioMicMonitorTestTab(Action repaint)
        {
            _repaint = repaint;
            _outputCallback = FillOutputBuffer;
        }

        public void Enable()
        {
            RefreshDevices();
            EditorApplication.update += OnEditorUpdate;
        }

        public void Disable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CloseDriver();
        }

        public void Draw()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

#if UNITY_EDITOR_WIN
            EditorGUILayout.HelpBox(
                "Captures a physical ASIO input, sends it through a BASS master mixer, then " +
                "outputs it through the same ASIO driver. This owns standalone BASS and ASIO " +
                "devices and cannot run during play mode or alongside another audio test.",
                MessageType.Info);

            EditorGUILayout.HelpBox(
                "Use headphones. Open speakers can create immediate loud feedback. Monitoring " +
                "starts muted; raise Monitor volume slowly after confirming the input and output.",
                MessageType.Warning);

            DrawDriverControls();
            if (_ownsAsio)
            {
                DrawChannelControls();
                DrawMonitoringControls();
            }

            EditorGUILayout.Space();
            DrawResults();
#else
            EditorGUILayout.HelpBox("ASIO monitoring is only available in Windows editor.",
                MessageType.Info);
#endif

            EditorGUILayout.EndScrollView();
        }

#if UNITY_EDITOR_WIN
        private void DrawDriverControls()
        {
            EditorGUILayout.LabelField("Driver", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_ownsAsio || _deviceNames.Length == 0))
                {
                    _device = EditorGUILayout.Popup("ASIO driver", _device, _deviceNames);
                }

                using (new EditorGUI.DisabledScope(_ownsAsio || _running))
                {
                    if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    {
                        RefreshDevices();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_ownsAsio || _deviceNames.Length == 0))
                {
                    if (GUILayout.Button("Open Driver"))
                    {
                        OpenDriver();
                    }
                }

                using (new EditorGUI.DisabledScope(!_ownsAsio))
                {
                    if (GUILayout.Button("Control Panel"))
                    {
                        OpenControlPanel();
                    }

                    if (GUILayout.Button("Close Driver"))
                    {
                        CloseDriver();
                    }
                }
            }

            if (_deviceNames.Length == 0)
            {
                EditorGUILayout.HelpBox("No ASIO drivers found.", MessageType.Warning);
            }
        }

        private void DrawChannelControls()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Route", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_running))
            {
                if (_inputNames.Length > 0)
                {
                    _inputChannel = EditorGUILayout.Popup("Input channel", _inputChannel, _inputNames);
                }
                else
                {
                    EditorGUILayout.HelpBox("Driver exposes no input channels.", MessageType.Error);
                }

                if (_outputPairNames.Length > 0)
                {
                    _outputPair = EditorGUILayout.Popup("Output pair", _outputPair, _outputPairNames);
                }
                else
                {
                    EditorGUILayout.HelpBox("Driver exposes fewer than two output channels.",
                        MessageType.Error);
                }

                _sampleRate = Math.Max(1, EditorGUILayout.IntField("Sample rate", _sampleRate));
                _bufferLength = Math.Max(0,
                    EditorGUILayout.IntField("Buffer length (frames)", _bufferLength));
            }

            EditorGUILayout.LabelField(
                $"Driver buffer: min {_asioInfo.MinBufferLength}, max {_asioInfo.MaxBufferLength}, " +
                $"preferred {_asioInfo.PreferredBufferLength}, granularity " +
                $"{_asioInfo.BufferLengthGranularity}");
            EditorGUILayout.LabelField("Buffer value 0 uses driver's current/default length.");
        }

        private void DrawMonitoringControls()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Monitoring", EditorStyles.boldLabel);

            _headphonesConfirmed = EditorGUILayout.ToggleLeft(
                "I am using headphones or have otherwise prevented acoustic feedback",
                _headphonesConfirmed);

            EditorGUI.BeginChangeCheck();
            _monitorVolume = EditorGUILayout.Slider(
                new GUIContent("Monitor gain", "1.0 is unity gain; values above 1 amplify input."),
                _monitorVolume, 0f, MAX_MONITOR_GAIN);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyMonitorVolume();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_running || !_headphonesConfirmed ||
                           _inputNames.Length == 0 || _outputPairNames.Length == 0))
                {
                    if (GUILayout.Button("Start Monitoring"))
                    {
                        StartMonitoring();
                    }
                }

                using (new EditorGUI.DisabledScope(!_running))
                {
                    if (GUILayout.Button("Mute"))
                    {
                        _monitorVolume = 0;
                        ApplyMonitorVolume();
                    }

                    if (GUILayout.Button("Stop"))
                    {
                        StopMonitoring();
                    }
                }
            }
        }

        private void DrawResults()
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, _status.StartsWith("Error:")
                ? MessageType.Error
                : MessageType.None);

            EditorGUILayout.HelpBox(
                "Monitor gain uses linear scaling. Input peak is measured before monitor gain; " +
                "lower ASIO input levels may need gain above 1. Watch for clipping.",
                MessageType.None);

            EditorGUILayout.HelpBox(
                "FX chain: 110 Hz high-pass, 300 Hz mud cut, 3.2 kHz presence boost, " +
                "4:1 compressor, light Freeverb.",
                MessageType.None);

            DrawInputLevelMeter();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Driver open", _ownsAsio);
                EditorGUILayout.Toggle("Monitoring", _running);
                EditorGUILayout.DoubleField("Input peak", _inputLevel);
                EditorGUILayout.DoubleField("ASIO CPU usage (%)", _asioCpuUsage);
                EditorGUILayout.IntField("Input latency (frames)", _inputLatencyFrames);
                EditorGUILayout.IntField("Output latency (frames)", _outputLatencyFrames);
                EditorGUILayout.DoubleField("Input latency (ms)",
                    FramesToMilliseconds(_inputLatencyFrames));
                EditorGUILayout.DoubleField("Output latency (ms)",
                    FramesToMilliseconds(_outputLatencyFrames));
                EditorGUILayout.DoubleField("Reported round-trip latency (ms)",
                    FramesToMilliseconds(_inputLatencyFrames + _outputLatencyFrames));
            }

            if (_inputLevel >= 1)
            {
                EditorGUILayout.HelpBox("Input is clipping.", MessageType.Warning);
            }
        }

        private void DrawInputLevelMeter()
        {
            EditorGUILayout.LabelField($"Input level: {_displayedInputLevel:P1}");
            Rect meter = GUILayoutUtility.GetRect(18, 18, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(meter, new Color(0.12f, 0.12f, 0.12f));

            float level = Mathf.Clamp01((float) _displayedInputLevel);
            float greenEnd = Mathf.Min(level, 0.75f);
            float yellowEnd = Mathf.Min(level, 0.9f);
            DrawMeterSegment(meter, 0, greenEnd, new Color(0.15f, 0.75f, 0.2f));
            DrawMeterSegment(meter, 0.75f, yellowEnd, new Color(0.9f, 0.75f, 0.1f));
            DrawMeterSegment(meter, 0.9f, level, new Color(0.9f, 0.15f, 0.1f));
        }

        private static void DrawMeterSegment(Rect meter, float start, float end, Color color)
        {
            if (end <= start)
            {
                return;
            }

            EditorGUI.DrawRect(new Rect(
                meter.x + meter.width * start,
                meter.y,
                meter.width * (end - start),
                meter.height), color);
        }

        private void RefreshDevices()
        {
            if (_ownsAsio)
            {
                return;
            }

            try
            {
                int count = BassAsio.DeviceCount;
                _deviceNames = new string[count];
                for (int i = 0; i < count; i++)
                {
                    _deviceNames[i] = BassAsio.GetDeviceInfo(i).Name;
                }
                _device = Mathf.Clamp(_device, 0, Math.Max(0, count - 1));
                _status = count > 0
                    ? "Select an ASIO driver, then open it."
                    : "No ASIO drivers found.";
            }
            catch (Exception exception)
            {
                _deviceNames = Array.Empty<string>();
                SetError($"Failed to enumerate ASIO drivers: {exception.Message}");
            }
        }

        private void OpenDriver()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SetError("Exit play mode before opening the ASIO driver.");
                return;
            }
            if (_deviceNames.Length == 0)
            {
                SetError("No ASIO driver is selected.");
                return;
            }

            try
            {
                if (!BassAsio.Init(_device, AsioInitFlags.Thread))
                {
                    SetAsioError("Failed to initialize ASIO driver");
                    return;
                }
                _ownsAsio = true;
                BassAsio.CurrentDevice = _device;
                _asioInfo = BassAsio.Info;

                _inputNames = new string[_asioInfo.Inputs];
                for (int i = 0; i < _inputNames.Length; i++)
                {
                    var info = BassAsio.ChannelGetInfo(true, i);
                    _inputNames[i] = $"{i}: {info.Name} ({info.Format})";
                }

                int pairCount = _asioInfo.Outputs / 2;
                _outputPairNames = new string[pairCount];
                _outputPairChannels = new int[pairCount];
                for (int pair = 0; pair < pairCount; pair++)
                {
                    int left = pair * 2;
                    int right = left + 1;
                    var leftInfo = BassAsio.ChannelGetInfo(false, left);
                    var rightInfo = BassAsio.ChannelGetInfo(false, right);
                    _outputPairChannels[pair] = left;
                    _outputPairNames[pair] =
                        $"{left}/{right}: {leftInfo.Name} + {rightInfo.Name}";
                }

                _inputChannel = Mathf.Clamp(_inputChannel, 0,
                    Math.Max(0, _inputNames.Length - 1));
                _outputPair = Mathf.Clamp(_outputPair, 0,
                    Math.Max(0, _outputPairNames.Length - 1));

                int driverRate = (int) Math.Round(BassAsio.Rate);
                if (driverRate > 0)
                {
                    _sampleRate = driverRate;
                }
                _bufferLength = 0;
                _status = $"Opened ASIO driver '{_deviceNames[_device]}'. Select route and start monitoring.";
            }
            catch (Exception exception)
            {
                SetError($"Failed to open ASIO driver: {exception.Message}");
                CloseDriver();
            }
        }

        private void OpenControlPanel()
        {
            try
            {
                BassAsio.CurrentDevice = _device;
                if (!BassAsio.ControlPanel())
                {
                    SetAsioError("Failed to open ASIO control panel");
                }
            }
            catch (Exception exception)
            {
                SetError($"Failed to open ASIO control panel: {exception.Message}");
            }
        }

        private void StartMonitoring()
        {
            if (!_ownsAsio || _running)
            {
                return;
            }
            if (!_headphonesConfirmed)
            {
                SetError("Confirm feedback protection before starting monitoring.");
                return;
            }
            if (Bass.CurrentDevice != -1)
            {
                SetError("BASS is already initialized. Stop play mode or another audio test first.");
                return;
            }

            try
            {
                BassAsio.CurrentDevice = _device;
                if (!BassAsio.CheckRate(_sampleRate))
                {
                    SetAsioError($"ASIO driver does not support {_sampleRate} Hz");
                    return;
                }
                BassAsio.Rate = _sampleRate;

                if (!Bass.Init(0, _sampleRate, DeviceInitFlags.Default, IntPtr.Zero))
                {
                    SetBassError("Failed to initialize BASS no-sound device");
                    return;
                }
                _ownsBass = true;

                _inputStreamHandle = Bass.CreateStream(_sampleRate, 1,
                    BassFlags.Float | BassFlags.Decode, StreamProcedureType.Push);
                if (_inputStreamHandle == 0)
                {
                    SetBassError("Failed to create ASIO input push stream");
                    StopMonitoring();
                    return;
                }

                if (!ConfigureMonitoringEffects())
                {
                    SetBassError("Failed to configure monitoring effects");
                    StopMonitoring();
                    return;
                }

                _masterMixerHandle = BassMix.CreateMixerStream(_sampleRate, 2,
                    BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);
                if (_masterMixerHandle == 0)
                {
                    SetBassError("Failed to create monitoring master mixer");
                    StopMonitoring();
                    return;
                }

                if (!BassMix.MixerAddChannel(_masterMixerHandle, _inputStreamHandle,
                        BassFlags.MixerChanNoRampin))
                {
                    SetBassError("Failed to add ASIO input to monitoring mixer");
                    StopMonitoring();
                    return;
                }

                // Never carry a pre-start slider value into a newly opened live mic route.
                _monitorVolume = 0;
                ApplyMonitorVolume();

                if (!BassAsio.ChannelEnableBass(true, _inputChannel, _inputStreamHandle,
                        Join: false))
                {
                    SetAsioError("Failed to route ASIO input into BASS");
                    StopMonitoring();
                    return;
                }

                int outputChannel = _outputPairChannels[_outputPair];
                if (!BassAsio.ChannelEnable(false, outputChannel, _outputCallback, IntPtr.Zero) ||
                    !BassAsio.ChannelJoin(false, outputChannel + 1, outputChannel) ||
                    !BassAsio.ChannelSetFormat(false, outputChannel, AsioSampleFormat.Float) ||
                    !BassAsio.ChannelSetRate(false, outputChannel, _sampleRate))
                {
                    SetAsioError("Failed to configure ASIO monitoring output");
                    StopMonitoring();
                    return;
                }

                if (!BassAsio.Start(_bufferLength, 0))
                {
                    SetAsioError("Failed to start ASIO monitoring");
                    StopMonitoring();
                    return;
                }

                _running = true;
                _inputLatencyFrames = Math.Max(0, BassAsio.GetLatency(true));
                _outputLatencyFrames = Math.Max(0, BassAsio.GetLatency(false));
                _status = "Monitoring active. Raise Monitor volume slowly.";
            }
            catch (Exception exception)
            {
                SetError($"Failed to start ASIO monitoring: {exception.Message}");
                StopMonitoring();
            }
        }

        private bool ConfigureMonitoringEffects()
        {
            // Keep analysis out of this path: these effects only shape monitor playback.
            var highPass = new BQFParameters
            {
                lFilter = BQFType.HighPass,
                fCenter = 110f,
                fQ = 0.707f,
                lChannel = FXChannelFlags.All,
            };
            if (BassHelpers.FXAddParameters(_inputStreamHandle, EffectType.BQF, highPass, 0) == 0)
            {
                return false;
            }

            var mudCut = new PeakEQParameters
            {
                fBandwidth = 1f,
                fCenter = 300f,
                fGain = -4f,
                lChannel = FXChannelFlags.All,
            };
            if (BassHelpers.FXAddParameters(_inputStreamHandle, EffectType.PeakEQ, mudCut, 1) == 0)
            {
                return false;
            }

            var presenceBoost = new PeakEQParameters
            {
                fBandwidth = 1f,
                fCenter = 3200f,
                fGain = 1f,
                lChannel = FXChannelFlags.All,
            };
            if (BassHelpers.FXAddParameters(_inputStreamHandle, EffectType.PeakEQ,
                    presenceBoost, 2) == 0)
            {
                return false;
            }

            var compressor = new CompressorParameters
            {
                fGain = 0f,
                fThreshold = -19f,
                fAttack = 10f,
                fRelease = 100f,
                fRatio = 4f,
                lChannel = FXChannelFlags.All,
            };
            if (BassHelpers.FXAddParameters(_inputStreamHandle, EffectType.Compressor,
                    compressor, 3) == 0)
            {
                return false;
            }

            _reverbDsp = BassFreeverbDsp.Create(_inputStreamHandle,
                dryMix: 1f,
                wetMix: 0.15f,
                roomSize: 0.4f,
                damp: 0.8f,
                width: 1f,
                priority: 4);
            return _reverbDsp != null;
        }

        private int FillOutputBuffer(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            int mixer = _masterMixerHandle;
            if (mixer == 0)
            {
                return 0;
            }

            int bytesRead = Bass.ChannelGetData(mixer, buffer, length);
            return bytesRead < 0 ? 0 : bytesRead;
        }

        private void ApplyMonitorVolume()
        {
            if (_inputStreamHandle != 0 &&
                !Bass.ChannelSetAttribute(_inputStreamHandle, ChannelAttribute.Volume,
                    _monitorVolume))
            {
                SetBassError("Failed to set monitor volume");
            }
        }

        private void StopMonitoring()
        {
            if (_ownsAsio)
            {
                try
                {
                    BassAsio.CurrentDevice = _device;
                    if (BassAsio.IsStarted)
                    {
                        BassAsio.Stop();
                    }

                    var resetFlags = AsioChannelResetFlags.Enable |
                        AsioChannelResetFlags.Join |
                        AsioChannelResetFlags.Format |
                        AsioChannelResetFlags.Rate |
                        AsioChannelResetFlags.Volume;
                    BassAsio.ChannelReset(true, -1, resetFlags);
                    BassAsio.ChannelReset(false, -1, resetFlags);
                }
                catch (Exception exception)
                {
                    SetError($"Failed to stop ASIO monitoring cleanly: {exception.Message}");
                }
            }

            _running = false;
            _reverbDsp?.Dispose();
            _reverbDsp = null;
            _masterMixerHandle = FreeStream(_masterMixerHandle);
            _inputStreamHandle = FreeStream(_inputStreamHandle);
            if (_ownsBass)
            {
                Bass.CurrentDevice = 0;
                Bass.Free();
                _ownsBass = false;
            }

            _inputLatencyFrames = 0;
            _outputLatencyFrames = 0;
            _inputLevel = 0;
            _displayedInputLevel = 0;
            _asioCpuUsage = 0;
            if (!_status.StartsWith("Error:"))
            {
                _status = _ownsAsio
                    ? "Monitoring stopped. Driver remains open."
                    : "Monitoring stopped.";
            }
        }

        private void CloseDriver()
        {
            StopMonitoring();
            if (_ownsAsio)
            {
                try
                {
                    BassAsio.CurrentDevice = _device;
                    BassAsio.Free();
                }
                catch (Exception exception)
                {
                    SetError($"Failed to free ASIO driver: {exception.Message}");
                }
                _ownsAsio = false;
            }

            _inputNames = Array.Empty<string>();
            _outputPairNames = Array.Empty<string>();
            _outputPairChannels = Array.Empty<int>();
            if (!_status.StartsWith("Error:"))
            {
                _status = "ASIO driver closed.";
            }
        }

        private void OnEditorUpdate()
        {
            if (!_running || EditorApplication.timeSinceStartup < _nextStatusUpdate)
            {
                return;
            }
            _nextStatusUpdate = EditorApplication.timeSinceStartup + STATUS_UPDATE_INTERVAL;

            try
            {
                BassAsio.CurrentDevice = _device;
                _inputLevel = BassAsio.ChannelGetLevel(true, _inputChannel);
                if (_inputLevel >= 0)
                {
                    _displayedInputLevel = Math.Max(_inputLevel, _displayedInputLevel * 0.82);
                }
                _asioCpuUsage = BassAsio.CPUUsage;
                _repaint();
            }
            catch (Exception exception)
            {
                SetError($"Failed to update ASIO monitoring status: {exception.Message}");
            }
        }

        private double FramesToMilliseconds(int frames)
        {
            return _sampleRate > 0 ? frames * 1000.0 / _sampleRate : 0;
        }

        private static int FreeStream(int handle)
        {
            if (handle != 0)
            {
                Bass.StreamFree(handle);
            }
            return 0;
        }

        private void SetBassError(string message)
        {
            SetError($"{message}: {Bass.LastError}");
        }

        private void SetAsioError(string message)
        {
            SetError($"{message}: {BassAsio.LastError}");
        }

        private void SetError(string message)
        {
            _status = $"Error: {message}";
            UnityEngine.Debug.LogError($"ASIO mic monitor test: {message}");
            _repaint();
        }
#endif
    }
}
