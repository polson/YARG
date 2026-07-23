using System;
using System.IO;
using ManagedBass;
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
    public sealed class MasterMixerTestWindow : EditorWindow
    {
        private const double STATUS_UPDATE_INTERVAL = 0.1;
        private const double TRANSITION_UPDATE_INTERVAL = 0.02;
        private const double TRANSITION_CAPTURE_LENGTH = 8;
        private static readonly Vector2 DefaultWindowSize = new(700, 800);

        private readonly float[] _fftData = new float[1024];
        private readonly float[] _previousFftData = new float[1024];
        private readonly float[] _levelData = new float[1];
        private readonly float[] _sampleData = new float[1024];

        private string _audioPath = string.Empty;
        private string _status = "Select an audio file, then create the test graph.";
        private int _bufferLength = 75;
        private float _tempo = 100;
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
        private double _nextStatusUpdate;
        private Vector2 _scrollPosition;

        [MenuItem("YARG/Audio/Master Mixer Test")]
        private static void Open()
        {
            var window = GetWindow<MasterMixerTestWindow>("Master Mixer Test");
            window.minSize = DefaultWindowSize;
            window.position = new Rect(window.position.position, DefaultWindowSize);
            window.Show();
        }

        private void OnEnable()
        {
            minSize = DefaultWindowSize;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            DisposeGraph();
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.HelpBox(
                "Standalone spike for tempo-source pause, seek, position, FFT, level, and sample-data " +
                "retrieval through an always-running master mixer. Stop play mode before using it.",
                MessageType.Info);

            DrawAudioFilePicker();

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
                    if (GUILayout.Button("Play source"))
                    {
                        SetSourcePaused(false);
                        BeginTransitionCapture("Initial play");
                    }

                    if (GUILayout.Button("Pause source"))
                    {
                        SetSourcePaused(true);
                    }
                }

                float newTempo = EditorGUILayout.Slider("Tempo (%)", _tempo, 5, 2000);
                if (!Mathf.Approximately(newTempo, _tempo))
                {
                    _tempo = newTempo;
                    SetTempo();
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

            EditorGUILayout.LabelField($"Transition capture: {_transitionName}", EditorStyles.boldLabel);
            EditorGUILayout.TextArea(_transitionLog, GUILayout.MinHeight(100));

            EditorGUILayout.HelpBox(
                "Pass criteria: heard position stops while paused, seek lands near requested position, " +
                "and FFT/level/sample-data calls continue succeeding without interrupting audio.",
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
            if (!Bass.Init(-1, 44100, DeviceInitFlags.Latency, IntPtr.Zero))
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
            _masterHandle = BassMix.CreateMixerStream(tempoInfo.Frequency, tempoInfo.Channels,
                BassFlags.Float | BassFlags.MixerNonStop);
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

            if (!Bass.ChannelSetAttribute(_masterHandle, ChannelAttribute.Buffer,
                    _bufferLength / 1000f))
            {
                SetBassError("Failed to set master playback buffer");
                DisposeGraph();
                return;
            }

            SetTempo();
            _length = GetLengthSeconds(_tempoHandle);
            _seekPosition = 0;

            if (!Bass.ChannelPlay(_masterHandle))
            {
                SetBassError("Failed to start master mixer");
                DisposeGraph();
                return;
            }

            ResetSourcePositionOrigin();

            _status = "Graph running. Master outputs silence until Play source is pressed.";
            UpdateStatus();
        }

        private void SetSourcePaused(bool paused)
        {
            var flags = paused ? BassFlags.MixerChanPause : BassFlags.Default;
            if (BassMix.ChannelFlags(_tempoHandle, flags, BassFlags.MixerChanPause) < 0)
            {
                SetBassError(paused ? "Failed to pause tempo source" : "Failed to resume tempo source");
                return;
            }

            _status = paused
                ? "Tempo source paused; master remains running."
                : "Tempo source playing through master.";
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

        private void SetTempo()
        {
            float relativeTempo = _tempo - 100;
            if (!Bass.ChannelSetAttribute(_tempoHandle, ChannelAttribute.Tempo, relativeTempo))
            {
                SetBassError("Failed to set tempo");
            }
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
            Repaint();
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
            Array.Clear(_previousFftData, 0, _previousFftData.Length);
            Array.Clear(_levelData, 0, _levelData.Length);
        }

        private void SetBassError(string message)
        {
            SetError($"{message}: {Bass.LastError}");
        }

        private void SetError(string message)
        {
            _status = $"Error: {message}";
            Debug.LogError($"Master mixer test: {message}");
        }
    }
}
