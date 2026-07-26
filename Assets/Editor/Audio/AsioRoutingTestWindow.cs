using System;
using System.Collections.Generic;
using System.Threading;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Mix;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace YARG.Editor
{
    /// <summary>
    /// Exercises the runtime ASIO input shape: every ASIO input is enabled before Start, while
    /// route selection attaches/detaches mixer inputs live. This intentionally owns standalone
    /// BASS and ASIO and cannot run alongside play mode or another audio test.
    /// </summary>
    internal sealed class AsioRoutingTestTab
    {
        private enum CaptureTransport
        {
            ChannelEnableBass,
            CustomCallback,
        }

        private enum DetachedMonitorPolicy
        {
            NoMaintenance,
            DrainOnEditorUpdate,
            ResetBeforeAttach,
        }

        private const double STATUS_UPDATE_INTERVAL = 0.1;
        private const float MAX_ROUTE_GAIN = 4f;
        private const int DRAIN_BUFFER_BYTES = 4096;
        private const int CALLBACK_GAP_WARNING_MS = 50;

        private readonly Action _repaint;
        private readonly AsioProcedure _outputCallback;
        private readonly AsioProcedure _inputCallback;
        private readonly List<int> _inputStreams = new();
        private readonly List<int> _pumpStreams = new();
        private readonly List<int> _analysisStreams = new();
        private readonly List<int> _monitorStreams = new();

        private string[] _deviceNames = Array.Empty<string>();
        private string[] _inputNames = Array.Empty<string>();
        private string[] _outputPairNames = Array.Empty<string>();
        private int[] _outputPairChannels = Array.Empty<int>();
        private bool[] _routes = Array.Empty<bool>();
        private bool[] _attachedRoutes = Array.Empty<bool>();
        private double[] _inputLevels = Array.Empty<double>();
        private int _device;
        private int _outputPair;
        private int _sampleRate = 48000;
        private int _bufferLength;
        private int _splitBufferLength = 2000;
        private float _routeGain = 1f;
        private CaptureTransport _captureTransport;
        private DetachedMonitorPolicy _detachedMonitorPolicy =
            DetachedMonitorPolicy.ResetBeforeAttach;
        private bool _headphonesConfirmed;
        private bool _asioAlreadyInitialized;
        private bool _ownsAsio;
        private bool _ownsBass;
        private bool _running;
        private int _masterMixerHandle;
        private int _inputLatencyFrames;
        private int _outputLatencyFrames;
        private int _startCount;
        private long _outputCallbackCount;
        private long _lastOutputCallbackTimestamp;
        private long _maxCallbackGapTicks;
        private int _callbackGapCount;
        private int _routeMutationCount;
        private int _routeMutationErrors;
        private int _telemetryErrors;
        private int[] _callbackInputStreams = Array.Empty<int>();
        private long[] _inputCallbackCounts = Array.Empty<long>();
        private long[] _inputCallbackFrames = Array.Empty<long>();
        private long[] _firstInputCallbackTimestamps = Array.Empty<long>();
        private long[] _lastInputCallbackTimestamps = Array.Empty<long>();
        private long[] _maxInputCallbackGapTicks = Array.Empty<long>();
        private int[] _lastInputCallbackFrames = Array.Empty<int>();
        private int[] _inputCallbackGapCounts = Array.Empty<int>();
        private int[] _inputCallbackErrors = Array.Empty<int>();
        private long[] _analysisBytes = Array.Empty<long>();
        private double[] _analysisLevels = Array.Empty<double>();
        private int[] _rootQueuedBytes = Array.Empty<int>();
        private int[] _splitBufferedBytes = Array.Empty<int>();
        private int[] _analysisLagBytes = Array.Empty<int>();
        private int[] _monitorLagBytes = Array.Empty<int>();
        private int[] _maxMonitorLagBytes = Array.Empty<int>();
        private int[] _monitorBufferLimitHits = Array.Empty<int>();
        private bool[] _monitorAtBufferLimit = Array.Empty<bool>();
        private double _asioCpuUsage;
        private double _nextStatusUpdate;
        private string _status = "Select an ASIO driver, then open it.";
        private Vector2 _scrollPosition;

        public AsioRoutingTestTab(Action repaint)
        {
            _repaint = repaint;
            _outputCallback = FillOutputBuffer;
            _inputCallback = CaptureInputBuffer;
        }

        public void Enable()
        {
#if UNITY_EDITOR_WIN
            RefreshDevices();
            EditorApplication.update += OnEditorUpdate;
#endif
        }

        public void Disable()
        {
#if UNITY_EDITOR_WIN
            EditorApplication.update -= OnEditorUpdate;
            CloseDriver();
#endif
        }

        public void Draw()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            try
            {
                EditorGUILayout.HelpBox(
                    "Pre-enables every ASIO input before driver start. Each input feeds an analysis " +
                    "splitter and an independently attached monitor splitter. Route changes never " +
                    "restart ASIO. This tests routing mechanics, not game profile creation.",
                    MessageType.Info);

                EditorGUILayout.HelpBox(
                    "Use headphones. Monitoring starts muted only when route gain is zero. Raise gain " +
                    "slowly after confirming feedback protection.", MessageType.Warning);

#if UNITY_EDITOR_WIN
                DrawDriverControls();
                if (_ownsAsio)
                {
                    DrawRouteControls();
                    DrawResults();
                }
                else
                {
                    EditorGUILayout.HelpBox(_status, MessageType.None);
                }
#else
                EditorGUILayout.HelpBox("ASIO routing is only available in Windows editor.",
                    MessageType.Info);
#endif
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
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

            if (_asioAlreadyInitialized && !_ownsAsio)
            {
                EditorGUILayout.HelpBox(
                    "BASSASIO reports this device is already initialized inside Unity process. " +
                    "This is usually a hidden Audio Tests tab or state left across script reload. " +
                    "Release only after confirming play mode and other audio tests are stopped.",
                    MessageType.Warning);
                if (GUILayout.Button("Release Existing ASIO Instance"))
                {
                    ReleaseExistingAsioInstance();
                }
            }
        }

        private void DrawRouteControls()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Routes", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_running))
            {
                if (_outputPairNames.Length > 0)
                {
                    _outputPair = EditorGUILayout.Popup("Output pair", _outputPair,
                        _outputPairNames);
                }

                _sampleRate = Math.Max(1, EditorGUILayout.IntField("Sample rate", _sampleRate));
                _bufferLength = Math.Max(0,
                    EditorGUILayout.IntField("Buffer length (frames)", _bufferLength));
                _splitBufferLength = Mathf.Clamp(
                    EditorGUILayout.IntField("Split buffer (ms)", _splitBufferLength), 100, 5000);
                _captureTransport = (CaptureTransport) EditorGUILayout.EnumPopup(
                    "Capture transport", _captureTransport);
            }

            _detachedMonitorPolicy = (DetachedMonitorPolicy) EditorGUILayout.EnumPopup(
                "Detached monitor policy", _detachedMonitorPolicy);

            if (_captureTransport == CaptureTransport.CustomCallback)
            {
                EditorGUILayout.HelpBox(
                    "Custom callback timestamps each ASIO input buffer with QPC, then pushes its " +
                    "float samples into the same root stream used by ChannelEnableBass mode.",
                    MessageType.None);
            }

            if (_detachedMonitorPolicy == DetachedMonitorPolicy.NoMaintenance)
            {
                EditorGUILayout.HelpBox(
                    "No maintenance intentionally lets detached monitor splitters lag. Reattaching " +
                    "may play stale data or expose splitter overflow behavior.", MessageType.Warning);
            }

            EditorGUILayout.LabelField(
                $"Input channels: {_inputNames.Length}; selected routes: {SelectedRouteCount()}");

            EditorGUI.BeginChangeCheck();
            _routeGain = EditorGUILayout.Slider("Route gain", _routeGain, 0f, MAX_ROUTE_GAIN);
            if (EditorGUI.EndChangeCheck() && _running)
            {
                ApplyRouteGains();
            }

            for (int i = 0; i < _routes.Length; i++)
            {
                EditorGUI.BeginChangeCheck();
                _routes[i] = EditorGUILayout.ToggleLeft(_inputNames[i], _routes[i]);
                if (EditorGUI.EndChangeCheck() && _running)
                {
                    if (!SetRouteAttached(i, _routes[i]))
                    {
                        _routes[i] = !_routes[i];
                    }
                }
            }

            _headphonesConfirmed = EditorGUILayout.ToggleLeft(
                "I am using headphones or have otherwise prevented acoustic feedback",
                _headphonesConfirmed);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_running || !_headphonesConfirmed ||
                           _inputNames.Length == 0 || _outputPairNames.Length == 0))
                {
                    if (GUILayout.Button("Start Routing"))
                    {
                        StartRouting();
                    }
                }

                using (new EditorGUI.DisabledScope(!_running))
                {
                    if (GUILayout.Button("Stop"))
                    {
                        StopRouting();
                    }
                }
            }

            using (new EditorGUI.DisabledScope(!_running))
            {
                if (GUILayout.Button("Reset Detached Monitor Branches"))
                {
                    ResetDetachedMonitorStreams();
                }
            }
        }

        private void DrawResults()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, _status.StartsWith("Error:")
                ? MessageType.Error
                : MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Driver open", _ownsAsio);
                EditorGUILayout.Toggle("Routing active", _running);
                EditorGUILayout.IntField("ASIO start count", _startCount);
                EditorGUILayout.LongField("Output callbacks", Interlocked.Read(ref _outputCallbackCount));
                EditorGUILayout.IntField("Selected routes", SelectedRouteCount());
                EditorGUILayout.IntField("Attached routes", AttachedRouteCount());
                EditorGUILayout.IntField("Route mutations", _routeMutationCount);
                EditorGUILayout.IntField("Route mutation errors", _routeMutationErrors);
                EditorGUILayout.IntField("Telemetry errors", _telemetryErrors);
                EditorGUILayout.DoubleField("Max callback gap (ms)",
                    Interlocked.Read(ref _maxCallbackGapTicks) * 1000.0 / Stopwatch.Frequency);
                EditorGUILayout.IntField($"Callback gaps > {CALLBACK_GAP_WARNING_MS} ms",
                    _callbackGapCount);
                EditorGUILayout.IntField("Input latency (frames)", _inputLatencyFrames);
                EditorGUILayout.IntField("Output latency (frames)", _outputLatencyFrames);
                EditorGUILayout.IntField("Split buffer (ms)", BassMix.SplitBufferLength);
                EditorGUILayout.DoubleField("ASIO CPU usage (%)", _asioCpuUsage);
            }

            for (int i = 0; i < _inputLevels.Length; i++)
            {
                DrawInputResults(i);
            }

            EditorGUILayout.HelpBox(
                "Expected: analysis peak continues while monitor is detached; route toggles leave " +
                "ASIO start count unchanged. Compare monitor lag after a long detach using each " +
                "policy. Reset-before-attach should discard stale data without continuous drain.",
                MessageType.None);
        }

        private void DrawInputResults(int index)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(_inputNames[index], EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Monitor attached", index < _attachedRoutes.Length &&
                    _attachedRoutes[index]);
                EditorGUILayout.LabelField("Raw ASIO peak", $"{_inputLevels[index]:P1}");
                EditorGUILayout.LabelField("Analysis peak", $"{_analysisLevels[index]:P1}");
                EditorGUILayout.LongField("Analysis bytes", ReadArray(_analysisBytes, index));
                DrawByteTelemetry("Root push queue", ReadArray(_rootQueuedBytes, index));
                DrawByteTelemetry("Shared split buffer", ReadArray(_splitBufferedBytes, index));
                DrawByteTelemetry("Analysis lag", ReadArray(_analysisLagBytes, index));
                DrawByteTelemetry("Monitor lag", ReadArray(_monitorLagBytes, index));
                DrawByteTelemetry("Max monitor lag", ReadArray(_maxMonitorLagBytes, index));
                EditorGUILayout.IntField("Monitor buffer-limit hits",
                    ReadArray(_monitorBufferLimitHits, index));

                if (_captureTransport == CaptureTransport.CustomCallback)
                {
                    EditorGUILayout.LongField("Input callbacks",
                        Interlocked.Read(ref _inputCallbackCounts[index]));
                    EditorGUILayout.LongField("Captured frames",
                        Interlocked.Read(ref _inputCallbackFrames[index]));
                    EditorGUILayout.DoubleField("Max input callback gap (ms)",
                        Interlocked.Read(ref _maxInputCallbackGapTicks[index]) * 1000.0 /
                        Stopwatch.Frequency);
                    EditorGUILayout.IntField($"Input gaps > {CALLBACK_GAP_WARNING_MS} ms",
                        Volatile.Read(ref _inputCallbackGapCounts[index]));
                    EditorGUILayout.IntField("Input callback errors",
                        Volatile.Read(ref _inputCallbackErrors[index]));
                    EditorGUILayout.DoubleField("Input clock drift (ms)",
                        GetInputClockDriftMilliseconds(index));
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawByteTelemetry(string label, long bytes)
        {
            EditorGUILayout.LabelField(label,
                $"{bytes:N0} bytes ({BytesToMilliseconds(bytes):F2} ms)");
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
            if (EditorApplication.isPlayingOrWillChangePlaymode || _deviceNames.Length == 0)
            {
                SetError(EditorApplication.isPlayingOrWillChangePlaymode
                    ? "Exit play mode before opening the ASIO driver."
                    : "No ASIO driver is selected.");
                return;
            }

            try
            {
                _asioAlreadyInitialized = false;
                if (!BassAsio.Init(_device, AsioInitFlags.Thread))
                {
                    _asioAlreadyInitialized = BassAsio.LastError == Errors.Already;
                    SetAsioError("Failed to initialize ASIO driver");
                    return;
                }

                _ownsAsio = true;
                BassAsio.CurrentDevice = _device;
                AsioInfo info = BassAsio.Info;
                _inputNames = new string[info.Inputs];
                _inputLevels = new double[info.Inputs];
                _routes = new bool[info.Inputs];
                _attachedRoutes = new bool[info.Inputs];
                ResetInputMetrics(info.Inputs);
                for (int i = 0; i < info.Inputs; i++)
                {
                    var channelInfo = BassAsio.ChannelGetInfo(true, i);
                    _inputNames[i] = $"{i}: {channelInfo.Name} ({channelInfo.Format})";
                }

                int pairCount = info.Outputs / 2;
                _outputPairNames = new string[pairCount];
                _outputPairChannels = new int[pairCount];
                for (int pair = 0; pair < pairCount; pair++)
                {
                    int left = pair * 2;
                    var leftInfo = BassAsio.ChannelGetInfo(false, left);
                    var rightInfo = BassAsio.ChannelGetInfo(false, left + 1);
                    _outputPairChannels[pair] = left;
                    _outputPairNames[pair] =
                        $"{left}/{left + 1}: {leftInfo.Name} + {rightInfo.Name}";
                }

                _outputPair = Mathf.Clamp(_outputPair, 0,
                    Math.Max(0, _outputPairNames.Length - 1));
                _startCount = 0;
                Interlocked.Exchange(ref _outputCallbackCount, 0);
                int driverRate = (int) Math.Round(BassAsio.Rate);
                if (driverRate > 0)
                {
                    _sampleRate = driverRate;
                }
                _status = $"Opened ASIO driver '{_deviceNames[_device]}'.";
            }
            catch (Exception exception)
            {
                SetError($"Failed to open ASIO driver: {exception.Message}");
                CloseDriver();
            }
        }

        private void ReleaseExistingAsioInstance()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SetError("Exit play mode before releasing existing ASIO state.");
                return;
            }

            try
            {
                BassAsio.CurrentDevice = _device;
                if (BassAsio.IsStarted && !BassAsio.Stop())
                {
                    SetAsioError("Failed to stop existing ASIO instance");
                    return;
                }
                if (!BassAsio.Free())
                {
                    SetAsioError("Failed to release existing ASIO instance");
                    return;
                }

                _asioAlreadyInitialized = false;
                _status = "Released existing ASIO instance. Open driver again.";
                _repaint();
            }
            catch (Exception exception)
            {
                SetError($"Failed to release existing ASIO instance: {exception.Message}");
            }
        }

        private void StartRouting()
        {
            if (!_ownsAsio || _running || _outputPairNames.Length == 0)
            {
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

                BassMix.SplitBufferLength = _splitBufferLength;

                _masterMixerHandle = BassMix.CreateMixerStream(_sampleRate, 2,
                    BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);
                if (_masterMixerHandle == 0)
                {
                    SetBassError("Failed to create routing master mixer");
                    StopRouting();
                    return;
                }

                _attachedRoutes = new bool[_inputNames.Length];
                ResetInputMetrics(_inputNames.Length);
                var callbackStreams = new int[_inputNames.Length];
                for (int i = 0; i < _inputNames.Length; i++)
                {
                    int rootStream = Bass.CreateStream(_sampleRate, 1,
                        BassFlags.Float | BassFlags.Decode, StreamProcedureType.Push);
                    if (rootStream == 0)
                    {
                        SetBassError($"Failed to create input root {i}");
                        StopRouting();
                        return;
                    }

                    _inputStreams.Add(rootStream);
                    callbackStreams[i] = rootStream;

                    // Keep one non-slave splitter in the output mixer at zero volume. It advances
                    // the push root at ASIO output cadence, independently of editor update timing.
                    // Analysis and monitor are slaves so neither can pull the source ahead of the
                    // other and introduce a variable amount of monitoring latency.
                    int pumpStream = BassMix.CreateSplitStream(rootStream,
                        BassFlags.Decode | BassFlags.SplitPosition, null);
                    if (pumpStream == 0)
                    {
                        SetBassError($"Failed to create clock splitter {i}");
                        StopRouting();
                        return;
                    }
                    _pumpStreams.Add(pumpStream);

                    int analysisStream = BassMix.CreateSplitStream(rootStream,
                        BassFlags.Decode | BassFlags.SplitPosition | BassFlags.SplitSlave, null);
                    if (analysisStream == 0)
                    {
                        SetBassError($"Failed to create analysis splitter {i}");
                        StopRouting();
                        return;
                    }
                    _analysisStreams.Add(analysisStream);

                    int monitorStream = BassMix.CreateSplitStream(rootStream,
                        BassFlags.Decode | BassFlags.SplitPosition | BassFlags.SplitSlave, null);
                    if (monitorStream == 0)
                    {
                        SetBassError($"Failed to create monitor splitter {i}");
                        StopRouting();
                        return;
                    }
                    _monitorStreams.Add(monitorStream);
                }

                Volatile.Write(ref _callbackInputStreams, callbackStreams);
                for (int i = 0; i < _pumpStreams.Count; i++)
                {
                    if (!BassMix.MixerAddChannel(_masterMixerHandle, _pumpStreams[i],
                            BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin) ||
                        !Bass.ChannelSetAttribute(_pumpStreams[i], ChannelAttribute.Volume, 0))
                    {
                        SetBassError($"Failed to attach clock splitter {i}");
                        StopRouting();
                        return;
                    }
                }

                for (int i = 0; i < _inputNames.Length; i++)
                {
                    bool enabled = _captureTransport == CaptureTransport.ChannelEnableBass
                        ? BassAsio.ChannelEnableBass(true, i, _inputStreams[i], Join: false)
                        : BassAsio.ChannelEnable(true, i, _inputCallback, new IntPtr(i));
                    if (!enabled ||
                        !BassAsio.ChannelSetFormat(true, i, AsioSampleFormat.Float) ||
                        !BassAsio.ChannelSetRate(true, i, _sampleRate))
                    {
                        SetAsioError($"Failed to pre-enable ASIO input channel {i}");
                        StopRouting();
                        return;
                    }
                }

                if (!ApplyRouteMembership())
                {
                    StopRouting();
                    return;
                }

                int outputChannel = _outputPairChannels[_outputPair];
                if (!BassAsio.ChannelEnable(false, outputChannel, _outputCallback, IntPtr.Zero) ||
                    !BassAsio.ChannelJoin(false, outputChannel + 1, outputChannel) ||
                    !BassAsio.ChannelSetFormat(false, outputChannel, AsioSampleFormat.Float) ||
                    !BassAsio.ChannelSetRate(false, outputChannel, _sampleRate))
                {
                    SetAsioError("Failed to configure ASIO routing output");
                    StopRouting();
                    return;
                }

                if (!BassAsio.Start(_bufferLength, 0))
                {
                    SetAsioError("Failed to start ASIO routing");
                    StopRouting();
                    return;
                }

                _running = true;
                _startCount++;
                Interlocked.Exchange(ref _outputCallbackCount, 0);
                Interlocked.Exchange(ref _lastOutputCallbackTimestamp, 0);
                Interlocked.Exchange(ref _maxCallbackGapTicks, 0);
                Interlocked.Exchange(ref _callbackGapCount, 0);
                _routeMutationCount = 0;
                _routeMutationErrors = 0;
                _telemetryErrors = 0;
                _inputLatencyFrames = Math.Max(0, BassAsio.GetLatency(true));
                _outputLatencyFrames = Math.Max(0, BassAsio.GetLatency(false));
                _status = "Routing active. Toggle routes; ASIO must not restart.";
            }
            catch (Exception exception)
            {
                SetError($"Failed to start ASIO routing: {exception.Message}");
                StopRouting();
            }
        }

        private bool ApplyRouteMembership()
        {
            _routeGain = Mathf.Clamp(_routeGain, 0, MAX_ROUTE_GAIN);
            for (int i = 0; i < _monitorStreams.Count && i < _routes.Length; i++)
            {
                if (!SetRouteAttached(i, _routes[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private void ApplyRouteGains()
        {
            _routeGain = Mathf.Clamp(_routeGain, 0, MAX_ROUTE_GAIN);
            for (int i = 0; i < _monitorStreams.Count && i < _attachedRoutes.Length; i++)
            {
                if (_attachedRoutes[i])
                {
                    Bass.ChannelSetAttribute(_monitorStreams[i], ChannelAttribute.Volume,
                        _routeGain);
                }
            }
        }

        private bool SetRouteAttached(int index, bool attached)
        {
            if (index < 0 || index >= _monitorStreams.Count ||
                index >= _attachedRoutes.Length || _masterMixerHandle == 0)
            {
                return false;
            }

            if (_attachedRoutes[index] == attached)
            {
                return true;
            }

            int stream = _monitorStreams[index];
            if (attached)
            {
                if (_detachedMonitorPolicy == DetachedMonitorPolicy.ResetBeforeAttach &&
                    !BassMix.SplitStreamReset(stream, 0))
                {
                    _routeMutationErrors++;
                    SetBassError($"Failed to reset monitor splitter {index}");
                    return false;
                }

                if (!BassMix.MixerAddChannel(_masterMixerHandle, stream,
                        BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin))
                {
                    _routeMutationErrors++;
                    SetBassError($"Failed to attach input route {index}");
                    return false;
                }

                Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, _routeGain);
            }
            else if (!BassMix.MixerRemoveChannel(stream))
            {
                _routeMutationErrors++;
                SetBassError($"Failed to detach input route {index}");
                return false;
            }

            _attachedRoutes[index] = attached;
            if (!attached &&
                _detachedMonitorPolicy == DetachedMonitorPolicy.DrainOnEditorUpdate)
            {
                DrainStream(stream, false, index);
            }
            _routeMutationCount++;
            _status = attached
                ? $"Attached ASIO input route {index} without restarting ASIO."
                : $"Detached ASIO input route {index} without restarting ASIO.";
            return true;
        }

        private int FillOutputBuffer(bool input, int channel, IntPtr buffer, int length,
            IntPtr user)
        {
            int mixer = Volatile.Read(ref _masterMixerHandle);
            if (mixer == 0)
            {
                return 0;
            }

            long timestamp = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref _lastOutputCallbackTimestamp, timestamp);
            if (previous != 0)
            {
                long gap = timestamp - previous;
                UpdateMaxCallbackGap(gap);
                if (gap > Stopwatch.Frequency * CALLBACK_GAP_WARNING_MS / 1000)
                {
                    Interlocked.Increment(ref _callbackGapCount);
                }
            }

            Interlocked.Increment(ref _outputCallbackCount);
            int bytesRead = Bass.ChannelGetData(mixer, buffer, length);
            return bytesRead < 0 ? 0 : bytesRead;
        }

        private int CaptureInputBuffer(bool input, int channel, IntPtr buffer, int length,
            IntPtr user)
        {
            int index = (int) user.ToInt64();
            int[] streams = Volatile.Read(ref _callbackInputStreams);
            if (!input || index < 0 || index >= streams.Length || streams[index] == 0)
            {
                return 0;
            }

            long timestamp = Stopwatch.GetTimestamp();
            Interlocked.CompareExchange(ref _firstInputCallbackTimestamps[index], timestamp, 0);
            long previous = Interlocked.Exchange(ref _lastInputCallbackTimestamps[index],
                timestamp);
            if (previous != 0)
            {
                long gap = timestamp - previous;
                UpdateMax(ref _maxInputCallbackGapTicks[index], gap);
                if (gap > Stopwatch.Frequency * CALLBACK_GAP_WARNING_MS / 1000)
                {
                    Interlocked.Increment(ref _inputCallbackGapCounts[index]);
                }
            }

            int frames = length / sizeof(float);
            Interlocked.Increment(ref _inputCallbackCounts[index]);
            Interlocked.Add(ref _inputCallbackFrames[index], frames);
            Volatile.Write(ref _lastInputCallbackFrames[index], frames);
            if (Bass.StreamPutData(streams[index], buffer, length) < 0)
            {
                Interlocked.Increment(ref _inputCallbackErrors[index]);
            }
            return length;
        }

        private void UpdateMaxCallbackGap(long gap)
        {
            UpdateMax(ref _maxCallbackGapTicks, gap);
        }

        private static void UpdateMax(ref long target, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref target);
                if (value <= current)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref target, value, current) != current);
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
                DrainAnalysisStreams();
                MaintainDetachedMonitorStreams();
                UpdateQueueTelemetry();
                for (int i = 0; i < _inputLevels.Length; i++)
                {
                    double level = BassAsio.ChannelGetLevel(true, i);
                    if (level >= 0)
                    {
                        _inputLevels[i] = Math.Max(level, _inputLevels[i] * 0.82);
                    }
                }
                _asioCpuUsage = BassAsio.CPUUsage;
                _repaint();
            }
            catch (Exception exception)
            {
                SetError($"Failed to update ASIO routing status: {exception.Message}");
            }
        }

        private void DrainAnalysisStreams()
        {
            for (int i = 0; i < _analysisStreams.Count; i++)
            {
                _analysisLevels[i] *= 0.82;
                DrainStream(_analysisStreams[i], true, i);
            }
        }

        private void MaintainDetachedMonitorStreams()
        {
            if (_detachedMonitorPolicy != DetachedMonitorPolicy.DrainOnEditorUpdate)
            {
                return;
            }

            for (int i = 0; i < _monitorStreams.Count && i < _attachedRoutes.Length; i++)
            {
                if (!_attachedRoutes[i])
                {
                    DrainStream(_monitorStreams[i], false, i);
                }
            }
        }

        private void ResetDetachedMonitorStreams()
        {
            for (int i = 0; i < _monitorStreams.Count && i < _attachedRoutes.Length; i++)
            {
                if (!_attachedRoutes[i] && !BassMix.SplitStreamReset(_monitorStreams[i], 0))
                {
                    _telemetryErrors++;
                    SetBassError($"Failed to reset detached monitor splitter {i}");
                    return;
                }
            }
            _status = "Reset detached monitor splitters to live position.";
        }

        private void UpdateQueueTelemetry()
        {
            long bufferLimitBytes = (long) _sampleRate * sizeof(float) *
                BassMix.SplitBufferLength / 1000;
            for (int i = 0; i < _inputStreams.Count; i++)
            {
                _rootQueuedBytes[i] = ReadTelemetry(
                    Bass.StreamPutData(_inputStreams[i], IntPtr.Zero, 0));
                _splitBufferedBytes[i] = ReadTelemetry(
                    BassMix.SplitStreamGetAvailable(_inputStreams[i]));
                _analysisLagBytes[i] = ReadTelemetry(
                    BassMix.SplitStreamGetAvailable(_analysisStreams[i]));
                int monitorLag = ReadTelemetry(
                    BassMix.SplitStreamGetAvailable(_monitorStreams[i]));
                _monitorLagBytes[i] = monitorLag;
                if (monitorLag > _maxMonitorLagBytes[i])
                {
                    _maxMonitorLagBytes[i] = monitorLag;
                }

                bool atLimit = bufferLimitBytes > 0 && monitorLag >= bufferLimitBytes * 95 / 100;
                if (atLimit && !_monitorAtBufferLimit[i])
                {
                    _monitorBufferLimitHits[i]++;
                }
                _monitorAtBufferLimit[i] = atLimit;
            }
        }

        private int ReadTelemetry(int value)
        {
            if (value >= 0)
            {
                return value;
            }
            _telemetryErrors++;
            return 0;
        }

        private void DrainStream(int stream, bool calculatePeak, int index)
        {
            unsafe
            {
                float* buffer = stackalloc float[DRAIN_BUFFER_BYTES / sizeof(float)];
                int bytesRead;
                do
                {
                    bytesRead = Bass.ChannelGetData(stream, (IntPtr) buffer, DRAIN_BUFFER_BYTES);
                    if (bytesRead > 0 && calculatePeak)
                    {
                        _analysisBytes[index] += bytesRead;
                        int sampleCount = bytesRead / sizeof(float);
                        double peak = _analysisLevels[index];
                        for (int sample = 0; sample < sampleCount; sample++)
                        {
                            peak = Math.Max(peak, Math.Abs(buffer[sample]));
                        }
                        _analysisLevels[index] = peak;
                    }
                } while (bytesRead > 0);

                if (bytesRead < 0)
                {
                    _telemetryErrors++;
                }
            }
        }

        private void StopRouting()
        {
            string cleanupError = null;
            bool callbacksStopped = !_ownsAsio;
            bool inputBindingsReset = !_ownsAsio;
            _running = false;
            if (_ownsAsio)
            {
                try
                {
                    BassAsio.CurrentDevice = _device;
                    if (BassAsio.IsStarted && !BassAsio.Stop())
                    {
                        AppendCleanupError(ref cleanupError,
                            $"failed to stop ASIO: {BassAsio.LastError}");
                    }
                    callbacksStopped = !BassAsio.IsStarted;

                    if (callbacksStopped)
                    {
                        var resetFlags = AsioChannelResetFlags.Enable |
                            AsioChannelResetFlags.Join | AsioChannelResetFlags.Format |
                            AsioChannelResetFlags.Rate | AsioChannelResetFlags.Volume;
                        inputBindingsReset = BassAsio.ChannelReset(true, -1, resetFlags);
                        if (!inputBindingsReset)
                        {
                            AppendCleanupError(ref cleanupError,
                                $"failed to reset ASIO inputs: {BassAsio.LastError}");
                        }
                        if (!BassAsio.ChannelReset(false, -1, resetFlags))
                        {
                            AppendCleanupError(ref cleanupError,
                                $"failed to reset ASIO outputs: {BassAsio.LastError}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    AppendCleanupError(ref cleanupError, exception.Message);
                }
            }

            if (!callbacksStopped || !inputBindingsReset)
            {
                // Keep every BASS handle alive while ASIO may still call or retain it. A later
                // Stop retries cleanup without introducing a callback use-after-free.
                _running = true;
                SetError($"ASIO still owns input streams; cleanup deferred: {cleanupError}");
                return;
            }

            Volatile.Write(ref _callbackInputStreams, Array.Empty<int>());
            for (int i = 0; i < _monitorStreams.Count && i < _attachedRoutes.Length; i++)
            {
                if (_attachedRoutes[i] && !BassMix.MixerRemoveChannel(_monitorStreams[i]))
                {
                    AppendCleanupError(ref cleanupError,
                        $"failed to detach monitor {i}: {Bass.LastError}");
                }
            }

            FreeStreams(_monitorStreams, "monitor splitter", ref cleanupError);
            FreeStreams(_analysisStreams, "analysis splitter", ref cleanupError);
            FreeStreams(_pumpStreams, "clock splitter", ref cleanupError);
            FreeStreams(_inputStreams, "input root", ref cleanupError);
            _attachedRoutes = new bool[_routes.Length];

            int mixer = Interlocked.Exchange(ref _masterMixerHandle, 0);
            if (mixer != 0 && !Bass.StreamFree(mixer))
            {
                AppendCleanupError(ref cleanupError,
                    $"failed to free master mixer: {Bass.LastError}");
            }

            if (_ownsBass)
            {
                try
                {
                    Bass.CurrentDevice = Bass.NoSoundDevice;
                    if (Bass.Free())
                    {
                        _ownsBass = false;
                    }
                    else
                    {
                        AppendCleanupError(ref cleanupError,
                            $"failed to free BASS: {Bass.LastError}");
                    }
                }
                catch (Exception exception)
                {
                    AppendCleanupError(ref cleanupError, exception.Message);
                }
            }

            if (_ownsBass)
            {
                _running = true;
                SetError($"BASS cleanup deferred; press Stop to retry: {cleanupError}");
                return;
            }

            _inputLatencyFrames = 0;
            _outputLatencyFrames = 0;
            _asioCpuUsage = 0;
            Interlocked.Exchange(ref _lastOutputCallbackTimestamp, 0);
            if (cleanupError != null)
            {
                SetError($"Failed to stop ASIO routing cleanly: {cleanupError}");
            }
            else if (!(_status?.StartsWith("Error:") ?? false))
            {
                _status = _ownsAsio ? "Routing stopped. Driver remains open." : "Routing stopped.";
            }
            _repaint();
        }

        private static void AppendCleanupError(ref string cleanupError, string error)
        {
            cleanupError = cleanupError == null ? error : $"{cleanupError}; {error}";
        }

        private static void FreeStreams(List<int> streams, string description,
            ref string cleanupError)
        {
            for (int i = streams.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (streams[i] != 0 && !Bass.StreamFree(streams[i]))
                    {
                        AppendCleanupError(ref cleanupError,
                            $"failed to free {description} {i}: {Bass.LastError}");
                    }
                }
                catch (Exception exception)
                {
                    AppendCleanupError(ref cleanupError,
                        $"failed to free {description} {i}: {exception.Message}");
                }
            }
            streams.Clear();
        }

        private void CloseDriver()
        {
            StopRouting();
            if (_running)
            {
                SetError("Cannot close ASIO driver until routing cleanup succeeds.");
                return;
            }
            if (_ownsAsio)
            {
                try
                {
                    BassAsio.CurrentDevice = _device;
                    if (!BassAsio.Free())
                    {
                        SetAsioError("Failed to free ASIO driver");
                        return;
                    }
                }
                catch (Exception exception)
                {
                    SetError($"Failed to free ASIO driver: {exception.Message}");
                    return;
                }
                _ownsAsio = false;
            }

            _inputNames = Array.Empty<string>();
            _inputLevels = Array.Empty<double>();
            _outputPairNames = Array.Empty<string>();
            _outputPairChannels = Array.Empty<int>();
            _routes = Array.Empty<bool>();
            _attachedRoutes = Array.Empty<bool>();
            ResetInputMetrics(0);
            if (!(_status?.StartsWith("Error:") ?? false))
            {
                _status = "ASIO driver closed.";
            }
            _repaint();
        }

        private void ResetInputMetrics(int count)
        {
            _analysisLevels = new double[count];
            _analysisBytes = new long[count];
            _rootQueuedBytes = new int[count];
            _splitBufferedBytes = new int[count];
            _analysisLagBytes = new int[count];
            _monitorLagBytes = new int[count];
            _maxMonitorLagBytes = new int[count];
            _monitorBufferLimitHits = new int[count];
            _monitorAtBufferLimit = new bool[count];
            _inputCallbackCounts = new long[count];
            _inputCallbackFrames = new long[count];
            _firstInputCallbackTimestamps = new long[count];
            _lastInputCallbackTimestamps = new long[count];
            _maxInputCallbackGapTicks = new long[count];
            _lastInputCallbackFrames = new int[count];
            _inputCallbackGapCounts = new int[count];
            _inputCallbackErrors = new int[count];
        }

        private double GetInputClockDriftMilliseconds(int index)
        {
            if (index < 0 || index >= _inputCallbackFrames.Length || _sampleRate <= 0)
            {
                return 0;
            }

            long first = Interlocked.Read(ref _firstInputCallbackTimestamps[index]);
            long last = Interlocked.Read(ref _lastInputCallbackTimestamps[index]);
            long frames = Interlocked.Read(ref _inputCallbackFrames[index]);
            int currentBufferFrames = Volatile.Read(ref _lastInputCallbackFrames[index]);
            if (first == 0 || last <= first || frames <= currentBufferFrames)
            {
                return 0;
            }

            double qpcFrames = (last - first) * (double) _sampleRate / Stopwatch.Frequency;
            return (frames - currentBufferFrames - qpcFrames) * 1000.0 / _sampleRate;
        }

        private double BytesToMilliseconds(long bytes)
        {
            return _sampleRate <= 0 ? 0 : bytes * 1000.0 / (_sampleRate * sizeof(float));
        }

        private static int ReadArray(int[] values, int index)
        {
            return index >= 0 && index < values.Length ? values[index] : 0;
        }

        private static long ReadArray(long[] values, int index)
        {
            return index >= 0 && index < values.Length ? values[index] : 0;
        }

        private int SelectedRouteCount()
        {
            int count = 0;
            foreach (bool route in _routes)
            {
                if (route)
                {
                    count++;
                }
            }
            return count;
        }

        private int AttachedRouteCount()
        {
            int count = 0;
            foreach (bool attached in _attachedRoutes)
            {
                if (attached)
                {
                    count++;
                }
            }
            return count;
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
            Debug.LogError($"ASIO routing test: {message}");
            _repaint();
        }
#endif
    }
}
