using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEditor;
using UnityEngine;

namespace YARG.Editor
{
    /// <summary>
    /// Measures positions and latency reported by BASS and its add-ons.
    /// This owns a standalone BASS device and cannot run alongside play mode.
    /// </summary>
    internal sealed class BassLatencyTestTab
    {
        private const double MEASUREMENT_WARMUP_SECONDS = 2;
        private const int MAXIMUM_GRAPH_SAMPLES = 18_000;
        private const int GRAPH_SAMPLES_TO_TRIM = 2_000;
        private const double POSITION_SETTLE_SECONDS = 0.1;
        private const double PLAY_POSITION_TIMEOUT_SECONDS = 10;

        private readonly Action _repaint;
        private readonly Action<string> _showNotification;
        private readonly List<Vector2> _positionJitterHistory = new();

        private string _audioPath = string.Empty;
        private string _status = "Select an audio file, then start the test.";
        private int _bufferLength = 75;
        private int _deviceBufferLength = 20;
        private int _updatePeriod = 5;
        private bool _useDeviceNonStop = true;
        private bool _useVistaTruePlayPosition;
        private int _sourceHandle;
        private int _mixerHandle;
        private bool _ownsBass;
        private long _bassPositionBytes;
        private long _mixerPositionBytes;
        private double _bassPositionSeconds;
        private double _mixerPositionSeconds;
        private double _differenceMilliseconds;
        private double _minimumDifferenceMilliseconds;
        private double _maximumDifferenceMilliseconds;
        private double _differenceTotalMilliseconds;
        private int _sampleCount;
        private int _deviceLatencyMilliseconds;
        private int _actualPlaybackBufferMilliseconds;
        private int _updatePeriodMilliseconds;
        private int _minimumBufferMilliseconds;
        private int _deviceBufferMilliseconds;
        private int _devicePeriodMilliseconds;
        private bool _deviceNonStop;
        private bool _vistaTruePlayPosition;
        private double _channelBufferMilliseconds;
        private double _measurementStartsAt;
        private double _jitterAnchorTime;
        private double _jitterAnchorPosition;
        private Vector2 _scrollPosition;
        private string _playPositionLatencyResult = "not measured";
        private long _playPositionBaselineBytes;
        private long _playPositionChangedBytes;
        private long _playPositionPollCount;
        private bool _automaticPlayLatencyPending;
        private string _masterPlayPositionLatencyResult = "not measured";
        private long _masterPlayPositionBaselineBytes;
        private long _masterPlayPositionChangedBytes;
        private double _mixerUpdateCallMilliseconds;
        private double _mixerPlayCallMilliseconds;
        private string _masterPositionAfterPlayReturnResult = "not measured";
        private string _bassMixPositionAfterPlayReturnResult = "not measured";

        public BassLatencyTestTab(Action repaint, Action<string> showNotification)
        {
            _repaint = repaint;
            _showNotification = showNotification;
        }

        public void Enable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        public void Disable()
        {
            EditorApplication.update -= OnEditorUpdate;
            StopTest();
        }

        public void Draw()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.HelpBox(
                "Plays a tempo stream through a BASS mixer, then compares the tempo stream's raw " +
                "decode position from Bass.ChannelGetPosition with its playback-buffer-compensated " +
                "position from BassMix.ChannelGetPosition. Stop play mode before using it.",
                MessageType.Info);

            DrawAudioFilePicker();

            using (new EditorGUI.DisabledScope(_mixerHandle != 0))
            {
                _bufferLength = EditorGUILayout.IntSlider("Playback buffer (ms)",
                    _bufferLength, 10, 5000);
                _deviceBufferLength = EditorGUILayout.IntSlider("Device buffer (ms)",
                    _deviceBufferLength, 10, 500);
                _updatePeriod = EditorGUILayout.IntSlider("Update period (ms)",
                    _updatePeriod, 1, 100);
                _useDeviceNonStop = EditorGUILayout.Toggle("Device non-stop",
                    _useDeviceNonStop);
                _useVistaTruePlayPosition = EditorGUILayout.Toggle("Vista true position",
                    _useVistaTruePlayPosition);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           _mixerHandle != 0 || string.IsNullOrWhiteSpace(_audioPath)))
                {
                    if (GUILayout.Button("Start Test"))
                    {
                        StartTest();
                    }
                }

