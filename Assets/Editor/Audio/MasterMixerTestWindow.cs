using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEditor;
using UnityEngine;

namespace YARG.Editor
{
    /// <summary>
    /// Exercises the BASS operations needed by the experimental single-mixer output path.
    /// This intentionally owns a standalone BASS device and cannot run alongside play mode.
    /// </summary>
    internal sealed class MasterMixerTestTab
    {
        private enum AsioOutputTransport
        {
            CustomCallback,
            ChannelEnableBass,
        }

        private const double STATUS_UPDATE_INTERVAL = 0.1;
        private const double TRANSITION_UPDATE_INTERVAL = 0.02;
        private const int TEMPO_LATENCY_TRIAL_COUNT = 10;
        private const double TEMPO_TRIAL_SETTLE_SECONDS = 0.25;
        private const double TEMPO_TRIAL_TIMEOUT_SECONDS = 5;
        private readonly float[] _fftData = new float[1024];
        private readonly float[] _previousFftData = new float[1024];
        private readonly float[] _levelData = new float[1];
        private readonly float[] _sampleData = new float[1024];
        private readonly object _asioCallbackLock = new();
        private readonly AsioProcedure _asioCallback;
        private readonly AsioTimingMeasurements _asioTiming = new();
        private readonly List<PositionSample> _positionSamples = new();
        private List<PositionSample> _lastCustomPositionSamples = new();
        private List<PositionSample> _lastDirectPositionSamples = new();
        private readonly Action _repaint;

        private string _audioPath = string.Empty;
        private string _status = "Select an audio file, then create the test graph.";
        private string[] _asioDeviceNames = Array.Empty<string>();
        private bool _useAsio;
        private AsioOutputTransport _asioOutputTransport;
        private int _asioDevice;
        private int _bufferLength = 75;
        private int _positionCaptureLengthSeconds = 30;
        private float _tempo = 100;
        private float _tempoCommand = 150;
        private double _seekPosition;
        private double _length;
        private double _heardPosition;
        private double _estimatedHeardPosition;
        private double _explicitDelayedPosition;
        private double _decodePosition;
        private double _normalizedPosition;
        private double _masterBufferedSeconds;
        private long _sourcePositionOrigin;
        private double _positionAnchor;
        private double _rawAnchoredPosition;
        private double _anchoredPosition;
        private double _estimatedAnchoredPosition;
        private double _explicitAnchoredPosition;
        private double _expectedPosition;
        private double _transitionExpectedStart;
        private double _rawTransitionError;
        private double _transitionError;
        private double _estimatedTransitionError;
        private double _explicitTransitionError;
        private double _transitionAbsoluteErrorTotal;
        private double _transitionMaximumAbsoluteError;
        private double _estimatedTransitionAbsoluteErrorTotal;
        private double _estimatedTransitionMaximumAbsoluteError;
        private int _transitionSampleCount;
        private bool _positionSamplesSaved;
        private double _transitionStartedAt;
        private string _transitionName = "None";
        private string _transitionLog = string.Empty;
        private bool _captureTransition;
        private int _availableSourceBytes;
        private int _fftResult;
        private float _fftPeak;
        private int _fftPeakBin;
        private float _fftChange;
        private int _sampleResult;
        private float _samplePeak;
        private int _levelResult;
        private bool _hasPreviousFft;
        private int _sourceHandle;
        private int _tempoHandle;
        private int _masterHandle;
        private bool _ownsBass;
        private bool _ownsAsio;
        private int _asioBytesPerFrame;
        private int _asioSampleRate;
        private int _asioLatencyFrames;
        private int _asioLatencyBytes;
        private long _lastAsioPosition;
        private bool _runningTempoLatencyTrials;
        private bool _waitingForTempoTrial;
        private int _completedTempoLatencyTrials;
        private double _tempoAffectedTotalMilliseconds;
        private double _tempoChangedTotalMilliseconds;
        private double _tempoAToBTotalMilliseconds;
        private double _tempoBToATotalMilliseconds;
        private int _tempoAToBTrialCount;
        private int _tempoBToATrialCount;
        private bool _currentTrialIsAToB;
        private double _nextTempoTrialAt;
        private double _tempoTrialDeadline;
        private float _tempoTrialA;
        private float _tempoTrialB;
        private string _tempoLatencyTrialResult = "not measured";
        private string _tempoAToBTrialResult = "not measured";
        private string _tempoBToATrialResult = "not measured";
        private double _nextStatusUpdate;
        private Vector2 _scrollPosition;

        public MasterMixerTestTab(Action repaint)
        {
            _repaint = repaint;
            _asioCallback = FillAsioBuffer;
        }

        public void Enable()
        {
            RefreshAsioDevices();
            EditorApplication.update += OnEditorUpdate;
        }

        public void Disable()
        {
            EditorApplication.update -= OnEditorUpdate;
            DisposeGraph();
        }

        public void Draw()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.HelpBox(
                "Standalone spike for tempo-source pause, seek, position, FFT, level, and sample-data " +
                "retrieval through an always-running master mixer. Stop play mode before using it.",
                MessageType.Info);

            DrawAudioFilePicker();

            DrawOutputSettings();

            using (new EditorGUI.DisabledScope(_masterHandle != 0))
            {
                _bufferLength = EditorGUILayout.IntSlider("Playback buffer (ms)", _bufferLength, 10, 5000);
                _positionCaptureLengthSeconds = EditorGUILayout.IntSlider(
                    "Position capture (s)", _positionCaptureLengthSeconds, 5, 120);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_masterHandle != 0 || string.IsNullOrWhiteSpace(_audioPath)))
                {
                    if (GUILayout.Button("Create Test Graph"))
                    {
                        CreateGraph();
                    }
                }

                using (new EditorGUI.DisabledScope(_masterHandle == 0))
                {
                    if (GUILayout.Button("Dispose"))
                    {
                        DisposeGraph();
                    }
                }
            }

            EditorGUILayout.Space();
            DrawPlaybackControls();
            EditorGUILayout.Space();
            DrawResults();

            EditorGUILayout.EndScrollView();
        }

        private void DrawOutputSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

#if UNITY_EDITOR_WIN
            using (new EditorGUI.DisabledScope(_masterHandle != 0 || _asioDeviceNames.Length == 0))
            {
                _useAsio = EditorGUILayout.Toggle("Use ASIO", _useAsio);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_masterHandle != 0 || !_useAsio))
                {
                    _asioDevice = EditorGUILayout.Popup("ASIO device", _asioDevice, _asioDeviceNames);
                }

                using (new EditorGUI.DisabledScope(_masterHandle != 0))
                {
                    if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    {
                        RefreshAsioDevices();
                    }
                }
            }

            if (_asioDeviceNames.Length == 0)
            {
                EditorGUILayout.HelpBox("No ASIO drivers found.", MessageType.Warning);
            }
            else if (_useAsio)
            {
                using (new EditorGUI.DisabledScope(_masterHandle != 0))
                {
                    _asioOutputTransport = (AsioOutputTransport) EditorGUILayout.EnumPopup(
                        "ASIO output transport", _asioOutputTransport);
                }

                EditorGUILayout.HelpBox(
                    "Playback buffer setting only applies to standard BASS output. ASIO driver " +
                    "controls hardware buffering. Compare CustomCallback against ChannelEnableBass " +
                    "with identical song, tempo, seek, and ASIO buffer settings.",
                    MessageType.Info);
            }
