using System;
using System.Diagnostics;
using ManagedBass.Asio;
using UnityEditor;
using UnityEngine;

namespace YARG.Editor
{
    /// <summary>
    /// Measures the ASIO device clock against QPC without involving BASS playback or mixing.
    /// </summary>
    public sealed class AsioClockTestWindow : EditorWindow
    {
        private const double STATUS_UPDATE_INTERVAL = 0.1;
        private static readonly Vector2 DefaultWindowSize = new(620, 620);

        private readonly object _measurementLock = new();
        private readonly AsioProcedure _outputCallback;

        private string[] _deviceNames = Array.Empty<string>();
        private string _status = "Select an ASIO device, then start the test.";
        private int _device;
        private int _sampleRate = 48000;
        private int _bufferLength;
        private double _warmupSeconds = 2;
        private bool _ownsAsio;
        private double _nextStatusUpdate;

        private long _runStartedTimestamp;
        private long _measurementStartedTimestamp;
        private long _lastCallbackTimestamp;
        private long _totalFrames;
        private long _measurementStartFrame;
        private long _latestMeasurementFrame;
        private long _callbackCount;
        private long _measurementCount;
        private long _intervalCount;
        private long _lastFrameCount;
        private long _minimumFrameCount;
        private long _maximumFrameCount;
        private double _latestMeasurementTime;
        private double _meanTime;
        private double _meanFrame;
        private double _timeVariance;
        private double _timeFrameCovariance;
        private double _meanCallbackInterval;
        private double _callbackIntervalVariance;
        private double _maximumIntervalDeviation;

        public AsioClockTestWindow()
        {
            _outputCallback = FillSilence;
        }

        [MenuItem("YARG/Audio/ASIO Clock Test")]
        private static void Open()
        {
            var window = GetWindow<AsioClockTestWindow>("ASIO Clock Test");
            window.minSize = DefaultWindowSize;
            window.position = new Rect(window.position.position, DefaultWindowSize);
            window.Show();
        }

        private void OnEnable()
        {
            minSize = DefaultWindowSize;
            RefreshDevices();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            StopTest();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Measures ASIO callback sample rate against Stopwatch/QPC while outputting silence. " +
                "No BASS stream, mixer, song clock, latency correction, or sync controller is used. " +
                "Stop play mode and other ASIO users before starting.",
                MessageType.Info);

#if UNITY_EDITOR_WIN
            using (new EditorGUI.DisabledScope(_ownsAsio))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _device = EditorGUILayout.Popup("ASIO device", _device, _deviceNames);
                    if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    {
                        RefreshDevices();
                    }
                }

                _sampleRate = Math.Max(1, EditorGUILayout.IntField("Sample rate", _sampleRate));
                _bufferLength = Math.Max(0,
                    EditorGUILayout.IntField("Buffer samples (0 = preferred)", _bufferLength));
                _warmupSeconds = Math.Max(0,
                    EditorGUILayout.DoubleField("Warmup seconds", _warmupSeconds));
            }

            if (_deviceNames.Length == 0)
            {
                EditorGUILayout.HelpBox("No ASIO drivers found.", MessageType.Warning);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_ownsAsio || _deviceNames.Length == 0))
                {
                    if (GUILayout.Button("Start Test"))
                    {
                        StartTest();
                    }
                }

                using (new EditorGUI.DisabledScope(!_ownsAsio))
                {
                    if (GUILayout.Button("Reset Measurement"))
                    {
                        ResetMeasurement();
                    }

                    if (GUILayout.Button("Stop"))
                    {
                        StopTest();
                    }
                }
            }
#else
            EditorGUILayout.HelpBox("ASIO output is only available in Windows editor.",
                MessageType.Warning);
