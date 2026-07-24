using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
        private const int MAXIMUM_GRAPH_SAMPLES = 18_000;
        private const int GRAPH_SAMPLES_TO_TRIM = 2_000;
        private const int SMOOTHING_SAMPLE_COUNT = 10;
        private static readonly Vector2 DefaultWindowSize = new(620, 800);

        private readonly AsioProcedure _outputCallback;
        private readonly List<Vector2> _driftHistory = new();

        private string[] _deviceNames = Array.Empty<string>();
        private string _status = "Select an ASIO device, then start the test.";
        private int _device;
        private int _sampleRate = 48000;
        private int _bufferLength;
        private double _warmupSeconds = 2;
        private bool _useDedicatedDriverThread = true;
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
        private double _asioCpuUsage;
        private double _maximumAsioCpuUsage;
        private double _lastGraphMeasurementTime = -1;
        private double _maximumGraphDriftChange;
        private int _measurementVersion;
        private int _resetRequested;

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
                _useDedicatedDriverThread = EditorGUILayout.Toggle(
                    new GUIContent("Dedicated driver host thread",
                        "Uses BASS_ASIO_THREAD. When disabled, the ASIO driver is hosted on the " +
                        "Unity editor thread that starts the test."),
                    _useDedicatedDriverThread);
            }

            EditorGUILayout.HelpBox(
                _useDedicatedDriverThread
                    ? "BASSASIO hosts the driver on its dedicated thread."
                    : "BASSASIO hosts the driver on the Unity editor thread. This tests driver " +
                      "hosting only; ASIO callback thread priority remains driver-controlled.",
                MessageType.None);

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
            MeasurementSnapshot measurement = GetMeasurementSnapshot();
            double elapsed = measurement.LatestTime;
            double effectiveRate = measurement.TimeVariance > 0
                ? measurement.TimeFrameCovariance / measurement.TimeVariance
                : 0;
            double driftPpm = effectiveRate > 0
                ? (effectiveRate / _sampleRate - 1) * 1_000_000
                : 0;
            double endpointDriftMilliseconds = measurement.Started
                ? ((measurement.LatestFrame / (double) _sampleRate) - elapsed) * 1000
                : 0;
            double intervalMilliseconds = measurement.MeanCallbackInterval * 1000;
            double intervalJitterMilliseconds = measurement.IntervalCount > 1
                ? Math.Sqrt(measurement.CallbackIntervalVariance /
                    (measurement.IntervalCount - 1)) * 1000
                : 0;
            double maximumIntervalDeviationMilliseconds =
                measurement.MaximumIntervalDeviation * 1000;

            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, _status.StartsWith("Error:")
                ? MessageType.Error
                : MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.DoubleField("Measurement time (s)", elapsed);
                EditorGUILayout.LongField("Callbacks", measurement.CallbackCount);
                EditorGUILayout.LongField("Measured callbacks", measurement.MeasurementCount);
                EditorGUILayout.LongField("Total frames", measurement.TotalFrames);
                EditorGUILayout.LongField("Latest callback frames", measurement.LastFrameCount);
                EditorGUILayout.TextField("Callback frame range",
                    $"{measurement.MinimumFrameCount} - {measurement.MaximumFrameCount}");
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
                EditorGUILayout.DoubleField("ASIO CPU usage (%)", _asioCpuUsage);
                EditorGUILayout.DoubleField("Maximum ASIO CPU usage (%)", _maximumAsioCpuUsage);
            }

            DrawDriftGraph();

            EditorGUILayout.HelpBox(
                "Run for at least 1-5 minutes. Similar ppm at multiple buffer sizes indicates real " +
                "ASIO hardware/QPC clock mismatch. Buffer-dependent ppm indicates callback timing or " +
                "frame-counting error. Interval jitter measures callback scheduling noise, not clock drift.",
                MessageType.Info);
        }

        private void DrawDriftGraph()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Endpoint drift history", EditorStyles.boldLabel);

            Rect graphRect = GUILayoutUtility.GetRect(100, 190, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(graphRect, EditorGUIUtility.isProSkin
                ? new Color(0.11f, 0.11f, 0.11f)
                : new Color(0.85f, 0.85f, 0.85f));

            if (_driftHistory.Count < 2)
            {
                GUI.Label(graphRect, "Waiting for measurement data...", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            float measuredMinimum = _driftHistory[0].y;
            float measuredMaximum = _driftHistory[0].y;
            for (int i = 1; i < _driftHistory.Count; i++)
            {
                measuredMinimum = Math.Min(measuredMinimum, _driftHistory[i].y);
                measuredMaximum = Math.Max(measuredMaximum, _driftHistory[i].y);
            }

            float minimumDrift = Math.Min(0, measuredMinimum);
            float maximumDrift = Math.Max(0, measuredMaximum);
            const float MINIMUM_DRIFT_RANGE = 0.1f;
            float driftRange = maximumDrift - minimumDrift;
            if (driftRange < MINIMUM_DRIFT_RANGE)
            {
                float padding = (MINIMUM_DRIFT_RANGE - driftRange) / 2;
                minimumDrift -= padding;
                maximumDrift += padding;
                driftRange = MINIMUM_DRIFT_RANGE;
            }

            Rect plotRect = new(graphRect.x + 50, graphRect.y + 10,
                graphRect.width - 60, graphRect.height - 32);
            float startTime = _driftHistory[0].x;
            float timeRange = _driftHistory[^1].x - startTime;

            if (Event.current.type == EventType.Repaint)
            {
                DrawGraphLine(plotRect, minimumDrift, driftRange, startTime, timeRange,
                    1, new Color(0.1f, 0.85f, 1, 0.25f), 1);
                DrawGraphLine(plotRect, minimumDrift, driftRange, startTime, timeRange,
                    SMOOTHING_SAMPLE_COUNT, new Color(0.1f, 0.85f, 1, 1), 2);

                float zeroY = MapDriftToGraph(0, plotRect, minimumDrift, driftRange);
                Handles.color = new Color(1, 1, 1, 0.3f);
                Handles.DrawLine(new Vector3(plotRect.x, zeroY),
                    new Vector3(plotRect.xMax, zeroY));
            }

            GUI.Label(new Rect(graphRect.x + 2, plotRect.y - 7, 46, 16),
                $"{maximumDrift:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(graphRect.x + 2, plotRect.yMax - 8, 46, 16),
                $"{minimumDrift:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(plotRect.x, plotRect.yMax + 2, 80, 16),
                $"{startTime:F1} s", EditorStyles.miniLabel);
            GUI.Label(new Rect(plotRect.xMax - 80, plotRect.yMax + 2, 80, 16),
                $"{_driftHistory[^1].x:F1} s", new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.UpperRight
                });
            GUI.Label(new Rect(graphRect.x + 2, graphRect.y + graphRect.height / 2 - 8, 46, 16),
                "drift ms", EditorStyles.miniLabel);

            EditorGUILayout.LabelField(
                $"Observed range: {measuredMinimum:F3} to {measuredMaximum:F3} ms  " +
                $"(peak-to-peak {measuredMaximum - measuredMinimum:F3} ms).\n" +
                $"Maximum update jump: {_maximumGraphDriftChange:F3} ms. " +
                "Faint line is raw; bright line uses a 1-second moving average.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawGraphLine(Rect plotRect, float minimumDrift, float driftRange,
            float startTime, float timeRange, int smoothingSamples, Color color, float width)
        {
            var points = new Vector3[_driftHistory.Count];
            double driftSum = 0;

            for (int i = 0; i < _driftHistory.Count; i++)
            {
                driftSum += _driftHistory[i].y;
                if (i >= smoothingSamples)
                {
                    driftSum -= _driftHistory[i - smoothingSamples].y;
                }

                int sampleCount = Math.Min(i + 1, smoothingSamples);
                float drift = (float) (driftSum / sampleCount);
                float normalizedTime = timeRange > 0
                    ? (_driftHistory[i].x - startTime) / timeRange
                    : 0;
                points[i] = new Vector3(
                    Mathf.Lerp(plotRect.x, plotRect.xMax, normalizedTime),
                    MapDriftToGraph(drift, plotRect, minimumDrift, driftRange));
            }

            Handles.color = color;
            Handles.DrawAAPolyLine(width, points);
        }

        private static float MapDriftToGraph(float drift, Rect plotRect,
            float minimumDrift, float driftRange)
        {
            float normalizedDrift = (drift - minimumDrift) / driftRange;
            return Mathf.Lerp(plotRect.yMax, plotRect.y, normalizedDrift);
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
                AsioInitFlags initFlags = _useDedicatedDriverThread
                    ? AsioInitFlags.Thread
                    : AsioInitFlags.None;
                if (!BassAsio.Init(_device, initFlags))
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
                string driverHost = _useDedicatedDriverThread
                    ? "dedicated BASSASIO driver host"
                    : "Unity editor driver host";
                _status = $"Measuring '{_deviceNames[_device]}' at {_sampleRate} Hz using " +
                    $"{driverHost}.";
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

            Interlocked.Increment(ref _measurementVersion);
            try
            {
                if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
                {
                    ResetMeasurementState();
                }

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
            finally
            {
                Interlocked.Increment(ref _measurementVersion);
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
            if (_ownsAsio)
            {
                Interlocked.Exchange(ref _resetRequested, 1);
            }
            else
            {
                ResetMeasurementState();
            }

            _driftHistory.Clear();
            _lastGraphMeasurementTime = -1;
            _maximumGraphDriftChange = 0;
            _asioCpuUsage = 0;
            _maximumAsioCpuUsage = 0;
        }

        private void ResetMeasurementState()
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
            _asioCpuUsage = BassAsio.CPUUsage;
            _maximumAsioCpuUsage = Math.Max(_maximumAsioCpuUsage, _asioCpuUsage);
            RecordDriftGraphSample();
            Repaint();
        }

        private void RecordDriftGraphSample()
        {
            MeasurementSnapshot measurement = GetMeasurementSnapshot();
            if (!measurement.Started || measurement.LatestTime <= _lastGraphMeasurementTime)
            {
                return;
            }

            double endpointDriftMilliseconds =
                ((measurement.LatestFrame / (double) _sampleRate) - measurement.LatestTime) * 1000;
            var sample = new Vector2((float) measurement.LatestTime,
                (float) endpointDriftMilliseconds);
            if (_driftHistory.Count > 0)
            {
                float driftChange = Math.Abs(sample.y - _driftHistory[^1].y);
                _maximumGraphDriftChange = Math.Max(_maximumGraphDriftChange, driftChange);
            }

            _driftHistory.Add(sample);
            _lastGraphMeasurementTime = measurement.LatestTime;

            if (_driftHistory.Count > MAXIMUM_GRAPH_SAMPLES)
            {
                _driftHistory.RemoveRange(0, GRAPH_SAMPLES_TO_TRIM);
            }
        }

        private MeasurementSnapshot GetMeasurementSnapshot()
        {
            var spinWait = new SpinWait();
            while (true)
            {
                int versionBefore = Volatile.Read(ref _measurementVersion);
                if ((versionBefore & 1) != 0)
                {
                    spinWait.SpinOnce();
                    continue;
                }

                var snapshot = new MeasurementSnapshot(
                    _measurementStartedTimestamp != 0,
                    _latestMeasurementTime,
                    _latestMeasurementFrame,
                    _callbackCount,
                    _measurementCount,
                    _totalFrames,
                    _lastFrameCount,
                    _minimumFrameCount == long.MaxValue ? 0 : _minimumFrameCount,
                    _maximumFrameCount,
                    _timeVariance,
                    _timeFrameCovariance,
                    _intervalCount,
                    _meanCallbackInterval,
                    _callbackIntervalVariance,
                    _maximumIntervalDeviation);

                if (versionBefore == Volatile.Read(ref _measurementVersion))
                {
                    return snapshot;
                }

                spinWait.SpinOnce();
            }
        }

        private readonly struct MeasurementSnapshot
        {
            public readonly bool Started;
            public readonly double LatestTime;
            public readonly long LatestFrame;
            public readonly long CallbackCount;
            public readonly long MeasurementCount;
            public readonly long TotalFrames;
            public readonly long LastFrameCount;
            public readonly long MinimumFrameCount;
            public readonly long MaximumFrameCount;
            public readonly double TimeVariance;
            public readonly double TimeFrameCovariance;
            public readonly long IntervalCount;
            public readonly double MeanCallbackInterval;
            public readonly double CallbackIntervalVariance;
            public readonly double MaximumIntervalDeviation;

            public MeasurementSnapshot(bool started, double latestTime, long latestFrame,
                long callbackCount, long measurementCount, long totalFrames, long lastFrameCount,
                long minimumFrameCount, long maximumFrameCount, double timeVariance,
                double timeFrameCovariance, long intervalCount, double meanCallbackInterval,
                double callbackIntervalVariance, double maximumIntervalDeviation)
            {
                Started = started;
                LatestTime = latestTime;
                LatestFrame = latestFrame;
                CallbackCount = callbackCount;
                MeasurementCount = measurementCount;
                TotalFrames = totalFrames;
                LastFrameCount = lastFrameCount;
                MinimumFrameCount = minimumFrameCount;
                MaximumFrameCount = maximumFrameCount;
                TimeVariance = timeVariance;
                TimeFrameCovariance = timeFrameCovariance;
                IntervalCount = intervalCount;
                MeanCallbackInterval = meanCallbackInterval;
                CallbackIntervalVariance = callbackIntervalVariance;
                MaximumIntervalDeviation = maximumIntervalDeviation;
            }
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
