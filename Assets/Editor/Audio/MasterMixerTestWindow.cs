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

        [MenuItem("YARG/Audio/Master Mixer Test")]
        private static void Open()
        {
            GetWindow<MasterMixerTestWindow>("Master Mixer Test");
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            DisposeGraph();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Standalone spike for tempo-source pause, seek, position, FFT, level, and sample-data " +
                "retrieval through an always-running master mixer. Stop play mode before using it.",
                MessageType.Info);

            DrawAudioFilePicker();

            using (new EditorGUI.DisabledScope(_masterHandle != 0))
            {
                _bufferLength = EditorGUILayout.IntSlider("Playback buffer (ms)", _bufferLength, 10, 500);
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
            }
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

            _nextStatusUpdate = EditorApplication.timeSinceStartup + STATUS_UPDATE_INTERVAL;
            UpdateStatus();
            Repaint();
        }

        private void UpdateStatus()
        {
            long heardBytes = BassMix.ChannelGetPosition(_tempoHandle, PositionFlags.Bytes);
            _heardPosition = heardBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, heardBytes)
                : -1;

            long decodeBytes = Bass.ChannelGetPosition(_tempoHandle, PositionFlags.Decode);
            _decodePosition = decodeBytes >= 0
                ? Bass.ChannelBytes2Seconds(_tempoHandle, decodeBytes)
                : -1;

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

            if (heardBytes < 0 || decodeBytes < 0 || _availableSourceBytes < 0 ||
                _fftResult < 0 || _levelResult < 0 || _sampleResult < 0)
            {
                SetBassError("One or more source inspection calls failed");
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
