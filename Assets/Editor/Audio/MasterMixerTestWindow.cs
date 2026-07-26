using System;
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
        private const double STATUS_UPDATE_INTERVAL = 0.1;
        private const double TRANSITION_UPDATE_INTERVAL = 0.02;
        private const double TRANSITION_CAPTURE_LENGTH = 8;
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
        private readonly Action _repaint;

        private string _audioPath = string.Empty;
        private string _status = "Select an audio file, then create the test graph.";
        private string[] _asioDeviceNames = Array.Empty<string>();
        private bool _useAsio;
        private int _asioDevice;
        private int _bufferLength = 75;
        private float _tempo = 100;
        private float _tempoCommand = 150;
        private double _seekPosition;
        private double _length;
        private double _heardPosition;
        private double _decodePosition;
        private double _normalizedPosition;
        private double _masterBufferedSeconds;
        private long _sourcePositionOrigin;
        private double _positionAnchor;
        private double _rawAnchoredPosition;
        private double _anchoredPosition;
        private double _expectedPosition;
        private double _rawTransitionError;
        private double _transitionError;
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
                EditorGUILayout.HelpBox(
                    "ASIO pulls decoded audio directly from master mixer. Playback buffer setting " +
                    "below only applies to standard BASS output; ASIO driver controls its buffer.",
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
                using (new EditorGUI.DisabledScope(!_useAsio || _runningTempoLatencyTrials))
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
                EditorGUILayout.DoubleField("Heard source position", _heardPosition);
                EditorGUILayout.DoubleField("Decode position", _decodePosition);
                EditorGUILayout.DoubleField("Normalized source position", _normalizedPosition);
                EditorGUILayout.DoubleField("Master buffered seconds", _masterBufferedSeconds);
                EditorGUILayout.LongField("Captured source origin", _sourcePositionOrigin);
                EditorGUILayout.DoubleField("Relative song anchor", _positionAnchor);
                EditorGUILayout.DoubleField("Raw anchored position", _rawAnchoredPosition);
                EditorGUILayout.DoubleField("Origin-adjusted position", _anchoredPosition);
                EditorGUILayout.DoubleField("Expected position", _expectedPosition);
                EditorGUILayout.DoubleField("Raw transition error (ms)", _rawTransitionError * 1000);
                EditorGUILayout.DoubleField("Origin-adjusted error (ms)", _transitionError * 1000);
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

            EditorGUILayout.LabelField($"Transition capture: {_transitionName}", EditorStyles.boldLabel);
            EditorGUILayout.TextArea(_transitionLog, GUILayout.MinHeight(100));

            EditorGUILayout.HelpBox(
                "Pass criteria: heard position stops while paused, seek lands near requested position, " +
                "and FFT/level/sample-data calls continue succeeding without interrupting audio.",
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
                "Times end at ASIO callback where mixer generates changed data. Add reported ASIO " +
                "latency for estimated physical-output time. Detection uses source-position slope; " +
                "5% and 95% of requested speed transition mark first affected and fully changed blocks.",
                MessageType.Info);
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
                masterFlags |= BassFlags.Decode;
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

            if (!BassAsio.ChannelSetFormat(false, 0, AsioSampleFormat.Float) ||
                !BassAsio.ChannelSetRate(false, 0, mixerInfo.Frequency))
            {
                SetAsioError("Failed to configure ASIO output format");
                return false;
            }

            _asioSampleRate = mixerInfo.Frequency;
            _asioBytesPerFrame = mixerInfo.Channels * sizeof(float);
            _lastAsioPosition = Math.Max(0,
                BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes));

            if (!BassAsio.Start(0, 0))
            {
                SetAsioError("Failed to start ASIO output");
                return false;
            }

            _asioLatencyFrames = Math.Max(0, BassAsio.GetLatency(false));

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

            long blockEndPosition = BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes);
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
                    BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes));
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
            UpdateStatus();
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
            _transitionName = name;
            _transitionStartedAt = EditorApplication.timeSinceStartup;
            _transitionLog =
                "elapsed\theard\tdecode\tnormalized\tmaster buffer\traw anchored\tanchored\texpected" +
                "\traw error ms\terror ms\n";
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

            if (_heardPosition < 0 || _decodePosition < 0 || _availableSourceBytes < 0 ||
                _fftResult < 0 || _levelResult < 0 || _sampleResult < 0)
            {
                SetBassError("One or more source inspection calls failed");
            }
        }

        private void UpdatePositions()
        {
            long heardBytes = BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes);
            _heardPosition = heardBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, heardBytes)
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
        }

        private void ResetSourcePositionOrigin()
        {
            long position = BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes);
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
            _expectedPosition = _positionAnchor + elapsed * (_tempo / 100f);
            _rawTransitionError = _expectedPosition - _rawAnchoredPosition;
            _transitionError = _expectedPosition - _anchoredPosition;
            _transitionLog += string.Format(
                "{0:F3}\t{1:F3}\t{2:F3}\t{3:F3}\t{4:F3}\t{5:F3}\t{6:F3}\t{7:F3}\t{8:F1}" +
                "\t{9:F1}\n",
                elapsed,
                _heardPosition,
                _decodePosition,
                _normalizedPosition,
                _masterBufferedSeconds,
                _rawAnchoredPosition,
                _anchoredPosition,
                _expectedPosition,
                _rawTransitionError * 1000,
                _transitionError * 1000);

            if (elapsed >= TRANSITION_CAPTURE_LENGTH)
            {
                _captureTransition = false;
            }
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
            _decodePosition = 0;
            _normalizedPosition = 0;
            _masterBufferedSeconds = 0;
            _sourcePositionOrigin = 0;
            _positionAnchor = 0;
            _rawAnchoredPosition = 0;
            _anchoredPosition = 0;
            _expectedPosition = 0;
            _rawTransitionError = 0;
            _transitionError = 0;
            _transitionStartedAt = 0;
            _transitionName = "None";
            _transitionLog = string.Empty;
            _captureTransition = false;
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