#endif

            EditorGUILayout.Space();
            DrawResults();
        }

        private void DrawResults()
        {
            double elapsed;
            double effectiveRate;
            double driftPpm;
            double endpointDriftMilliseconds;
            double intervalMilliseconds;
            double intervalJitterMilliseconds;
            double maximumIntervalDeviationMilliseconds;
            long callbackCount;
            long measurementCount;
            long totalFrames;
            long lastFrameCount;
            long minimumFrameCount;
            long maximumFrameCount;

            lock (_measurementLock)
            {
                elapsed = _latestMeasurementTime;
                effectiveRate = _timeVariance > 0
                    ? _timeFrameCovariance / _timeVariance
                    : 0;
                driftPpm = effectiveRate > 0
                    ? (effectiveRate / _sampleRate - 1) * 1_000_000
                    : 0;
                endpointDriftMilliseconds = _measurementStartedTimestamp != 0
                    ? ((_latestMeasurementFrame / (double) _sampleRate) - elapsed) * 1000
                    : 0;
                intervalMilliseconds = _meanCallbackInterval * 1000;
                intervalJitterMilliseconds = _intervalCount > 1
                    ? Math.Sqrt(_callbackIntervalVariance / (_intervalCount - 1)) * 1000
                    : 0;
                maximumIntervalDeviationMilliseconds = _maximumIntervalDeviation * 1000;
                callbackCount = _callbackCount;
                measurementCount = _measurementCount;
                totalFrames = _totalFrames;
                lastFrameCount = _lastFrameCount;
                minimumFrameCount = _minimumFrameCount == long.MaxValue ? 0 : _minimumFrameCount;
                maximumFrameCount = _maximumFrameCount;
            }

            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, _status.StartsWith("Error:")
                ? MessageType.Error
                : MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.DoubleField("Measurement time (s)", elapsed);
                EditorGUILayout.LongField("Callbacks", callbackCount);
                EditorGUILayout.LongField("Measured callbacks", measurementCount);
                EditorGUILayout.LongField("Total frames", totalFrames);
                EditorGUILayout.LongField("Latest callback frames", lastFrameCount);
                EditorGUILayout.TextField("Callback frame range",
                    $"{minimumFrameCount} - {maximumFrameCount}");
                EditorGUILayout.DoubleField("Configured rate (Hz)", _sampleRate);
                EditorGUILayout.DoubleField("Measured rate (Hz)", effectiveRate);
                EditorGUILayout.DoubleField("Rate difference (ppm)", driftPpm);
                EditorGUILayout.DoubleField("Expected drift (ms/min)", driftPpm * 0.06);
                EditorGUILayout.DoubleField("Endpoint drift (ms)", endpointDriftMilliseconds);
                EditorGUILayout.DoubleField("Mean callback interval (ms)", intervalMilliseconds);
                EditorGUILayout.DoubleField("Callback interval jitter (ms)",
                    intervalJitterMilliseconds);
                EditorGUILayout.DoubleField("Maximum interval deviation (ms)",
                    maximumIntervalDeviationMilliseconds);
            }

            EditorGUILayout.HelpBox(
                "Run for at least 1-5 minutes. Similar ppm at multiple buffer sizes indicates real " +
                "ASIO hardware/QPC clock mismatch. Buffer-dependent ppm indicates callback timing or " +
                "frame-counting error. Interval jitter measures callback scheduling noise, not clock drift.",
                MessageType.Info);
        }

        private void RefreshDevices()
        {
#if UNITY_EDITOR_WIN
            try
            {
                int count = BassAsio.DeviceCount;
                _deviceNames = new string[count];
                for (int i = 0; i < count; i++)
                {
                    _deviceNames[i] = BassAsio.GetDeviceInfo(i).Name;
                }

                _device = Mathf.Clamp(_device, 0, Math.Max(0, count - 1));
            }
            catch (Exception exception)
            {
                _deviceNames = Array.Empty<string>();
                SetError($"Failed to enumerate ASIO devices: {exception.Message}");
            }
#endif
        }

        private void StartTest()
        {
#if UNITY_EDITOR_WIN
            StopTest();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SetError("Exit play mode before running this test.");
                return;
            }

            try
            {
                if (!BassAsio.Init(_device, AsioInitFlags.Thread))
                {
                    SetAsioError("Failed to initialize ASIO device");
                    return;
                }
                _ownsAsio = true;

                if (!BassAsio.CheckRate(_sampleRate))
                {
                    SetAsioError($"ASIO device does not support {_sampleRate} Hz");
                    StopTest();
                    return;
                }

                BassAsio.Rate = _sampleRate;
                if (!BassAsio.ChannelEnable(false, 0, _outputCallback, IntPtr.Zero) ||
                    !BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float) ||
                    !BassAsio.ChannelSetRate(false, 0, _sampleRate) ||
                    !BassAsio.Start(_bufferLength, 0))
                {
                    SetAsioError("Failed to start ASIO output");
                    StopTest();
                    return;
                }

                ResetMeasurement();
                _status = $"Measuring '{_deviceNames[_device]}' at {_sampleRate} Hz.";
            }
            catch (Exception exception)
            {
                SetError($"Failed to start ASIO test: {exception.Message}");
                StopTest();
            }