                using (new EditorGUI.DisabledScope(_mixerHandle == 0))
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

            using (new EditorGUI.DisabledScope(_mixerHandle == 0))
            {
                if (GUILayout.Button("Measure mixer Play → BassMix position change"))
                {
                    MeasurePlayPositionLatency();
                }
            }

            EditorGUILayout.HelpBox(
                "Measurement pauses master mixer and busy-waits until source's heard position stops. " +
                "It then updates and plays master mixer using normal YARG playback sequence, and " +
                "busy-waits for BassMix.ChannelGetPosition to change. " +
                "Editor will be unresponsive during measurement.",
                MessageType.None);

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

                using (new EditorGUI.DisabledScope(_mixerHandle != 0))
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

        private void DrawResults()
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, _status.StartsWith("Error:")
                ? MessageType.Error
                : MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LongField("Bass.ChannelGetPosition (bytes)", _bassPositionBytes);
                EditorGUILayout.DoubleField("Bass position (seconds)", _bassPositionSeconds);
                EditorGUILayout.LongField("BassMix.ChannelGetPosition (bytes)", _mixerPositionBytes);
                EditorGUILayout.DoubleField("BassMix position (seconds)", _mixerPositionSeconds);
                EditorGUILayout.DoubleField("Bass - BassMix (ms)", _differenceMilliseconds);
                EditorGUILayout.DoubleField("Mean difference (ms)", MeanDifferenceMilliseconds);
                EditorGUILayout.TextField("Difference range (ms)", _sampleCount > 0
                    ? $"{_minimumDifferenceMilliseconds:F3} - {_maximumDifferenceMilliseconds:F3}"
                    : "not measured");
                EditorGUILayout.IntField("Samples", _sampleCount);
                EditorGUILayout.IntField("Actual playback buffer (ms)",
                    _actualPlaybackBufferMilliseconds);
                EditorGUILayout.DoubleField("Mixer channel buffer (ms)",
                    _channelBufferMilliseconds);
                EditorGUILayout.IntField("Actual update period (ms)",
                    _updatePeriodMilliseconds);
                EditorGUILayout.IntField("BASS recommended minimum buffer (ms)",
                    _minimumBufferMilliseconds);
                EditorGUILayout.IntField("Actual device buffer (ms)",
                    _deviceBufferMilliseconds);
                EditorGUILayout.IntField("BASS device period (ms)",
                    _devicePeriodMilliseconds);
                EditorGUILayout.LabelField("BASS device non-stop", _deviceNonStop.ToString());
                EditorGUILayout.LabelField("BASS Vista true position",
                    _vistaTruePlayPosition.ToString());
                EditorGUILayout.IntField("BASS reported device latency (ms)",
                    _deviceLatencyMilliseconds);
                EditorGUILayout.DoubleField("Buffer + device latency (ms)",
                    _channelBufferMilliseconds + _deviceLatencyMilliseconds);
                EditorGUILayout.IntField("Candidate mixer resume estimate (ms)",
                    ExpectedMixerResumeMilliseconds);
                EditorGUILayout.TextField("Mixer Play → master position change",
                    _masterPlayPositionLatencyResult);
                EditorGUILayout.TextField("Mixer Play → BassMix position change",
                    _playPositionLatencyResult);
                EditorGUILayout.DoubleField("Bass.ChannelUpdate call time (ms)",
                    _mixerUpdateCallMilliseconds);
                EditorGUILayout.DoubleField("Bass.ChannelPlay call time (ms)",
                    _mixerPlayCallMilliseconds);
                EditorGUILayout.TextField("Play return → master change",
                    _masterPositionAfterPlayReturnResult);
                EditorGUILayout.TextField("Play return → BassMix change",
                    _bassMixPositionAfterPlayReturnResult);
                EditorGUILayout.LongField("Master baseline position (bytes)",
                    _masterPlayPositionBaselineBytes);
                EditorGUILayout.LongField("Master first changed position (bytes)",
                    _masterPlayPositionChangedBytes);
                EditorGUILayout.LongField("Play baseline position (bytes)",
                    _playPositionBaselineBytes);
                EditorGUILayout.LongField("First changed position (bytes)",
                    _playPositionChangedBytes);
                EditorGUILayout.LongField("Busy-wait polls", _playPositionPollCount);
            }

            if (GUILayout.Button("Copy Results"))
            {
                EditorGUIUtility.systemCopyBuffer = FormatResults();
                _showNotification("BASS latency results copied.");
            }

            DrawBassMixPositionGraph();

            EditorGUILayout.HelpBox(
                "Positive difference means BASS has decoded farther ahead than the position BASSmix " +
                "reports as currently heard. Compare result across playback buffer sizes. Device " +
                "latency is reported separately and is not added to position difference.",
                MessageType.Info);
        }

        private double MeanDifferenceMilliseconds => _sampleCount > 0
            ? _differenceTotalMilliseconds / _sampleCount
            : 0;

        private int ExpectedMixerResumeMilliseconds =>
            _deviceBufferMilliseconds + _devicePeriodMilliseconds + _updatePeriodMilliseconds;

        private void StartTest()
        {
            StopTest();

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
                SetError("BASS is already initialized. Stop other audio tests or restart the editor.");
                return;
            }

            Bass.PlaybackBufferLength = _bufferLength;
            Bass.DeviceBufferLength = _deviceBufferLength;
            Bass.UpdatePeriod = _updatePeriod;
            Bass.DeviceNonStop = _useDeviceNonStop;
            Bass.VistaTruePlayPosition = _useVistaTruePlayPosition;
            if (!Bass.Init(-1, 44100, DeviceInitFlags.Latency, IntPtr.Zero))
            {
                SetBassError("Failed to initialize BASS");
                return;
            }
            _ownsBass = true;

            int fileHandle = Bass.CreateStream(_audioPath, 0, 0,
                BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (fileHandle == 0)
            {
                SetBassError("Failed to create source stream");
                StopTest(false);
                return;
            }

            _sourceHandle = BassFx.TempoCreate(fileHandle,
                BassFlags.Decode | BassFlags.FxFreeSource);
            if (_sourceHandle == 0)
            {
                SetBassError("Failed to create tempo stream");
                Bass.StreamFree(fileHandle);
                StopTest(false);
                return;
            }

            ChannelInfo sourceInfo = Bass.ChannelGetInfo(_sourceHandle);
            _mixerHandle = BassMix.CreateMixerStream(sourceInfo.Frequency, sourceInfo.Channels,
                BassFlags.Float | BassFlags.MixerNonStop);
            if (_mixerHandle == 0)
            {
                SetBassError("Failed to create mixer");
                StopTest(false);
                return;
            }

            if (!BassMix.MixerAddChannel(_mixerHandle, _sourceHandle,
                    BassFlags.MixerChanNoRampin))
            {
                SetBassError("Failed to add source to mixer");
                StopTest(false);
                return;
            }

            if (!Bass.ChannelSetAttribute(_mixerHandle, ChannelAttribute.Buffer,
                    _bufferLength / 1000f))
            {
                SetBassError("Failed to set mixer playback buffer");
                StopTest(false);
                return;
            }

            if (!Bass.ChannelGetAttribute(_mixerHandle, ChannelAttribute.Buffer,
                    out float channelBufferSeconds))
            {
                SetBassError("Failed to read mixer playback buffer");
                StopTest(false);
                return;
            }

            if (!Bass.ChannelPlay(_mixerHandle))
            {
                SetBassError("Failed to play mixer");
                StopTest(false);
                return;
            }

            BassInfo bassInfo = Bass.Info;
            _deviceLatencyMilliseconds = Math.Max(0, bassInfo.Latency);
            _minimumBufferMilliseconds = Math.Max(0, bassInfo.MinBufferLength);
            _actualPlaybackBufferMilliseconds = Bass.PlaybackBufferLength;
            _updatePeriodMilliseconds = Bass.UpdatePeriod;
            _deviceBufferMilliseconds = Bass.DeviceBufferLength;
            _devicePeriodMilliseconds = Math.Max(0, Bass.GetConfig(Configuration.DevicePeriod));
            _deviceNonStop = Bass.DeviceNonStop;
            _vistaTruePlayPosition = Bass.VistaTruePlayPosition;
            _channelBufferMilliseconds = channelBufferSeconds * 1000;
            ResetMeasurement();
            _automaticPlayLatencyPending = true;
            _status = $"Song playing; play latency measurement starts after " +
                $"{MEASUREMENT_WARMUP_SECONDS:F0}s warmup.";
            UpdatePositions();
        }

        private void OnEditorUpdate()
        {
            if (_mixerHandle == 0)
            {
                return;
            }

            if (_automaticPlayLatencyPending &&
                EditorApplication.timeSinceStartup >= _measurementStartsAt)
            {
                MeasurePlayPositionLatency();
            }
            UpdatePositions();
            _repaint();
        }

        private void MeasurePlayPositionLatency()
        {
            _automaticPlayLatencyPending = false;

            if (!Bass.ChannelPause(_mixerHandle))
            {
                SetBassError("Failed to pause mixer for play latency measurement");
                return;
            }

            _status = "Waiting for mixer position to stop...";
            _repaint();

            if (!WaitForMixerPositionToSettle(out _playPositionBaselineBytes))
            {
                return;
            }

            long updateTimestamp = Stopwatch.GetTimestamp();
            if (!Bass.ChannelUpdate(_mixerHandle, 0))
            {
                SetBassError("Failed to update mixer before play latency measurement");
                return;
            }
            _mixerUpdateCallMilliseconds = ElapsedSeconds(updateTimestamp) * 1000;

            // ChannelUpdate can fill or remap buffered data. Capture both baselines afterward so
            // only movement following ChannelPlay counts, matching normal YARG playback.
            _playPositionBaselineBytes = BassMix.ChannelGetPosition(
                _sourceHandle, PositionFlags.Bytes);
            if (_playPositionBaselineBytes < 0)
            {
                SetBassError("Failed to read source position before play latency measurement");
                return;
            }

            _masterPlayPositionBaselineBytes = Bass.ChannelGetPosition(
                _mixerHandle, PositionFlags.Bytes);
            if (_masterPlayPositionBaselineBytes < 0)
            {
                SetBassError("Failed to read master position before play latency measurement");
                return;
            }

            _masterPlayPositionChangedBytes = _masterPlayPositionBaselineBytes;
            _masterPlayPositionLatencyResult = "waiting";
            _masterPositionAfterPlayReturnResult = "waiting";
            _playPositionChangedBytes = _playPositionBaselineBytes;
            _playPositionLatencyResult = "waiting";
            _bassMixPositionAfterPlayReturnResult = "waiting";
            _playPositionPollCount = 0;
            long commandTimestamp = Stopwatch.GetTimestamp();
            if (!Bass.ChannelPlay(_mixerHandle))
            {
                SetBassError("Failed to play mixer for latency measurement");
                return;
            }
            long commandReturnedTimestamp = Stopwatch.GetTimestamp();
            _mixerPlayCallMilliseconds =
                (commandReturnedTimestamp - commandTimestamp) * 1000.0 / Stopwatch.Frequency;

            long timeoutTimestamp = commandTimestamp +
                (long) (PLAY_POSITION_TIMEOUT_SECONDS * Stopwatch.Frequency);
            bool masterPositionChanged = false;
            bool bassMixPositionChanged = false;
            while (Stopwatch.GetTimestamp() < timeoutTimestamp)
            {
                if (!masterPositionChanged)
                {
                    _masterPlayPositionChangedBytes = Bass.ChannelGetPosition(
                        _mixerHandle, PositionFlags.Bytes);
                    if (_masterPlayPositionChangedBytes < 0)
                    {
                        SetBassError("Failed to read master position during play latency measurement");
                        return;
                    }

                    if (_masterPlayPositionChangedBytes != _masterPlayPositionBaselineBytes)
                    {
                        masterPositionChanged = true;
                        _masterPlayPositionLatencyResult =
                            $"{ElapsedSeconds(commandTimestamp) * 1000:F3} ms";
                        _masterPositionAfterPlayReturnResult =
                            $"{ElapsedSeconds(commandReturnedTimestamp) * 1000:F3} ms";
                    }
                }

                if (!bassMixPositionChanged)
                {
                    _playPositionChangedBytes = BassMix.ChannelGetPosition(
                        _sourceHandle, PositionFlags.Bytes);
                }
                _playPositionPollCount++;
                if (_playPositionChangedBytes < 0)
                {
                    SetBassError("Failed to read position during play latency measurement");
                    return;
                }

                if (!bassMixPositionChanged &&
                    _playPositionChangedBytes != _playPositionBaselineBytes)
                {
                    bassMixPositionChanged = true;
                    double elapsedMilliseconds = ElapsedSeconds(commandTimestamp) * 1000;
                    _bassMixPositionAfterPlayReturnResult =
                        $"{ElapsedSeconds(commandReturnedTimestamp) * 1000:F3} ms";
                    _playPositionLatencyResult = $"{elapsedMilliseconds:F3} ms";
                }

                if (masterPositionChanged && bassMixPositionChanged)
                {
                    _status = "Play position latency measured; mixer is playing.";
                    // Exclude the deliberate pause from playback jitter measurement.
                    _positionJitterHistory.Clear();
                    UpdatePositions();
                    _repaint();
                    return;
                }

                Thread.SpinWait(1);
            }

            _playPositionLatencyResult = $"timed out after {PLAY_POSITION_TIMEOUT_SECONDS:F0}s";
            if (!masterPositionChanged)
            {
                _masterPlayPositionLatencyResult =
                    $"timed out after {PLAY_POSITION_TIMEOUT_SECONDS:F0}s";
                _masterPositionAfterPlayReturnResult = "not observed";
            }
            if (!bassMixPositionChanged)
            {
                _playPositionLatencyResult =
                    $"timed out after {PLAY_POSITION_TIMEOUT_SECONDS:F0}s";
                _bassMixPositionAfterPlayReturnResult = "not observed";
            }
            _status = "Error: A position did not change after mixer was played.";
            _repaint();
        }

        private bool WaitForMixerPositionToSettle(out long settledPosition)
        {
            settledPosition = BassMix.ChannelGetPosition(_sourceHandle, PositionFlags.Bytes);
            if (settledPosition < 0)
            {
                SetBassError("Failed to read position before mixer play latency measurement");
                return false;
            }

            long startedTimestamp = Stopwatch.GetTimestamp();
            long lastChangeTimestamp = startedTimestamp;
            long timeoutTimestamp = startedTimestamp +
                (long) (PLAY_POSITION_TIMEOUT_SECONDS * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < timeoutTimestamp)
            {
                long position = BassMix.ChannelGetPosition(_sourceHandle, PositionFlags.Bytes);
                if (position < 0)
                {
                    SetBassError("Failed to read position while waiting for mixer to pause");
                    return false;
                }

                if (position != settledPosition)
                {
                    settledPosition = position;
                    lastChangeTimestamp = Stopwatch.GetTimestamp();
                }
                else if (ElapsedSeconds(lastChangeTimestamp) >= POSITION_SETTLE_SECONDS)
                {
                    return true;
                }

                Thread.SpinWait(1);
            }

            _playPositionLatencyResult = "mixer position did not settle";
            _status = "Error: Source position did not settle after mixer was paused.";
            _repaint();
            return false;
        }

        private static double ElapsedSeconds(long timestamp)
        {
            return (Stopwatch.GetTimestamp() - timestamp) / (double) Stopwatch.Frequency;
        }

        private void UpdatePositions()
        {
            // Read raw decode position first. Both calls inspect same source handle.
            _bassPositionBytes = Bass.ChannelGetPosition(_sourceHandle, PositionFlags.Bytes);
            _mixerPositionBytes = BassMix.ChannelGetPosition(_sourceHandle, PositionFlags.Bytes);
            if (_bassPositionBytes < 0 || _mixerPositionBytes < 0)
            {
                SetBassError("Failed to read source positions");
                return;
            }

            _bassPositionSeconds = Bass.ChannelBytes2Seconds(_sourceHandle, _bassPositionBytes);
            _mixerPositionSeconds = Bass.ChannelBytes2Seconds(_sourceHandle, _mixerPositionBytes);
            _differenceMilliseconds =
                (_bassPositionSeconds - _mixerPositionSeconds) * 1000;

            if (EditorApplication.timeSinceStartup < _measurementStartsAt)
            {
                return;
            }

            if (_sampleCount == 0)
            {
                _status = "Song playing; collecting position measurements.";
            }

            if (_sampleCount == 0)
            {
                _minimumDifferenceMilliseconds = _differenceMilliseconds;
                _maximumDifferenceMilliseconds = _differenceMilliseconds;
            }
            else
            {
                _minimumDifferenceMilliseconds = Math.Min(
                    _minimumDifferenceMilliseconds, _differenceMilliseconds);
                _maximumDifferenceMilliseconds = Math.Max(
                    _maximumDifferenceMilliseconds, _differenceMilliseconds);
            }

            _differenceTotalMilliseconds += _differenceMilliseconds;
            _sampleCount++;
            double sampleTime = EditorApplication.timeSinceStartup;
            if (_positionJitterHistory.Count == 0)
            {
                _jitterAnchorTime = sampleTime;
                _jitterAnchorPosition = _mixerPositionSeconds;
            }

            double expectedPosition = _jitterAnchorPosition + sampleTime - _jitterAnchorTime;
            float positionErrorMilliseconds =
                (float) ((expectedPosition - _mixerPositionSeconds) * 1000);
            _positionJitterHistory.Add(new Vector2(
                (float) (sampleTime - _measurementStartsAt), positionErrorMilliseconds));
            if (_positionJitterHistory.Count > MAXIMUM_GRAPH_SAMPLES)
            {
                _positionJitterHistory.RemoveRange(0, GRAPH_SAMPLES_TO_TRIM);
            }
        }

        private void ResetMeasurement()
        {
            _bassPositionBytes = 0;
            _mixerPositionBytes = 0;
            _bassPositionSeconds = 0;
            _mixerPositionSeconds = 0;
            _differenceMilliseconds = 0;
            _minimumDifferenceMilliseconds = 0;
            _maximumDifferenceMilliseconds = 0;
            _differenceTotalMilliseconds = 0;
            _sampleCount = 0;
            _positionJitterHistory.Clear();
            _measurementStartsAt = EditorApplication.timeSinceStartup +
                MEASUREMENT_WARMUP_SECONDS;
            if (_mixerHandle != 0)
            {
                _status = $"Song playing; measurement starts after " +
                    $"{MEASUREMENT_WARMUP_SECONDS:F0}s warmup.";
            }
        }

        private string FormatResults()
        {
            var results = new StringBuilder();
            results.AppendLine("BASS Latency Test Results");
            results.AppendLine($"Audio file: {_audioPath}");
            results.AppendLine($"Configured playback buffer (ms): {_bufferLength}");
            results.AppendLine($"Actual playback buffer (ms): {_actualPlaybackBufferMilliseconds}");
            results.AppendLine($"Mixer channel buffer (ms): {_channelBufferMilliseconds:F3}");
            results.AppendLine($"Configured update period (ms): {_updatePeriod}");
            results.AppendLine($"Actual update period (ms): {_updatePeriodMilliseconds}");
            results.AppendLine($"BASS recommended minimum buffer (ms): {_minimumBufferMilliseconds}");
            results.AppendLine($"Configured device buffer (ms): {_deviceBufferLength}");
            results.AppendLine($"Actual device buffer (ms): {_deviceBufferMilliseconds}");
            results.AppendLine($"BASS device period (ms): {_devicePeriodMilliseconds}");
            results.AppendLine($"Configured device non-stop: {_useDeviceNonStop}");
            results.AppendLine($"Actual BASS device non-stop: {_deviceNonStop}");
            results.AppendLine($"Configured Vista true position: {_useVistaTruePlayPosition}");
            results.AppendLine($"Actual BASS Vista true position: {_vistaTruePlayPosition}");
            results.AppendLine($"BASS reported device latency (ms): {_deviceLatencyMilliseconds}");
            results.AppendLine($"Buffer + device latency (ms): " +
                $"{_channelBufferMilliseconds + _deviceLatencyMilliseconds:F3}");
            results.AppendLine($"Candidate mixer resume estimate (device buffer + device period + " +
                $"update period) (ms): {ExpectedMixerResumeMilliseconds}");
            results.AppendLine($"Measurement warmup (s): {MEASUREMENT_WARMUP_SECONDS:F1}");
            results.AppendLine($"Mixer Play to master position change: " +
                _masterPlayPositionLatencyResult);
            results.AppendLine($"Mixer Play to BassMix position change: {_playPositionLatencyResult}");
            results.AppendLine($"Bass.ChannelUpdate call time (ms): " +
                $"{_mixerUpdateCallMilliseconds:F3}");
            results.AppendLine($"Bass.ChannelPlay call time (ms): {_mixerPlayCallMilliseconds:F3}");
            results.AppendLine($"Play return to master position change: " +
                _masterPositionAfterPlayReturnResult);
            results.AppendLine($"Play return to BassMix position change: " +
                _bassMixPositionAfterPlayReturnResult);
            results.AppendLine($"Master baseline position (bytes): {_masterPlayPositionBaselineBytes}");
            results.AppendLine($"Master first changed position (bytes): " +
                _masterPlayPositionChangedBytes);
            results.AppendLine($"Play baseline position (bytes): {_playPositionBaselineBytes}");
            results.AppendLine($"First changed position (bytes): {_playPositionChangedBytes}");
            results.AppendLine($"Busy-wait polls: {_playPositionPollCount}");
            results.AppendLine($"Bass position (bytes): {_bassPositionBytes}");
            results.AppendLine($"Bass position (seconds): {_bassPositionSeconds:F6}");
            results.AppendLine($"BassMix position (bytes): {_mixerPositionBytes}");
            results.AppendLine($"BassMix position (seconds): {_mixerPositionSeconds:F6}");
            results.AppendLine($"Latest Bass - BassMix (ms): {_differenceMilliseconds:F3}");
            results.AppendLine($"Mean difference (ms): {MeanDifferenceMilliseconds:F3}");
            results.AppendLine(_sampleCount > 0
                ? $"Difference range (ms): {_minimumDifferenceMilliseconds:F3} - " +
                  $"{_maximumDifferenceMilliseconds:F3}"
                : "Difference range (ms): not measured");
            results.AppendLine($"Samples: {_sampleCount}");
            return results.ToString();
        }

        private void DrawBassMixPositionGraph()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("BassMix position timing error",
                EditorStyles.boldLabel);

            Rect graphRect = GUILayoutUtility.GetRect(100, 190, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(graphRect, EditorGUIUtility.isProSkin
                ? new Color(0.11f, 0.11f, 0.11f)
                : new Color(0.85f, 0.85f, 0.85f));

            if (_positionJitterHistory.Count < 2)
            {
                GUI.Label(graphRect, "Waiting for measurement data...",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            float measuredMinimum = _positionJitterHistory[0].y;
            float measuredMaximum = _positionJitterHistory[0].y;
            for (int i = 1; i < _positionJitterHistory.Count; i++)
            {
                measuredMinimum = Math.Min(measuredMinimum, _positionJitterHistory[i].y);
                measuredMaximum = Math.Max(measuredMaximum, _positionJitterHistory[i].y);
            }

            const float MINIMUM_ERROR = -2;
            const float MAXIMUM_ERROR = 2;
            float minimumError = Math.Min(measuredMinimum, MINIMUM_ERROR);
            float maximumError = Math.Max(measuredMaximum, MAXIMUM_ERROR);
            float errorRange = maximumError - minimumError;

            Rect plotRect = new(graphRect.x + 85, graphRect.y + 10,
                graphRect.width - 95, graphRect.height - 32);
            float startTime = _positionJitterHistory[0].x;
            float timeRange = _positionJitterHistory[^1].x - startTime;

            if (Event.current.type == EventType.Repaint)
            {
                float zeroY = MapPositionToGraph(0, plotRect, minimumError, errorRange);
                Handles.color = new Color(0.55f, 0.55f, 0.55f, 0.6f);
                Handles.DrawLine(new Vector3(plotRect.x, zeroY),
                    new Vector3(plotRect.xMax, zeroY));
                DrawPositionGraphLine(plotRect, minimumError, errorRange, startTime,
                    timeRange, new Color(0.1f, 0.85f, 1), 2);
            }

            GUI.Label(new Rect(graphRect.x + 2, plotRect.y - 7, 80, 16),
                $"{maximumError:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(graphRect.x + 2, plotRect.yMax - 8, 80, 16),
                $"{minimumError:F3}", EditorStyles.miniLabel);
            GUI.Label(new Rect(plotRect.x, plotRect.yMax + 2, 80, 16),
                $"{startTime:F1} s", EditorStyles.miniLabel);
            GUI.Label(new Rect(plotRect.xMax - 80, plotRect.yMax + 2, 80, 16),
                $"{_positionJitterHistory[^1].x:F1} s", new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.UpperRight
            });
            GUI.Label(new Rect(graphRect.x + 2, graphRect.y + graphRect.height / 2 - 8, 80, 16),
                "ms", EditorStyles.miniLabel);

            EditorGUILayout.LabelField(
                $"Observed timing error: {measuredMinimum:F3} to {measuredMaximum:F3} ms.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawPositionGraphLine(Rect plotRect, float minimumPosition,
            float positionRange, float startTime, float timeRange, Color color, float width)
        {
            var points = new Vector3[_positionJitterHistory.Count];

            for (int i = 0; i < _positionJitterHistory.Count; i++)
            {
                float normalizedTime = timeRange > 0
                    ? (_positionJitterHistory[i].x - startTime) / timeRange
                    : 0;
                points[i] = new Vector3(
                    Mathf.Lerp(plotRect.x, plotRect.xMax, normalizedTime),
                    MapPositionToGraph(_positionJitterHistory[i].y, plotRect,
                        minimumPosition, positionRange));
            }

            Handles.color = color;
            Handles.DrawAAPolyLine(width, points);
        }

        private static float MapPositionToGraph(float position, Rect plotRect,
            float minimumPosition, float positionRange)
        {
            float normalizedPosition = (position - minimumPosition) / positionRange;
            return Mathf.Lerp(plotRect.yMax, plotRect.y, normalizedPosition);
        }

        private void StopTest(bool resetStatus = true)
        {
            if (_mixerHandle != 0)
            {
                Bass.StreamFree(_mixerHandle);
                _mixerHandle = 0;
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

            _deviceLatencyMilliseconds = 0;
            _actualPlaybackBufferMilliseconds = 0;
            _updatePeriodMilliseconds = 0;
            _minimumBufferMilliseconds = 0;
            _deviceBufferMilliseconds = 0;
            _devicePeriodMilliseconds = 0;
            _channelBufferMilliseconds = 0;
            _playPositionLatencyResult = "not measured";
            _playPositionBaselineBytes = 0;
            _playPositionChangedBytes = 0;
            _playPositionPollCount = 0;
            _automaticPlayLatencyPending = false;
            _masterPlayPositionLatencyResult = "not measured";
            _masterPlayPositionBaselineBytes = 0;
            _masterPlayPositionChangedBytes = 0;
            _mixerPlayCallMilliseconds = 0;
            _masterPositionAfterPlayReturnResult = "not measured";
            _bassMixPositionAfterPlayReturnResult = "not measured";
            ResetMeasurement();
            if (resetStatus)
            {
                _status = "Select an audio file, then start the test.";
            }
        }

        private void SetBassError(string message)
        {
            SetError($"{message}: {Bass.LastError}");
        }

        private void SetError(string message)
        {
            _status = $"Error: {message}";
        }
    }
}
