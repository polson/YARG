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
        private const double STATUS_UPDATE_INTERVAL = 0.1;
        private const float MAX_ROUTE_GAIN = 4f;
        private const int DRAIN_BUFFER_BYTES = 4096;

        private readonly Action _repaint;
        private readonly AsioProcedure _outputCallback;
        private readonly List<int> _inputStreams = new();

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
        private float _routeGain = 1f;
        private bool _headphonesConfirmed;
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
        private double _asioCpuUsage;
        private double _nextStatusUpdate;
        private string _status = "Select an ASIO driver, then open it.";
        private Vector2 _scrollPosition;

        public AsioRoutingTestTab(Action repaint)
        {
            _repaint = repaint;
            _outputCallback = FillOutputBuffer;
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

            EditorGUILayout.HelpBox(
                "Pre-enables every ASIO input before driver start. Route changes then attach/detach " +
                "mixer inputs without restarting ASIO. This tests routing mechanics, not game " +
                "profile creation.", MessageType.Info);

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
                EditorGUILayout.DoubleField("Max callback gap (ms)",
                    Interlocked.Read(ref _maxCallbackGapTicks) * 1000.0 / Stopwatch.Frequency);
                EditorGUILayout.IntField("Callback gaps > 50 ms", _callbackGapCount);
                EditorGUILayout.IntField("Input latency (frames)", _inputLatencyFrames);
                EditorGUILayout.IntField("Output latency (frames)", _outputLatencyFrames);
                EditorGUILayout.DoubleField("ASIO CPU usage (%)", _asioCpuUsage);
            }

            for (int i = 0; i < _inputLevels.Length; i++)
            {
                EditorGUILayout.LabelField($"Input {i} peak", $"{_inputLevels[i]:P1}");
            }

            EditorGUILayout.HelpBox(
                "Expected result: selecting or clearing input routes changes attached route count " +
                "while ASIO start count stays constant. Callback gaps should remain near zero.",
                MessageType.None);
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
                if (!BassAsio.Init(_device, AsioInitFlags.Thread))
                {
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

                _masterMixerHandle = BassMix.CreateMixerStream(_sampleRate, 2,
                    BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);
                if (_masterMixerHandle == 0)
                {
                    SetBassError("Failed to create routing master mixer");
                    StopRouting();
                    return;
                }

                for (int i = 0; i < _inputNames.Length; i++)
                {
                    int stream = Bass.CreateStream(_sampleRate, 1,
                        BassFlags.Float | BassFlags.Decode, StreamProcedureType.Push);
                    if (stream == 0)
                    {
                        SetBassError($"Failed to create input route {i}");
                        StopRouting();
                        return;
                    }

                    _inputStreams.Add(stream);
                    if (!BassAsio.ChannelEnableBass(true, i, stream, Join: false))
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
            for (int i = 0; i < _inputStreams.Count && i < _routes.Length; i++)
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
            for (int i = 0; i < _inputStreams.Count && i < _attachedRoutes.Length; i++)
            {
                if (_attachedRoutes[i])
                {
                    Bass.ChannelSetAttribute(_inputStreams[i], ChannelAttribute.Volume,
                        _routeGain);
                }
            }
        }

        private bool SetRouteAttached(int index, bool attached)
        {
            if (index < 0 || index >= _inputStreams.Count ||
                index >= _attachedRoutes.Length || _masterMixerHandle == 0)
            {
                return false;
            }

            if (_attachedRoutes[index] == attached)
            {
                return true;
            }

            int stream = _inputStreams[index];
            if (attached)
            {
                // Drop samples captured while route was detached. Decode push streams do not
                // reliably flush through StreamPutData(..., 0); consume their queue instead.
                DrainInputStream(stream);
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
            if (!attached)
            {
                DrainInputStream(stream);
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
                if (gap > Stopwatch.Frequency / 20)
                {
                    Interlocked.Increment(ref _callbackGapCount);
                }
            }

            Interlocked.Increment(ref _outputCallbackCount);
            int bytesRead = Bass.ChannelGetData(mixer, buffer, length);
            return bytesRead < 0 ? 0 : bytesRead;
        }

        private void UpdateMaxCallbackGap(long gap)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref _maxCallbackGapTicks);
                if (gap <= current)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref _maxCallbackGapTicks, gap, current) != current);
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
                DrainDetachedInputStreams();
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

        private void DrainDetachedInputStreams()
        {
            for (int i = 0; i < _inputStreams.Count && i < _attachedRoutes.Length; i++)
            {
                if (!_attachedRoutes[i])
                {
                    DrainInputStream(_inputStreams[i]);
                }
            }
        }

        private void DrainInputStream(int stream)
        {
            unsafe
            {
                byte* buffer = stackalloc byte[DRAIN_BUFFER_BYTES];
                int bytesRead;
                do
                {
                    bytesRead = Bass.ChannelGetData(stream, (IntPtr) buffer, DRAIN_BUFFER_BYTES);
                } while (bytesRead > 0);

                if (bytesRead < 0)
                {
                    SetBassError($"Failed to drain detached input stream {stream}");
                }
            }
        }

        private void StopRouting()
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
                        AsioChannelResetFlags.Join | AsioChannelResetFlags.Format |
                        AsioChannelResetFlags.Rate | AsioChannelResetFlags.Volume;
                    BassAsio.ChannelReset(true, -1, resetFlags);
                    BassAsio.ChannelReset(false, -1, resetFlags);
                }
                catch (Exception exception)
                {
                    SetError($"Failed to stop ASIO routing cleanly: {exception.Message}");
                }
            }

            _running = false;
            foreach (int stream in _inputStreams)
            {
                Bass.StreamFree(stream);
            }
            _inputStreams.Clear();
            _attachedRoutes = Array.Empty<bool>();
            if (_masterMixerHandle != 0)
            {
                Bass.StreamFree(_masterMixerHandle);
                _masterMixerHandle = 0;
            }

            if (_ownsBass)
            {
                Bass.CurrentDevice = 0;
                Bass.Free();
                _ownsBass = false;
            }

            _inputLatencyFrames = 0;
            _outputLatencyFrames = 0;
            _asioCpuUsage = 0;
            Interlocked.Exchange(ref _lastOutputCallbackTimestamp, 0);
            if (!(_status?.StartsWith("Error:") ?? false))
            {
                _status = _ownsAsio ? "Routing stopped. Driver remains open." : "Routing stopped.";
            }
            _repaint();
        }

        private void CloseDriver()
        {
            StopRouting();
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
            _inputLevels = Array.Empty<double>();
            _outputPairNames = Array.Empty<string>();
            _outputPairChannels = Array.Empty<int>();
            _routes = Array.Empty<bool>();
            _attachedRoutes = Array.Empty<bool>();
            if (!(_status?.StartsWith("Error:") ?? false))
            {
                _status = "ASIO driver closed.";
            }
            _repaint();
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