#endif
        }

        private int FillSilence(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            long timestamp = Stopwatch.GetTimestamp();
            long frameCount = length / sizeof(float); // One enabled mono float channel.

            lock (_measurementLock)
            {
                if (_runStartedTimestamp == 0)
                {
                    _runStartedTimestamp = timestamp;
                }

                long blockStartFrame = _totalFrames;
                _totalFrames += frameCount;
                _callbackCount++;
                _lastFrameCount = frameCount;
                _minimumFrameCount = Math.Min(_minimumFrameCount, frameCount);
                _maximumFrameCount = Math.Max(_maximumFrameCount, frameCount);

                double runTime = (double) (timestamp - _runStartedTimestamp) /
                    Stopwatch.Frequency;
                if (runTime < _warmupSeconds)
                {
                    return 0;
                }

                if (_measurementStartedTimestamp == 0)
                {
                    _measurementStartedTimestamp = timestamp;
                    _measurementStartFrame = blockStartFrame;
                    _lastCallbackTimestamp = timestamp;
                }

                double time = (double) (timestamp - _measurementStartedTimestamp) /
                    Stopwatch.Frequency;
                double frame = blockStartFrame - _measurementStartFrame;
                AddRegressionSample(time, frame);

                if (_measurementCount > 1)
                {
                    double interval = (double) (timestamp - _lastCallbackTimestamp) /
                        Stopwatch.Frequency;
                    AddIntervalSample(interval);
                }

                _lastCallbackTimestamp = timestamp;
                _latestMeasurementTime = time;
                _latestMeasurementFrame = (long) frame;
            }

            // Returning 0 asks BASSASIO to fill the output buffer with silence.
            return 0;
        }

        private void AddRegressionSample(double time, double frame)
        {
            _measurementCount++;
            double timeDelta = time - _meanTime;
            _meanTime += timeDelta / _measurementCount;
            double frameDelta = frame - _meanFrame;
            _meanFrame += frameDelta / _measurementCount;
            _timeVariance += timeDelta * (time - _meanTime);
            _timeFrameCovariance += timeDelta * (frame - _meanFrame);
        }

        private void AddIntervalSample(double interval)
        {
            _intervalCount++;
            double delta = interval - _meanCallbackInterval;
            _meanCallbackInterval += delta / _intervalCount;
            _callbackIntervalVariance += delta * (interval - _meanCallbackInterval);
            _maximumIntervalDeviation = Math.Max(_maximumIntervalDeviation,
                Math.Abs(interval - _meanCallbackInterval));
        }

        private void ResetMeasurement()
        {
            lock (_measurementLock)
            {
                _runStartedTimestamp = 0;
                _measurementStartedTimestamp = 0;
                _lastCallbackTimestamp = 0;
                _totalFrames = 0;
                _measurementStartFrame = 0;
                _latestMeasurementFrame = 0;
                _callbackCount = 0;
                _measurementCount = 0;
                _intervalCount = 0;
                _lastFrameCount = 0;
                _minimumFrameCount = long.MaxValue;
                _maximumFrameCount = 0;
                _latestMeasurementTime = 0;
                _meanTime = 0;
                _meanFrame = 0;
                _timeVariance = 0;
                _timeFrameCovariance = 0;
                _meanCallbackInterval = 0;
                _callbackIntervalVariance = 0;
                _maximumIntervalDeviation = 0;
            }
        }

        private void StopTest()
        {
#if UNITY_EDITOR_WIN
            if (_ownsAsio)
            {
                BassAsio.Stop();
                BassAsio.Free();
                _ownsAsio = false;
                if (!_status.StartsWith("Error:"))
                {
                    _status = "Test stopped.";
                }
            }
#endif
        }

        private void OnEditorUpdate()
        {
            if (!_ownsAsio || EditorApplication.timeSinceStartup < _nextStatusUpdate)
            {
                return;
            }

            _nextStatusUpdate = EditorApplication.timeSinceStartup + STATUS_UPDATE_INTERVAL;
            Repaint();
        }

        private void SetAsioError(string message)
        {
            SetError($"{message}: {BassAsio.LastError}");
        }

        private void SetError(string message)
        {
            _status = $"Error: {message}";
            UnityEngine.Debug.LogError($"ASIO clock test: {message}");
        }
    }
}