#else
            EditorGUILayout.HelpBox("ASIO output is only available in Windows editor.", MessageType.Info);
#endif
        }

        private void RefreshAsioDevices()
        {
#if UNITY_EDITOR_WIN
            try
            {
                int deviceCount = BassAsio.DeviceCount;
                _asioDeviceNames = new string[deviceCount];
                for (int i = 0; i < deviceCount; i++)
                {
                    _asioDeviceNames[i] = BassAsio.GetDeviceInfo(i).Name;
                }

                _asioDevice = Mathf.Clamp(_asioDevice, 0, Math.Max(0, deviceCount - 1));
                if (deviceCount == 0)
                {
                    _useAsio = false;
                }
            }
            catch (Exception exception)
            {
                _asioDeviceNames = Array.Empty<string>();
                _useAsio = false;
                SetError($"Failed to enumerate ASIO devices: {exception.Message}");
            }
#endif
        }

        private void DrawAudioFilePicker()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Audio file");
                EditorGUILayout.SelectableLabel(_audioPath, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));

                using (new EditorGUI.DisabledScope(_masterHandle != 0))
                {
                    if (GUILayout.Button("Browse", GUILayout.Width(70)))
                    {
                        string path = EditorUtility.OpenFilePanel("Select test audio", string.Empty,
                            "wav,ogg,mp3,aiff");
                        if (!string.IsNullOrEmpty(path))
                        {
                            _audioPath = path;
                        }
                    }
                }
            }
        }

        private void DrawPlaybackControls()
        {
            using (new EditorGUI.DisabledScope(_masterHandle == 0))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_runningTempoLatencyTrials))
                    {
                        if (GUILayout.Button("Play source"))
                        {
                            PlaySource();
                            BeginTransitionCapture("Initial play");
                        }

                        if (GUILayout.Button("Pause source"))
                        {
                            SetSourcePaused(true);
                        }
                    }
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.FloatField("Current tempo (%)", _tempo);
                }
                using (new EditorGUI.DisabledScope(_runningTempoLatencyTrials))
                {
                    _tempoCommand = Mathf.Clamp(
                        EditorGUILayout.FloatField("Command tempo (%)", _tempoCommand), 5, 2000);
                    if (GUILayout.Button("Apply tempo command"))
                    {
                        ChangeTempo(_tempoCommand);
                    }
                }
                using (new EditorGUI.DisabledScope(!_useAsio ||
                                                   _asioOutputTransport != AsioOutputTransport.CustomCallback ||
                                                   _runningTempoLatencyTrials))
                {
                    if (GUILayout.Button($"Measure tempo command latency ({TEMPO_LATENCY_TRIAL_COUNT} trials)"))
                    {
                        BeginTempoLatencyTrials();
                    }
                }

                _seekPosition = EditorGUILayout.Slider("Seek (seconds)", (float) _seekPosition, 0,
                    (float) Math.Max(0, _length));
                if (GUILayout.Button("Pause, seek source, and resume"))
                {
                    SeekSource(_seekPosition);
                }

                if (GUILayout.Button("Quickplay-style restart"))
                {
                    ResetRelativeSource();
                }
            }

            EditorGUILayout.HelpBox(
                "Quickplay-style restart preserves current song position, pauses and resets the tempo " +
                "source, then resumes it into the continuously-running master. Raw error shows BASS's " +
                "reported position; origin-adjusted error reproduces the rejected normalization model.",
                MessageType.Info);
        }

        private void DrawResults()
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, _status.StartsWith("Error:")
                ? MessageType.Error
                : MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("ASIO output transport", _useAsio
                    ? _asioOutputTransport.ToString()
                    : "BASS output");
                EditorGUILayout.DoubleField("BASS source cursor (s)", _heardPosition);
                EditorGUILayout.DoubleField("MixerLatency position (s)",
                    _estimatedHeardPosition);
                EditorGUILayout.DoubleField("Explicit-delay position (s)",
                    _explicitDelayedPosition);
                EditorGUILayout.DoubleField("Native position difference (ms)",
                    (_estimatedHeardPosition - _explicitDelayedPosition) * 1000);
                EditorGUILayout.DoubleField("Decode position", _decodePosition);
                EditorGUILayout.DoubleField("Normalized source position", _normalizedPosition);
                EditorGUILayout.DoubleField("Master buffered seconds", _masterBufferedSeconds);
                EditorGUILayout.LongField("Captured source origin", _sourcePositionOrigin);
                EditorGUILayout.DoubleField("Relative song anchor", _positionAnchor);
                EditorGUILayout.DoubleField("Raw anchored position", _rawAnchoredPosition);
                EditorGUILayout.DoubleField("Origin-adjusted position", _anchoredPosition);
                EditorGUILayout.DoubleField("MixerLatency anchored position",
                    _estimatedAnchoredPosition);
                EditorGUILayout.DoubleField("Explicit-delay anchored position",
                    _explicitAnchoredPosition);
                EditorGUILayout.DoubleField("Expected position", _expectedPosition);
                EditorGUILayout.DoubleField("Raw transition error (ms)", _rawTransitionError * 1000);
                EditorGUILayout.DoubleField("Origin-adjusted error (ms)", _transitionError * 1000);
                EditorGUILayout.DoubleField("MixerLatency error (ms)",
                    _estimatedTransitionError * 1000);
                EditorGUILayout.DoubleField("Explicit-delay error (ms)",
                    _explicitTransitionError * 1000);
                EditorGUILayout.IntField("Transition samples", _transitionSampleCount);
                EditorGUILayout.DoubleField("Cursor mean absolute error (ms)",
                    _transitionSampleCount > 0
                        ? _transitionAbsoluteErrorTotal / _transitionSampleCount * 1000
                        : 0);
                EditorGUILayout.DoubleField("Cursor maximum absolute error (ms)",
                    _transitionMaximumAbsoluteError * 1000);
                EditorGUILayout.DoubleField("MixerLatency mean absolute error (ms)",
                    _transitionSampleCount > 0
                        ? _estimatedTransitionAbsoluteErrorTotal / _transitionSampleCount * 1000
                        : 0);
                EditorGUILayout.DoubleField("MixerLatency maximum absolute error (ms)",
                    _estimatedTransitionMaximumAbsoluteError * 1000);
                EditorGUILayout.IntField("Buffered source bytes", _availableSourceBytes);
                EditorGUILayout.IntField("FFT bytes read", _fftResult);
                EditorGUILayout.FloatField("FFT peak magnitude", _fftPeak);
                EditorGUILayout.IntField("FFT peak bin", _fftPeakBin);
                EditorGUILayout.FloatField("FFT change", _fftChange);
                EditorGUILayout.Toggle("Level succeeded", _levelResult >= 0);
                EditorGUILayout.FloatField("RMS level", _levelData[0]);
                EditorGUILayout.IntField("Sample-data bytes read", _sampleResult);
                EditorGUILayout.FloatField("Sample peak", _samplePeak);
            }

            DrawAsioTimingResults();
            DrawPositionAccuracyGraph();

            EditorGUILayout.HelpBox(
                "Compare MixerLatency and explicit-delay positions first: they should overlap through " +
                "play, pause, seek, and tempo changes. BASS source cursor is mixer output-edge position. " +
                "Absolute speaker validation still requires loopback. Automated tempo timing requires " +
                "CustomCallback; run tempo changes manually in direct mode.",
                MessageType.Info);
        }

        private void DrawAsioTimingResults()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("ASIO command timing", EditorStyles.boldLabel);

            if (!_useAsio)
            {
                EditorGUILayout.HelpBox(
                    "Select ASIO before creating graph to collect callback-level command timing.",
                    MessageType.Info);
                return;
            }

            double callbackMilliseconds;
            double latestSpeed;
            string playResult;
            string tempoAffectedResult;
            string tempoChangedResult;
            lock (_asioCallbackLock)
            {
                callbackMilliseconds = _asioTiming.CallbackMilliseconds;
                latestSpeed = _asioTiming.LatestSpeed;
                playResult = _asioTiming.PlayResult;
                tempoAffectedResult = _asioTiming.TempoAffectedResult;
                tempoChangedResult = _asioTiming.TempoChangedResult;
            }

            double latencyMilliseconds = _asioSampleRate > 0
                ? _asioLatencyFrames * 1000.0 / _asioSampleRate
                : 0;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.DoubleField("ASIO callback period (ms)", callbackMilliseconds);
                EditorGUILayout.DoubleField("ASIO reported latency (ms)", latencyMilliseconds);
                EditorGUILayout.DoubleField("Latest generated speed", latestSpeed);
                EditorGUILayout.TextField("Play → position change", playResult);
                EditorGUILayout.TextField("Tempo → first affected block", tempoAffectedResult);
                EditorGUILayout.TextField("Tempo → fully changed block", tempoChangedResult);
                EditorGUILayout.TextField("Automated tempo latency", _tempoLatencyTrialResult);
                EditorGUILayout.TextField("Automated A → B average", _tempoAToBTrialResult);
                EditorGUILayout.TextField("Automated B → A average", _tempoBToATrialResult);
            }

            EditorGUILayout.HelpBox(
                "CustomCallback timestamps generated blocks. ChannelEnableBass has no managed output " +
                "callback. MixerPositionEx maps ASIO-latency-delayed mixer output back to source " +
                "positions. Validate final physical-output timing with loopback or known clicks.",
                MessageType.Info);
        }

        private void DrawPositionAccuracyGraph()
        {
            if (!_useAsio)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Position accuracy", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Zero line means expected position. Blue = raw output-edge cursor. Green = automatic " +
                "MixerLatency compensation. Yellow = explicit ASIO-latency delay. Orange = automatic " +
                "result from previous run using other transport.",
                MessageType.None);

            List<PositionSample> current = _positionSamples.Count > 1
                ? _positionSamples
                : _asioOutputTransport == AsioOutputTransport.CustomCallback
                    ? _lastCustomPositionSamples
                    : _lastDirectPositionSamples;
            List<PositionSample> reference = _asioOutputTransport == AsioOutputTransport.CustomCallback
                ? _lastDirectPositionSamples
                : _lastCustomPositionSamples;

            Rect graph = GUILayoutUtility.GetRect(100, 190, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(graph, EditorGUIUtility.isProSkin
                ? new Color(0.11f, 0.11f, 0.11f)
                : new Color(0.85f, 0.85f, 0.85f));

            if (current.Count < 2)
            {
                GUI.Label(graph, "Waiting for capture data...",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            double measuredMinimum = double.PositiveInfinity;
            double measuredMaximum = double.NegativeInfinity;
            var scaleValues = new List<double>(current.Count * 3 + reference.Count);
            for (int i = 0; i < current.Count; i++)
            {
                scaleValues.Add(current[i].CursorErrorMilliseconds);
                scaleValues.Add(current[i].EstimatedErrorMilliseconds);
                scaleValues.Add(current[i].ExplicitErrorMilliseconds);
                measuredMinimum = Math.Min(measuredMinimum,
                    Math.Min(current[i].ExplicitErrorMilliseconds,
                        Math.Min(current[i].EstimatedErrorMilliseconds,
                            current[i].CursorErrorMilliseconds)));
                measuredMaximum = Math.Max(measuredMaximum,
                    Math.Max(current[i].ExplicitErrorMilliseconds,
                        Math.Max(current[i].EstimatedErrorMilliseconds,
                            current[i].CursorErrorMilliseconds)));
            }
            for (int i = 0; i < reference.Count; i++)
            {
                scaleValues.Add(reference[i].EstimatedErrorMilliseconds);
                measuredMinimum = Math.Min(measuredMinimum,
                    reference[i].EstimatedErrorMilliseconds);
                measuredMaximum = Math.Max(measuredMaximum,
                    reference[i].EstimatedErrorMilliseconds);
            }
            scaleValues.Sort();
            int lowerScaleIndex = Mathf.FloorToInt((scaleValues.Count - 1) * 0.01f);
            int upperScaleIndex = Mathf.CeilToInt((scaleValues.Count - 1) * 0.99f);
            double minimumError = Math.Min(scaleValues[lowerScaleIndex], -2);
            double maximumError = Math.Max(scaleValues[upperScaleIndex], 2);
            double errorRange = maximumError - minimumError;
            double startTime = current[0].Elapsed;
            double timeRange = current[current.Count - 1].Elapsed - startTime;

            Rect plot = new(graph.x + 85, graph.y + 10,
                graph.width - 95, graph.height - 32);

            if (Event.current.type == EventType.Repaint)
            {
                float zeroY = MapPositionToGraph(0, plot, minimumError, errorRange);
                Handles.color = new Color(0.55f, 0.55f, 0.55f, 0.6f);
                Handles.DrawLine(new Vector3(plot.x, zeroY),
                    new Vector3(plot.xMax, zeroY));

                if (reference.Count > 1)
                {
                    DrawPositionLine(reference, plot, startTime, timeRange,
                        sample => sample.EstimatedErrorMilliseconds,
                        new Color(1f, 0.55f, 0.1f), minimumError, errorRange);
                }
                DrawPositionLine(current, plot, startTime, timeRange,
                    sample => sample.CursorErrorMilliseconds,
                    new Color(0.25f, 0.55f, 1f), minimumError, errorRange);
                DrawPositionLine(current, plot, startTime, timeRange,
                    sample => sample.ExplicitErrorMilliseconds,
                    new Color(1f, 0.9f, 0.2f), minimumError, errorRange, 4);
                DrawPositionLine(current, plot, startTime, timeRange,
                    sample => sample.EstimatedErrorMilliseconds,
                    new Color(0.25f, 1f, 0.4f), minimumError, errorRange, 2);
            }

            GUI.Label(new Rect(graph.x + 2, plot.y - 7, 80, 16),
                $"{maximumError:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(graph.x + 2, plot.yMax - 8, 80, 16),
                $"{minimumError:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(plot.x, plot.yMax + 2, 80, 16),
                $"{startTime:F1} s", EditorStyles.miniLabel);
            GUI.Label(new Rect(plot.xMax - 80, plot.yMax + 2, 80, 16),
                $"{current[current.Count - 1].Elapsed:F1} s", new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.UpperRight
                });
            GUI.Label(new Rect(graph.x + 2, graph.y + graph.height / 2 - 8, 80, 16),
                "ms", EditorStyles.miniLabel);

            EditorGUILayout.LabelField(
                $"Full observed error: {measuredMinimum:F3} to {measuredMaximum:F3} ms. " +
                "Graph scale excludes outer 1% so seek discontinuities do not flatten steady data.",
                EditorStyles.wordWrappedMiniLabel);

            DrawNativePositionDifferenceGraph(current, startTime, timeRange);
        }

        private static void DrawNativePositionDifferenceGraph(IReadOnlyList<PositionSample> samples,
            double startTime, double timeRange)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Native compensation agreement", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Automatic MixerLatency position minus explicit-delay position. Flat at 0 ms means " +
                "both BASS APIs agree exactly.", MessageType.None);

            double measuredMinimum = double.PositiveInfinity;
            double measuredMaximum = double.NegativeInfinity;
            for (int i = 0; i < samples.Count; i++)
            {
                measuredMinimum = Math.Min(measuredMinimum,
                    samples[i].NativeDifferenceMilliseconds);
                measuredMaximum = Math.Max(measuredMaximum,
                    samples[i].NativeDifferenceMilliseconds);
            }

            double maximumMagnitude = Math.Max(0.05,
                Math.Max(Math.Abs(measuredMinimum), Math.Abs(measuredMaximum)));
            double minimumDifference = -maximumMagnitude;
            double differenceRange = maximumMagnitude * 2;
            Rect graph = GUILayoutUtility.GetRect(100, 120, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(graph, EditorGUIUtility.isProSkin
                ? new Color(0.11f, 0.11f, 0.11f)
                : new Color(0.85f, 0.85f, 0.85f));
            Rect plot = new(graph.x + 85, graph.y + 10,
                graph.width - 95, graph.height - 32);

            if (Event.current.type == EventType.Repaint)
            {
                float zeroY = MapPositionToGraph(0, plot, minimumDifference, differenceRange);
                Handles.color = new Color(0.55f, 0.55f, 0.55f, 0.6f);
                Handles.DrawLine(new Vector3(plot.x, zeroY), new Vector3(plot.xMax, zeroY));
                DrawPositionLine(samples, plot, startTime, timeRange,
                    sample => sample.NativeDifferenceMilliseconds,
                    new Color(0.8f, 0.4f, 1f), minimumDifference, differenceRange);
            }

            GUI.Label(new Rect(graph.x + 2, plot.y - 7, 80, 16),
                $"{maximumMagnitude:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(graph.x + 2, plot.yMax - 8, 80, 16),
                $"{-maximumMagnitude:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(plot.x, plot.yMax + 2, 80, 16),
                $"{startTime:F1} s", EditorStyles.miniLabel);
            GUI.Label(new Rect(plot.xMax - 80, plot.yMax + 2, 80, 16),
                $"{samples[samples.Count - 1].Elapsed:F1} s",
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperRight });
            GUI.Label(new Rect(graph.x + 2, graph.y + graph.height / 2 - 8, 80, 16),
                "ms", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"Automatic - explicit: {measuredMinimum:F3} to {measuredMaximum:F3} ms.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawPositionLine(IReadOnlyList<PositionSample> samples, Rect plot,
            double startTime, double timeRange, Func<PositionSample, double> value,
            Color color, double minimumError, double errorRange, float width = 2)
        {
            var points = new Vector3[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                float normalizedTime = timeRange > 0
                    ? (float) ((samples[i].Elapsed - startTime) / timeRange)
                    : 0;
                points[i] = new Vector3(
                    Mathf.Lerp(plot.x, plot.xMax, normalizedTime),
                    MapPositionToGraph(value(samples[i]), plot, minimumError, errorRange));
            }

            Handles.color = color;
            Handles.DrawAAPolyLine(width, points);
        }

        private static float MapPositionToGraph(double position, Rect plot,
            double minimumPosition, double positionRange)
        {
            float normalizedPosition = Mathf.Clamp01(
                (float) ((position - minimumPosition) / positionRange));
            return Mathf.Lerp(plot.yMax, plot.y, normalizedPosition);
        }

        private void CreateGraph()
        {
            DisposeGraph();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SetError("Exit play mode before running this test.");
                return;
            }

            if (!File.Exists(_audioPath))
            {
                SetError("Selected audio file does not exist.");
                return;
            }

            if (Bass.CurrentDevice != -1)
            {
                SetError("BASS is already initialized. Close other BASS users or restart the editor.");
                return;
            }

            Bass.PlaybackBufferLength = _bufferLength;
            int bassDevice = _useAsio ? 0 : -1;
            var initFlags = _useAsio ? DeviceInitFlags.Default : DeviceInitFlags.Latency;
            if (!Bass.Init(bassDevice, 44100, initFlags, IntPtr.Zero))
            {
                SetBassError("Failed to initialize BASS");
                return;
            }
            _ownsBass = true;

            _sourceHandle = Bass.CreateStream(_audioPath, 0, 0,
                BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (_sourceHandle == 0)
            {
                SetBassError("Failed to create source stream");
                DisposeGraph();
                return;
            }

            _tempoHandle = BassFx.TempoCreate(_sourceHandle,
                BassFlags.Decode | BassFlags.FxFreeSource);
            if (_tempoHandle == 0)
            {
                SetBassError("Failed to create tempo stream");
                DisposeGraph();
                return;
            }
            _sourceHandle = 0; // Tempo stream now owns the source stream.

            var tempoInfo = Bass.ChannelGetInfo(_tempoHandle);
            var masterFlags = BassFlags.Float | BassFlags.MixerNonStop;
            if (_useAsio)
            {
                masterFlags |= BassFlags.Decode | BassFlags.MixerPositionEx;
            }
            _masterHandle = BassMix.CreateMixerStream(tempoInfo.Frequency, tempoInfo.Channels,
                masterFlags);
            if (_masterHandle == 0)
            {
                SetBassError("Failed to create master mixer");
                DisposeGraph();
                return;
            }

            var sourceFlags = BassFlags.MixerChanNoRampin |
                BassFlags.MixerChanBuffer |
                BassFlags.MixerChanPause;
            if (!BassMix.MixerAddChannel(_masterHandle, _tempoHandle, sourceFlags))
            {
                SetBassError("Failed to add tempo stream to master mixer");
                DisposeGraph();
                return;
            }

            if (!_useAsio && !Bass.ChannelSetAttribute(_masterHandle, ChannelAttribute.Buffer,
                    _bufferLength / 1000f))
            {
                SetBassError("Failed to set master playback buffer");
                DisposeGraph();
                return;
            }

            ApplyTempo(_tempo);
            _length = GetLengthSeconds(_tempoHandle);
            _seekPosition = 0;

            if (_useAsio ? !StartAsio(tempoInfo) : !Bass.ChannelPlay(_masterHandle))
            {
                if (!_status.StartsWith("Error:"))
                {
                    SetBassError("Failed to start master mixer");
                }
                DisposeGraph();
                return;
            }

            ResetSourcePositionOrigin();

            string output = _useAsio ? $"ASIO device '{_asioDeviceNames[_asioDevice]}'" : "BASS output";
            _status = $"Graph running through {output}. Master outputs silence until Play source is pressed.";
            UpdateStatus();
        }

        private bool StartAsio(ChannelInfo mixerInfo)
        {
#if UNITY_EDITOR_WIN
            if (!BassAsio.Init(_asioDevice, AsioInitFlags.Thread))
            {
                SetAsioError("Failed to initialize ASIO device");
                return false;
            }
            _ownsAsio = true;

            if (!BassAsio.CheckRate(mixerInfo.Frequency))
            {
                SetAsioError($"ASIO device does not support {mixerInfo.Frequency} Hz");
                return false;
            }
            BassAsio.Rate = mixerInfo.Frequency;

            if (_asioOutputTransport == AsioOutputTransport.ChannelEnableBass)
            {
                if (!BassAsio.ChannelEnableBass(false, 0, _masterHandle, true))
                {
                    SetAsioError("Failed to route master mixer through ChannelEnableBass");
                    return false;
                }
            }
            else
            {
                if (!BassAsio.ChannelEnable(false, 0, _asioCallback, IntPtr.Zero))
                {
                    SetAsioError("Failed to route master mixer to ASIO");
                    return false;
                }

                for (int channel = 1; channel < mixerInfo.Channels; channel++)
                {
                    if (!BassAsio.ChannelJoin(false, channel, 0))
                    {
                        SetAsioError($"Failed to join ASIO output channel {channel}");
                        return false;
                    }
                }
            }

            if (_asioOutputTransport == AsioOutputTransport.CustomCallback &&
                (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float) ||
                 !BassAsio.ChannelSetRate(false, 0, mixerInfo.Frequency)))
            {
                SetAsioError("Failed to configure ASIO output format");
                return false;
            }

            _asioSampleRate = mixerInfo.Frequency;
            _asioBytesPerFrame = mixerInfo.Channels * sizeof(float);
            _lastAsioPosition = Math.Max(0,
                BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes, 0));

            if (!BassAsio.Start(0, 0))
            {
                SetAsioError("Failed to start ASIO output");
                return false;
            }

            _asioLatencyFrames = Math.Max(0, BassAsio.GetLatency(false));
            double latencySeconds = _asioSampleRate > 0
                ? _asioLatencyFrames / (double) _asioSampleRate
                : 0;
            long latencyBytes = Bass.ChannelSeconds2Bytes(_masterHandle, latencySeconds);
            if (latencyBytes < 0 || latencyBytes > int.MaxValue)
            {
                SetBassError("Failed to convert ASIO latency to mixer bytes");
                return false;
            }
            _asioLatencyBytes = (int) latencyBytes;

            if (!Bass.ChannelSetAttribute(_masterHandle, ChannelAttribute.MixerLatency,
                    (float) latencySeconds))
            {
                SetBassError("Failed to set mixer output latency");
                return false;
            }

            return true;
#else
            SetError("ASIO output is only available on Windows.");
            return false;
#endif
        }

        private int FillAsioBuffer(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            long timestamp = Stopwatch.GetTimestamp();
            long blockStartPosition;
            lock (_asioCallbackLock)
            {
                blockStartPosition = _lastAsioPosition;
            }

            int bytesRead = Bass.ChannelGetData(_masterHandle, buffer, length);
            if (bytesRead < 0)
            {
                bytesRead = 0;
            }

            long blockEndPosition = BassMix.ChannelGetPosition(
                _tempoHandle, PositionFlags.Bytes, 0);
            if (blockEndPosition >= 0)
            {
                lock (_asioCallbackLock)
                {
                    _lastAsioPosition = blockEndPosition;
                    int frameCount = _asioBytesPerFrame > 0 ? length / _asioBytesPerFrame : 0;
                    _asioTiming.AddBlock(timestamp, blockStartPosition, blockEndPosition,
                        frameCount, _asioSampleRate, _asioBytesPerFrame);
                }
            }

            return bytesRead;
        }

        private void PlaySource()
        {
            if (!_useAsio)
            {
                SetSourcePaused(false);
                return;
            }

            lock (_asioCallbackLock)
            {
                long baseline = Math.Max(0,
                    BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes, 0));
                long timestamp = Stopwatch.GetTimestamp();
                if (SetSourcePaused(false))
                {
                    _lastAsioPosition = baseline;
                    _asioTiming.BeginPlay(timestamp, baseline);
                }
            }
        }

        private bool SetSourcePaused(bool paused)
        {
            var flags = paused ? BassFlags.MixerChanPause : BassFlags.Default;
            if (BassMix.ChannelFlags(_tempoHandle, flags, BassFlags.MixerChanPause) < 0)
            {
                SetBassError(paused ? "Failed to pause tempo source" : "Failed to resume tempo source");
                return false;
            }

            _status = paused
                ? "Tempo source paused; master remains running."
                : "Tempo source playing through master.";
            return true;
        }

        private void SeekSource(double position)
        {
            SetSourcePaused(true);
            long positionBytes = Bass.ChannelSeconds2Bytes(_tempoHandle, position);
            if (positionBytes < 0 ||
                !BassMix.ChannelSetPosition(_tempoHandle, positionBytes, PositionFlags.Bytes))
            {
                SetBassError("Failed to seek tempo source");
                return;
            }

            SetSourcePaused(false);
            _status = $"Tempo source sought to {position:F3}s and resumed; master was not reset.";
            BeginTransitionCapture($"Seek to {position:F3}s");
        }

        private void ResetRelativeSource()
        {
            UpdatePositions();
            double anchor = _anchoredPosition;

            SetSourcePaused(true);
            if (!BassMix.ChannelSetPosition(_tempoHandle, 0, PositionFlags.Bytes))
            {
                SetBassError("Failed to reset relative tempo source position");
                return;
            }

            _positionAnchor = anchor;
            ResetSourcePositionOrigin();
            SetSourcePaused(false);
            BeginTransitionCapture("Relative reset");
            _status = $"Tempo source reset to zero with song anchor {anchor:F3}s; master was not reset.";
        }

        private void BeginTransitionCapture(string name)
        {
            UpdatePositions();
            _transitionName = name;
            _transitionStartedAt = EditorApplication.timeSinceStartup;
            _transitionExpectedStart = _anchoredPosition >= 0
                ? _anchoredPosition
                : _positionAnchor;
            _transitionLog =
                "elapsed\theard\tdecode\tnormalized\tmaster buffer\traw anchored\tanchored\texpected" +
                "\traw error ms\terror ms\tmixer latency\tmixer latency anchored" +
                "\tmixer latency error ms\texplicit delayed\texplicit anchored" +
                "\texplicit error ms\n";
            _transitionAbsoluteErrorTotal = 0;
            _transitionMaximumAbsoluteError = 0;
            _estimatedTransitionAbsoluteErrorTotal = 0;
            _estimatedTransitionMaximumAbsoluteError = 0;
            _transitionSampleCount = 0;
            _positionSamples.Clear();
            _positionSamplesSaved = false;
            _captureTransition = true;
            UpdateStatus();
        }

        private bool ChangeTempo(float tempo)
        {
            float oldTempo = _tempo;
            if (!_useAsio)
            {
                if (ApplyTempo(tempo))
                {
                    _tempo = tempo;
                    return true;
                }
                return false;
            }

            lock (_asioCallbackLock)
            {
                long timestamp = Stopwatch.GetTimestamp();
                if (ApplyTempo(tempo))
                {
                    _tempo = tempo;
                    _asioTiming.BeginTempo(timestamp, oldTempo / 100.0, tempo / 100.0);
                    return true;
                }
            }
            return false;
        }

        private void BeginTempoLatencyTrials()
        {
            if (_asioOutputTransport != AsioOutputTransport.CustomCallback)
            {
                _tempoLatencyTrialResult = "requires CustomCallback transport";
                return;
            }

            if (Mathf.Approximately(_tempo, _tempoCommand))
            {
                _tempoLatencyTrialResult = "choose command tempo different from current tempo";
                return;
            }

            if (BassMix.ChannelHasFlag(_tempoHandle, BassFlags.MixerChanPause))
            {
                _tempoLatencyTrialResult = "play source before starting measurement";
                return;
            }

            _tempoTrialA = _tempo;
            _tempoTrialB = _tempoCommand;
            _completedTempoLatencyTrials = 0;
            _tempoAffectedTotalMilliseconds = 0;
            _tempoChangedTotalMilliseconds = 0;
            _tempoAToBTotalMilliseconds = 0;
            _tempoBToATotalMilliseconds = 0;
            _tempoAToBTrialCount = 0;
            _tempoBToATrialCount = 0;
            _runningTempoLatencyTrials = true;
            _waitingForTempoTrial = false;
            _nextTempoTrialAt = 0;
            _tempoLatencyTrialResult = $"running 0/{TEMPO_LATENCY_TRIAL_COUNT}";
            _tempoAToBTrialResult = $"{_tempoTrialA:F1}% → {_tempoTrialB:F1}%";
            _tempoBToATrialResult = $"{_tempoTrialB:F1}% → {_tempoTrialA:F1}%";
            StartNextTempoTrial();
        }

        private void UpdateTempoLatencyTrials()
        {
            if (!_runningTempoLatencyTrials)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (_waitingForTempoTrial)
            {
                bool complete;
                double affectedMilliseconds;
                double changedMilliseconds;
                lock (_asioCallbackLock)
                {
                    complete = _asioTiming.TempoComplete;
                    affectedMilliseconds = _asioTiming.TempoAffectedMilliseconds;
                    changedMilliseconds = _asioTiming.TempoChangedMilliseconds;
                }

                if (!complete)
                {
                    if (now >= _tempoTrialDeadline)
                    {
                        _runningTempoLatencyTrials = false;
                        _tempoLatencyTrialResult =
                            $"timed out after {_completedTempoLatencyTrials}/{TEMPO_LATENCY_TRIAL_COUNT} trials";
                    }
                    return;
                }

                _waitingForTempoTrial = false;
                _completedTempoLatencyTrials++;
                _tempoAffectedTotalMilliseconds += affectedMilliseconds;
                _tempoChangedTotalMilliseconds += changedMilliseconds;
                if (_currentTrialIsAToB)
                {
                    _tempoAToBTotalMilliseconds += changedMilliseconds;
                    _tempoAToBTrialCount++;
                }
                else
                {
                    _tempoBToATotalMilliseconds += changedMilliseconds;
                    _tempoBToATrialCount++;
                }

                if (_completedTempoLatencyTrials >= TEMPO_LATENCY_TRIAL_COUNT)
                {
                    _runningTempoLatencyTrials = false;
                    _tempoLatencyTrialResult = string.Format(
                        "average {0:F3} ms fully changed; {1:F3} ms first affected",
                        _tempoChangedTotalMilliseconds / _completedTempoLatencyTrials,
                        _tempoAffectedTotalMilliseconds / _completedTempoLatencyTrials);
                    _tempoAToBTrialResult = FormatDirectionalTempoAverage(
                        _tempoTrialA, _tempoTrialB, _tempoAToBTotalMilliseconds,
                        _tempoAToBTrialCount);
                    _tempoBToATrialResult = FormatDirectionalTempoAverage(
                        _tempoTrialB, _tempoTrialA, _tempoBToATotalMilliseconds,
                        _tempoBToATrialCount);
                    return;
                }

                _nextTempoTrialAt = now + TEMPO_TRIAL_SETTLE_SECONDS;
                _tempoLatencyTrialResult =
                    $"running {_completedTempoLatencyTrials}/{TEMPO_LATENCY_TRIAL_COUNT}";
            }

            if (!_waitingForTempoTrial && now >= _nextTempoTrialAt)
            {
                StartNextTempoTrial();
            }
        }

        private void StartNextTempoTrial()
        {
            _currentTrialIsAToB = Mathf.Approximately(_tempo, _tempoTrialA);
            float target = _currentTrialIsAToB ? _tempoTrialB : _tempoTrialA;
            if (!ChangeTempo(target))
            {
                _runningTempoLatencyTrials = false;
                _tempoLatencyTrialResult = "tempo command failed";
                return;
            }

            _waitingForTempoTrial = true;
            _tempoTrialDeadline = EditorApplication.timeSinceStartup + TEMPO_TRIAL_TIMEOUT_SECONDS;
        }

        private static string FormatDirectionalTempoAverage(float from, float to,
            double totalMilliseconds, int trialCount)
        {
            return trialCount > 0
                ? $"{from:F1}% → {to:F1}%: {totalMilliseconds / trialCount:F3} ms ({trialCount} trials)"
                : "not measured";
        }

        private bool ApplyTempo(float tempo)
        {
            float relativeTempo = tempo - 100;
            if (!Bass.ChannelSetAttribute(_tempoHandle, ChannelAttribute.Tempo, relativeTempo))
            {
                SetBassError("Failed to set tempo");
                return false;
            }
            return true;
        }

        private void OnEditorUpdate()
        {
            if (_masterHandle == 0 || EditorApplication.timeSinceStartup < _nextStatusUpdate)
            {
                return;
            }

            double updateInterval = _captureTransition
                ? TRANSITION_UPDATE_INTERVAL
                : STATUS_UPDATE_INTERVAL;
            _nextStatusUpdate = EditorApplication.timeSinceStartup + updateInterval;
            UpdateStatus();
            UpdateTempoLatencyTrials();
            _repaint();
        }

        private void UpdateStatus()
        {
            UpdatePositions();

            _availableSourceBytes = BassMix.ChannelGetData(_tempoHandle, IntPtr.Zero,
                (int) DataFlags.Available);
            _fftResult = BassMix.ChannelGetData(_tempoHandle, _fftData, (int) DataFlags.FFT2048);
            UpdateFftMetrics();
            _levelResult = BassMix.ChannelGetLevel(_tempoHandle, _levelData, 0.05f,
                LevelRetrievalFlags.Mono | LevelRetrievalFlags.RMS);
            _sampleResult = BassMix.ChannelGetData(_tempoHandle, _sampleData,
                _sampleData.Length * sizeof(float) | (int) DataFlags.Float);
            _samplePeak = 0;
            if (_sampleResult >= 0)
            {
                int sampleCount = Math.Min(_sampleData.Length, _sampleResult / sizeof(float));
                for (int i = 0; i < sampleCount; i++)
                {
                    _samplePeak = Math.Max(_samplePeak, Math.Abs(_sampleData[i]));
                }
            }

            if (_captureTransition)
            {
                CaptureTransitionSample();
            }

            if (_heardPosition < 0 || _estimatedHeardPosition < 0 ||
                _explicitDelayedPosition < 0 || _decodePosition < 0 ||
                _availableSourceBytes < 0 || _fftResult < 0 || _levelResult < 0 ||
                _sampleResult < 0)
            {
                SetBassError("One or more source inspection calls failed");
            }
        }

        private void UpdatePositions()
        {
            long heardBytes = _useAsio
                ? BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes, 0)
                : BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes);
            _heardPosition = heardBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, heardBytes)
                : -1;

            long automaticBytes = BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes);
            _estimatedHeardPosition = automaticBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, automaticBytes)
                : -1;

            long explicitDelayedBytes = _useAsio
                ? BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes, _asioLatencyBytes)
                : automaticBytes;
            _explicitDelayedPosition = explicitDelayedBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, explicitDelayedBytes)
                : -1;

            long decodeBytes = Bass.ChannelGetPosition(_tempoHandle, PositionFlags.Decode);
            _decodePosition = decodeBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, decodeBytes)
                : -1;

            int masterBufferedBytes = Bass.ChannelGetData(_masterHandle, IntPtr.Zero,
                (int) DataFlags.Available);
            _masterBufferedSeconds = masterBufferedBytes >= 0
                ? Bass.ChannelBytes2Seconds(_masterHandle, masterBufferedBytes)
                : -1;
            long normalizedBytes = heardBytes >= 0
                ? Math.Max(0, heardBytes - _sourcePositionOrigin)
                : -1;
            _normalizedPosition = normalizedBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, normalizedBytes)
                : -1;
            _rawAnchoredPosition = _positionAnchor + _heardPosition;
            _anchoredPosition = _positionAnchor + _normalizedPosition;
            double originSeconds = _sourcePositionOrigin >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, _sourcePositionOrigin)
                : -1;
            _estimatedAnchoredPosition = _estimatedHeardPosition >= 0 && originSeconds >= 0
                ? _positionAnchor + _estimatedHeardPosition - originSeconds
                : -1;
            _explicitAnchoredPosition = _explicitDelayedPosition >= 0 && originSeconds >= 0
                ? _positionAnchor + _explicitDelayedPosition - originSeconds
                : -1;
        }

        private void ResetSourcePositionOrigin()
        {
            long position = _useAsio
                ? BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes, 0)
                : BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes);
            if (position < 0)
            {
                SetBassError("Failed to reset source position origin");
                return;
            }

            _sourcePositionOrigin = position;
        }

        private void CaptureTransitionSample()
        {
            double elapsed = EditorApplication.timeSinceStartup - _transitionStartedAt;
            _expectedPosition = _transitionExpectedStart + elapsed * (_tempo / 100f);
            _rawTransitionError = _expectedPosition - _rawAnchoredPosition;
            _transitionError = _expectedPosition - _anchoredPosition;
            _estimatedTransitionError = _expectedPosition - _estimatedAnchoredPosition;
            _explicitTransitionError = _expectedPosition - _explicitAnchoredPosition;
            _transitionLog += string.Format(
                "{0:F3}\t{1:F3}\t{2:F3}\t{3:F3}\t{4:F3}\t{5:F3}\t{6:F3}\t{7:F3}\t{8:F1}" +
                "\t{9:F1}\t{10:F3}\t{11:F3}\t{12:F1}\t{13:F3}\t{14:F3}\t{15:F1}\n",
                elapsed,
                _heardPosition,
                _decodePosition,
                _normalizedPosition,
                _masterBufferedSeconds,
                _rawAnchoredPosition,
                _anchoredPosition,
                _expectedPosition,
                _rawTransitionError * 1000,
                _transitionError * 1000,
                _estimatedHeardPosition,
                _estimatedAnchoredPosition,
                _estimatedTransitionError * 1000,
                _explicitDelayedPosition,
                _explicitAnchoredPosition,
                _explicitTransitionError * 1000);

            double absoluteError = Math.Abs(_transitionError);
            _transitionAbsoluteErrorTotal += absoluteError;
            _transitionMaximumAbsoluteError = Math.Max(_transitionMaximumAbsoluteError,
                absoluteError);
            double estimatedAbsoluteError = Math.Abs(_estimatedTransitionError);
            _estimatedTransitionAbsoluteErrorTotal += estimatedAbsoluteError;
            _estimatedTransitionMaximumAbsoluteError = Math.Max(
                _estimatedTransitionMaximumAbsoluteError, estimatedAbsoluteError);
            _transitionSampleCount++;
            _positionSamples.Add(new PositionSample(elapsed, _transitionError * 1000,
                _estimatedTransitionError * 1000, _explicitTransitionError * 1000,
                (_estimatedHeardPosition - _explicitDelayedPosition) * 1000));

            if (elapsed >= _positionCaptureLengthSeconds)
            {
                _captureTransition = false;
                SavePositionSamples();
            }
        }

        private void SavePositionSamples()
        {
            if (_positionSamplesSaved || !_useAsio || _positionSamples.Count < 2)
            {
                return;
            }

            var samples = new List<PositionSample>(_positionSamples);
            if (_asioOutputTransport == AsioOutputTransport.CustomCallback)
            {
                _lastCustomPositionSamples = samples;
            }
            else
            {
                _lastDirectPositionSamples = samples;
            }
            _positionSamplesSaved = true;
        }

        private void UpdateFftMetrics()
        {
            _fftPeak = 0;
            _fftPeakBin = 0;
            _fftChange = 0;
            if (_fftResult < 0)
            {
                return;
            }

            for (int i = 0; i < _fftData.Length; i++)
            {
                float magnitude = _fftData[i];
                if (magnitude > _fftPeak)
                {
                    _fftPeak = magnitude;
                    _fftPeakBin = i;
                }

                if (_hasPreviousFft)
                {
                    _fftChange += Math.Abs(magnitude - _previousFftData[i]);
                }
                _previousFftData[i] = magnitude;
            }
            _hasPreviousFft = true;
        }

        private static double GetLengthSeconds(int handle)
        {
            long bytes = Bass.ChannelGetLength(handle, PositionFlags.Bytes);
            return bytes >= 0 ? Bass.ChannelBytes2Seconds(handle, bytes) : 0;
        }

        private void DisposeGraph()
        {
            if (_ownsAsio)
            {
                BassAsio.Stop();
                BassAsio.Free();
                _ownsAsio = false;
            }

            if (_masterHandle != 0)
            {
                Bass.StreamFree(_masterHandle);
                _masterHandle = 0;
            }

            if (_tempoHandle != 0)
            {
                Bass.StreamFree(_tempoHandle);
                _tempoHandle = 0;
            }

            if (_sourceHandle != 0)
            {
                Bass.StreamFree(_sourceHandle);
                _sourceHandle = 0;
            }

            if (_ownsBass)
            {
                Bass.Free();
                _ownsBass = false;
            }

            _length = 0;
            _heardPosition = 0;
            _estimatedHeardPosition = 0;
            _explicitDelayedPosition = 0;
            _decodePosition = 0;
            _normalizedPosition = 0;
            _masterBufferedSeconds = 0;
            _sourcePositionOrigin = 0;
            _positionAnchor = 0;
            _rawAnchoredPosition = 0;
            _anchoredPosition = 0;
            _estimatedAnchoredPosition = 0;
            _explicitAnchoredPosition = 0;
            _expectedPosition = 0;
            _transitionExpectedStart = 0;
            _rawTransitionError = 0;
            _transitionError = 0;
            _estimatedTransitionError = 0;
            _explicitTransitionError = 0;
            _transitionAbsoluteErrorTotal = 0;
            _transitionMaximumAbsoluteError = 0;
            _estimatedTransitionAbsoluteErrorTotal = 0;
            _estimatedTransitionMaximumAbsoluteError = 0;
            _transitionSampleCount = 0;
            _transitionStartedAt = 0;
            _transitionName = "None";
            _transitionLog = string.Empty;
            _captureTransition = false;
            _positionSamples.Clear();
            _positionSamplesSaved = false;
            _availableSourceBytes = 0;
            _fftResult = 0;
            _fftPeak = 0;
            _fftPeakBin = 0;
            _fftChange = 0;
            _sampleResult = 0;
            _samplePeak = 0;
            _levelResult = 0;
            _hasPreviousFft = false;
            _asioBytesPerFrame = 0;
            _asioSampleRate = 0;
            _asioLatencyFrames = 0;
            _asioLatencyBytes = 0;
            _lastAsioPosition = 0;
            _asioTiming.Reset();
            _runningTempoLatencyTrials = false;
            _waitingForTempoTrial = false;
            _completedTempoLatencyTrials = 0;
            _tempoLatencyTrialResult = "not measured";
            _tempoAToBTrialResult = "not measured";
            _tempoBToATrialResult = "not measured";
            Array.Clear(_previousFftData, 0, _previousFftData.Length);
            Array.Clear(_levelData, 0, _levelData.Length);
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
            UnityEngine.Debug.LogError($"Master mixer test: {message}");
        }

        private readonly struct PositionSample
        {
            public readonly double Elapsed;
            public readonly double CursorErrorMilliseconds;
            public readonly double EstimatedErrorMilliseconds;
            public readonly double ExplicitErrorMilliseconds;
            public readonly double NativeDifferenceMilliseconds;

            public PositionSample(double elapsed, double cursorErrorMilliseconds,
                double estimatedErrorMilliseconds, double explicitErrorMilliseconds,
                double nativeDifferenceMilliseconds)
            {
                Elapsed = elapsed;
                CursorErrorMilliseconds = cursorErrorMilliseconds;
                EstimatedErrorMilliseconds = estimatedErrorMilliseconds;
                ExplicitErrorMilliseconds = explicitErrorMilliseconds;
                NativeDifferenceMilliseconds = nativeDifferenceMilliseconds;
            }
        }

        private sealed class AsioTimingMeasurements
        {
            private long _playTimestamp;
            private long _playBaseline;
            private bool _playPending;
            private double _playDelayMilliseconds = double.NaN;

            private long _tempoTimestamp;
            private double _oldSpeed;
            private double _targetSpeed;
            private bool _tempoPending;
            private double _tempoAffectedMilliseconds = double.NaN;
            private double _tempoChangedMilliseconds = double.NaN;

            public double CallbackMilliseconds { get; private set; }
            public double LatestSpeed { get; private set; }
            public bool TempoComplete => !_tempoPending &&
                !double.IsNaN(_tempoChangedMilliseconds);
            public double TempoAffectedMilliseconds => _tempoAffectedMilliseconds;
            public double TempoChangedMilliseconds => _tempoChangedMilliseconds;

            public string PlayResult => FormatResult(_playPending, _playDelayMilliseconds);
            public string TempoAffectedResult => FormatResult(
                _tempoPending && double.IsNaN(_tempoAffectedMilliseconds),
                _tempoAffectedMilliseconds);
            public string TempoChangedResult => FormatResult(_tempoPending,
                _tempoChangedMilliseconds);

            public void BeginPlay(long timestamp, long baseline)
            {
                _playTimestamp = timestamp;
                _playBaseline = baseline;
                _playPending = true;
                _playDelayMilliseconds = double.NaN;
            }

            public void BeginTempo(long timestamp, double oldSpeed, double targetSpeed)
            {
                _tempoTimestamp = timestamp;
                _oldSpeed = oldSpeed;
                _targetSpeed = targetSpeed;
                _tempoPending = !Approximately(oldSpeed, targetSpeed);
                _tempoAffectedMilliseconds = double.NaN;
                _tempoChangedMilliseconds = double.NaN;
            }

            public void AddBlock(long timestamp, long startPosition, long endPosition,
                int frameCount, int sampleRate, int bytesPerFrame)
            {
                if (frameCount <= 0 || sampleRate <= 0 || bytesPerFrame <= 0)
                {
                    return;
                }

                CallbackMilliseconds = frameCount * 1000.0 / sampleRate;
                if (_playPending && endPosition > _playBaseline)
                {
                    _playDelayMilliseconds = ElapsedMilliseconds(_playTimestamp, timestamp);
                    _playPending = false;
                }

                long positionDelta = endPosition - startPosition;
                if (positionDelta <= 0)
                {
                    return;
                }

                LatestSpeed = positionDelta / (double) (frameCount * bytesPerFrame);
                if (!_tempoPending)
                {
                    return;
                }

                double transition = (LatestSpeed - _oldSpeed) / (_targetSpeed - _oldSpeed);
                if (double.IsNaN(_tempoAffectedMilliseconds) && transition >= 0.05)
                {
                    _tempoAffectedMilliseconds = ElapsedMilliseconds(_tempoTimestamp, timestamp);
                }

                if (transition >= 0.95)
                {
                    _tempoChangedMilliseconds = ElapsedMilliseconds(_tempoTimestamp, timestamp);
                    _tempoPending = false;
                }
            }

            public void Reset()
            {
                _playPending = false;
                _playDelayMilliseconds = double.NaN;
                _tempoPending = false;
                _tempoAffectedMilliseconds = double.NaN;
                _tempoChangedMilliseconds = double.NaN;
                CallbackMilliseconds = 0;
                LatestSpeed = 0;
            }

            private static string FormatResult(bool pending, double milliseconds)
            {
                if (pending)
                {
                    return "waiting";
                }
                return double.IsNaN(milliseconds) ? "not measured" : $"{milliseconds:F3} ms";
            }

            private static bool Approximately(double left, double right) =>
                Math.Abs(left - right) < 0.0001;

            private static double ElapsedMilliseconds(long start, long end) =>
                (end - start) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
