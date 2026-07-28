#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Renders a decoding mixer on a worker thread and queues its output in a BASS push stream.
    /// </summary>
    internal sealed class BassRenderAheadStream : IDisposable
    {
        private const int RENDER_AHEAD_MILLISECONDS = 30;
        private const int RENDER_CHUNK_FRAMES = 128;
        private const int START_TIMEOUT_MILLISECONDS = 2000;
        private const double CLOCK_SMOOTHING_SECONDS = 1.0;

        private readonly int _sourceMixerHandle;
        private readonly int _bassDeviceId;
        private readonly int _sampleRate;
        private readonly int _bytesPerFrame;
        private readonly float[] _renderBuffer;
        private readonly object _renderLock = new();
        private readonly AutoResetEvent _renderWake = new(false);
        private readonly int _targetFrames;
        private readonly bool _outputRequestsReported;
        private readonly ContinuousOutputClock _outputClock;

        private Thread? _renderThread;
        private volatile bool _running;
        private volatile bool _queueReady;
        private int _disposed;
        private int _queueEmpty;
        private int _queueGeneration;
        private long _generatedFrames;
        private long _maximumRenderTicks;
        private long _maximumSourceReadTicks;
        private long _maximumQueueWriteTicks;
        private long _maximumGcRenderTicks;
        private long _maximumNonGcRenderTicks;
        private long _gcOverlapRenderCalls;
        private long _minimumQueuedFrames = long.MaxValue;
        private long _underruns;

        public int Handle { get; }

        public int QueuedFrames
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return 0;
                }

                int queuedBytes = Bass.StreamPutData(Handle, IntPtr.Zero, 0);
                return queuedBytes > 0 ? queuedBytes / _bytesPerFrame : 0;
            }
        }

        public int MinimumQueuedFrames
        {
            get
            {
                long frames = Volatile.Read(ref _minimumQueuedFrames);
                return frames == long.MaxValue ? QueuedFrames : (int) frames;
            }
        }

        public double MaximumRenderTimeMilliseconds =>
            Volatile.Read(ref _maximumRenderTicks) * 1000.0 / Stopwatch.Frequency;

        public double MaximumSourceReadTimeMilliseconds =>
            Volatile.Read(ref _maximumSourceReadTicks) * 1000.0 / Stopwatch.Frequency;

        public double MaximumQueueWriteTimeMilliseconds =>
            Volatile.Read(ref _maximumQueueWriteTicks) * 1000.0 / Stopwatch.Frequency;

        public double MaximumGcRenderTimeMilliseconds =>
            Volatile.Read(ref _maximumGcRenderTicks) * 1000.0 / Stopwatch.Frequency;

        public double MaximumNonGcRenderTimeMilliseconds =>
            Volatile.Read(ref _maximumNonGcRenderTicks) * 1000.0 / Stopwatch.Frequency;

        public long GcOverlapRenderCallCount => Volatile.Read(ref _gcOverlapRenderCalls);

        public long UnderrunCount => Volatile.Read(ref _underruns);

        private BassRenderAheadStream(int sourceMixerHandle, int bassDeviceId, int sampleRate,
            int channels, int callbackFrames, bool outputRequestsReported, int handle)
        {
            _sourceMixerHandle = sourceMixerHandle;
            _bassDeviceId = bassDeviceId;
            _sampleRate = sampleRate;
            _bytesPerFrame = channels * sizeof(float);
            _renderBuffer = new float[RENDER_CHUNK_FRAMES * channels];
            _targetFrames = TargetFrames(callbackFrames);
            _outputRequestsReported = outputRequestsReported;
            _outputClock = new ContinuousOutputClock(sampleRate, callbackFrames);
            _outputClock.Reset(queueGeneration: 0, nextOutputFrame: 0);
            Handle = handle;
        }

        public static BassRenderAheadStream? Create(int sourceMixerHandle, int bassDeviceId,
            int sampleRate, int channels, int callbackFrames, bool outputRequestsReported)
        {
            int handle = Bass.CreateStream(sampleRate, channels,
                BassFlags.Float | BassFlags.Decode, StreamProcedureType.Push);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to create ASIO render-ahead stream: {0}",
                    Bass.LastError);
                return null;
            }

            var stream = new BassRenderAheadStream(sourceMixerHandle, bassDeviceId, sampleRate,
                channels, callbackFrames, outputRequestsReported, handle);
            if (stream.Start())
            {
                return stream;
            }

            stream.Dispose();
            return null;
        }

        /// <summary>
        /// Records one output request and wakes producer before BASS consumes queued frames.
        /// </summary>
        public void OnOutputRequested(int frames, long timestamp)
        {
            int queueGeneration = Volatile.Read(ref _queueGeneration);
            int queuedFrames = QueuedFrames;
            _outputClock.ObserveCallback(
                queueGeneration, timestamp, frames, Math.Min(frames, queuedFrames));
            UpdateMinimum(ref _minimumQueuedFrames, Math.Max(0, queuedFrames - frames));
            if (_queueReady && queuedFrames < frames)
            {
                Interlocked.Increment(ref _underruns);
            }

            _renderWake.Set();
        }

        /// <summary>
        /// Discards rendered audio after a source seek. Producer lock prevents pre-seek data from
        /// being queued after reset.
        /// </summary>
        public void Flush()
        {
            lock (_renderLock)
            {
                _queueReady = false;
                if (!Bass.ChannelSetPosition(Handle, 0, PositionFlags.Bytes))
                {
                    YargLogger.LogFormatError("Failed to flush ASIO render-ahead stream: {0}",
                        Bass.LastError);
                }
                else
                {
                    int queueGeneration = unchecked(_queueGeneration + 1);
                    Volatile.Write(ref _queueGeneration, queueGeneration);
                    _outputClock.Reset(queueGeneration, _generatedFrames);
                }
            }
            _renderWake.Set();
        }

        /// <summary>
        /// Gets source position at output. A QPC-smoothed output clock advances between ASIO
        /// buffers; MixerPositionEx maps that output frame back through tempo and seek history.
        /// Producer lock keeps the mixer output edge fixed during the lookup.
        /// </summary>
        public long GetSourcePosition(int sourceHandle, int outputLatencyFrames)
        {
            lock (_renderLock)
            {
                long timestamp = Stopwatch.GetTimestamp();
                double fallbackHeardFrame = 0;
                if (!_outputClock.IsInitialized)
                {
                    fallbackHeardFrame = Math.Max(
                        0, _generatedFrames - QueuedFrames - outputLatencyFrames);
                }
                double heardFrame = _outputClock.GetHeardFrame(
                    timestamp, outputLatencyFrames, _generatedFrames, fallbackHeardFrame);
                long delayFrames = (long) Math.Ceiling(
                    Math.Max(0, _generatedFrames - heardFrame));
                int delayBytes = FramesToBytes(delayFrames);
                long position = BassMix.ChannelGetPosition(
                    sourceHandle, PositionFlags.Bytes, delayBytes);

                // Freshly attached/reset sources may not have enough position history yet.
                return position < 0 && Bass.LastError == Errors.NotAvailable
                    ? BassMix.ChannelGetPosition(sourceHandle, PositionFlags.Bytes, 0)
                    : position;
            }
        }

        public int SnapshotQueuedFrames()
        {
            lock (_renderLock)
            {
                return QueuedFrames;
            }
        }

        public void ResetMetrics()
        {
            int queuedFrames = QueuedFrames;
            Interlocked.Exchange(ref _maximumRenderTicks, 0);
            Interlocked.Exchange(ref _maximumSourceReadTicks, 0);
            Interlocked.Exchange(ref _maximumQueueWriteTicks, 0);
            Interlocked.Exchange(ref _maximumGcRenderTicks, 0);
            Interlocked.Exchange(ref _maximumNonGcRenderTicks, 0);
            Interlocked.Exchange(ref _gcOverlapRenderCalls, 0);
            Interlocked.Exchange(ref _minimumQueuedFrames, queuedFrames);
            Interlocked.Exchange(ref _underruns, 0);
            Volatile.Write(ref _queueEmpty, queuedFrames == 0 ? 1 : 0);
        }

        private bool Start()
        {
            int reserveFrames = _targetFrames + (RENDER_CHUNK_FRAMES * 2);
            if (Bass.StreamPutData(Handle, IntPtr.Zero,
                    checked(reserveFrames * _bytesPerFrame)) < 0)
            {
                YargLogger.LogFormatError("Failed to reserve ASIO render-ahead buffer: {0}",
                    Bass.LastError);
                return false;
            }

            _running = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "ASIO render-ahead",
                Priority = ThreadPriority.Highest,
            };
            _renderThread.Start();

            var timeout = Stopwatch.StartNew();
            while (!_queueReady && _running &&
                timeout.ElapsedMilliseconds < START_TIMEOUT_MILLISECONDS)
            {
                Thread.Sleep(1);
            }

            if (_queueReady)
            {
                return true;
            }

            YargLogger.LogError("Failed to prefill ASIO render-ahead stream");
            return false;
        }

        private void RenderLoop()
        {
            try
            {
                Bass.CurrentDevice = _bassDeviceId;
                while (_running)
                {
                    int queuedFrames = QueuedFrames;
                    if (_queueReady && !_outputRequestsReported)
                    {
                        RecordPolledOutput(queuedFrames, Stopwatch.GetTimestamp());
                    }

                    if (queuedFrames >= _targetFrames)
                    {
                        if (!_queueReady)
                        {
                            Interlocked.Exchange(ref _minimumQueuedFrames, queuedFrames);
                            Volatile.Write(ref _queueEmpty, 0);
                            _queueReady = true;
                        }
                        _renderWake.WaitOne(2);
                        continue;
                    }

                    RenderChunk();
                }
            }
            catch (Exception exception)
            {
                _running = false;
                YargLogger.LogException(exception, "ASIO render-ahead thread failed");
            }
        }

        private void RecordPolledOutput(int queuedFrames, long timestamp)
        {
            _outputClock.ObserveQueueDepth(
                Volatile.Read(ref _queueGeneration), timestamp, _generatedFrames, queuedFrames);
            UpdateMinimum(ref _minimumQueuedFrames, queuedFrames);
            if (queuedFrames == 0)
            {
                if (Interlocked.Exchange(ref _queueEmpty, 1) == 0)
                {
                    Interlocked.Increment(ref _underruns);
                }
                return;
            }

            Volatile.Write(ref _queueEmpty, 0);
        }

        private void RenderChunk()
        {
            lock (_renderLock)
            {
                if (!_running)
                {
                    return;
                }

                int gen0Collections = GC.CollectionCount(0);
                int gen1Collections = GC.CollectionCount(1);
                int gen2Collections = GC.CollectionCount(2);
                long start = Stopwatch.GetTimestamp();
                try
                {
                    int requestedBytes = _renderBuffer.Length * sizeof(float);
                    long sourceReadStart = Stopwatch.GetTimestamp();
                    int bytesRead = Bass.ChannelGetData(
                        _sourceMixerHandle, _renderBuffer, requestedBytes);
                    UpdateMaximum(ref _maximumSourceReadTicks,
                        Stopwatch.GetTimestamp() - sourceReadStart);
                    if (bytesRead < 0)
                    {
                        FailRender("Failed to render ASIO audio", Bass.LastError);
                        return;
                    }

                    bytesRead -= bytesRead % _bytesPerFrame;
                    if (bytesRead <= 0)
                    {
                        return;
                    }
                    long queueWriteStart = Stopwatch.GetTimestamp();
                    int putResult = Bass.StreamPutData(Handle, _renderBuffer, bytesRead);
                    UpdateMaximum(ref _maximumQueueWriteTicks,
                        Stopwatch.GetTimestamp() - queueWriteStart);
                    if (putResult < 0)
                    {
                        FailRender("Failed to queue rendered ASIO audio", Bass.LastError);
                        return;
                    }

                    _generatedFrames += bytesRead / _bytesPerFrame;
                }
                finally
                {
                    long elapsedTicks = Stopwatch.GetTimestamp() - start;
                    UpdateMaximum(ref _maximumRenderTicks, elapsedTicks);
                    bool gcOverlapped = GC.CollectionCount(0) != gen0Collections ||
                        GC.CollectionCount(1) != gen1Collections ||
                        GC.CollectionCount(2) != gen2Collections;
                    if (gcOverlapped)
                    {
                        Interlocked.Increment(ref _gcOverlapRenderCalls);
                        UpdateMaximum(ref _maximumGcRenderTicks, elapsedTicks);
                    }
                    else
                    {
                        UpdateMaximum(ref _maximumNonGcRenderTicks, elapsedTicks);
                    }
                }
            }
        }

        private void FailRender(string message, Errors error)
        {
            _running = false;
            YargLogger.LogFormatError("{0}: {1}", message, error);
        }

        private int TargetFrames(int callbackFrames) => Math.Max(
            (int) Math.Ceiling(_sampleRate * RENDER_AHEAD_MILLISECONDS / 1000.0),
            callbackFrames * 2);

        private int FramesToBytes(long frames)
        {
            long bytes = Math.Max(0, frames) * (long) _bytesPerFrame;
            return (int) Math.Min(bytes, int.MaxValue);
        }

        /// <summary>
        /// Maps QPC to the render stream's absolute output-frame domain. Managed output reports
        /// callback entry directly. Native ChannelEnableBass is deliberately callback-free, so the
        /// render worker detects queue drains; its polling delay is filtered as callback jitter.
        /// </summary>
        private sealed class ContinuousOutputClock
        {
            private readonly object _lock = new();
            private readonly int _sampleRate;
            private readonly int _callbackFrames;

            private int _queueGeneration;
            private bool _initialized;
            private bool _queueDepthInitialized;
            private long _anchorTimestamp;
            private long _lastObservationTimestamp;
            private long _nextCallbackFrame;
            private long _lastQueueOutputFrame;
            private long _latestSubmittedFrame;
            private double _anchorFrame;
            private double _lastReportedFrame;

            public ContinuousOutputClock(int sampleRate, int callbackFrames)
            {
                _sampleRate = sampleRate;
                _callbackFrames = Math.Max(1, callbackFrames);
            }

            public bool IsInitialized
            {
                get
                {
                    lock (_lock)
                    {
                        return _initialized;
                    }
                }
            }

            public void ObserveCallback(int queueGeneration, long timestamp,
                int requestedFrames, int availableFrames)
            {
                lock (_lock)
                {
                    if (queueGeneration != _queueGeneration)
                    {
                        return;
                    }

                    long blockStart = _nextCallbackFrame;
                    long submittedFrames = Math.Clamp(availableFrames, 0, requestedFrames);
                    _nextCallbackFrame += submittedFrames;
                    RecordObservation(blockStart, _nextCallbackFrame, timestamp);
                }
            }

            public void ObserveQueueDepth(int queueGeneration, long timestamp,
                long generatedFrames, int queuedFrames)
            {
                long outputFrame = Math.Max(0, generatedFrames - Math.Max(0, queuedFrames));
                lock (_lock)
                {
                    if (queueGeneration != _queueGeneration)
                    {
                        return;
                    }

                    if (!_queueDepthInitialized)
                    {
                        _lastQueueOutputFrame = outputFrame;
                        _nextCallbackFrame = outputFrame;
                        _queueDepthInitialized = true;
                        return;
                    }

                    long advancedFrames = outputFrame - _lastQueueOutputFrame;
                    if (advancedFrames <= 0)
                    {
                        return;
                    }

                    // Poll may span several callbacks. Latest callback started one buffer (or the
                    // available underfill amount) before the newly observed output edge.
                    long blockFrames = Math.Min(_callbackFrames, advancedFrames);
                    long blockStart = outputFrame - blockFrames;
                    _lastQueueOutputFrame = outputFrame;
                    _nextCallbackFrame = outputFrame;
                    RecordObservation(blockStart, outputFrame, timestamp);
                }
            }

            public double GetHeardFrame(long timestamp, int outputLatencyFrames,
                long generatedFrames, double fallbackHeardFrame)
            {
                lock (_lock)
                {
                    double heardFrame;
                    if (!_initialized || _sampleRate <= 0)
                    {
                        heardFrame = fallbackHeardFrame;
                    }
                    else
                    {
                        double elapsed = (double) (timestamp - _anchorTimestamp) /
                            Stopwatch.Frequency;
                        heardFrame = _anchorFrame + Math.Max(0, elapsed) * _sampleRate -
                            Math.Max(0, outputLatencyFrames);
                    }

                    double maximumFrame = Math.Min(generatedFrames, _latestSubmittedFrame);
                    maximumFrame = Math.Max(maximumFrame, _lastReportedFrame);
                    heardFrame = Math.Clamp(heardFrame, _lastReportedFrame, maximumFrame);
                    _lastReportedFrame = heardFrame;
                    return heardFrame;
                }
            }

            public void Reset(int queueGeneration, long nextOutputFrame)
            {
                lock (_lock)
                {
                    _queueGeneration = queueGeneration;
                    _initialized = false;
                    _queueDepthInitialized = true;
                    _anchorTimestamp = 0;
                    _lastObservationTimestamp = 0;
                    _nextCallbackFrame = nextOutputFrame;
                    _lastQueueOutputFrame = nextOutputFrame;
                    _latestSubmittedFrame = nextOutputFrame;
                }
            }

            private void RecordObservation(long blockStartFrame, long submittedFrame,
                long timestamp)
            {
                if (_sampleRate <= 0)
                {
                    return;
                }

                if (!_initialized)
                {
                    _anchorFrame = blockStartFrame;
                    _anchorTimestamp = timestamp;
                    _initialized = true;
                }
                else
                {
                    double elapsed = (double) (timestamp - _anchorTimestamp) /
                        Stopwatch.Frequency;
                    double predictedFrame = _anchorFrame + Math.Max(0, elapsed) * _sampleRate;
                    double observationElapsed = (double)
                        Math.Max(0, timestamp - _lastObservationTimestamp) / Stopwatch.Frequency;
                    double blend = 1 - Math.Exp(
                        -observationElapsed / CLOCK_SMOOTHING_SECONDS);
                    _anchorFrame = predictedFrame +
                        blend * (blockStartFrame - predictedFrame);
                    _anchorTimestamp = timestamp;
                }

                _lastObservationTimestamp = timestamp;
                _latestSubmittedFrame = Math.Max(_latestSubmittedFrame, submittedFrame);
            }
        }

        private static void UpdateMaximum(ref long target, long value)
        {
            long previous;
            do
            {
                previous = Volatile.Read(ref target);
                if (value <= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }

        private static void UpdateMinimum(ref long target, long value)
        {
            long previous;
            do
            {
                previous = Volatile.Read(ref target);
                if (value >= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _running = false;
            _renderWake.Set();
            if (_renderThread != null && _renderThread != Thread.CurrentThread)
            {
                _renderThread.Join();
            }
            _renderThread = null;

            Bass.StreamFree(Handle);
            _renderWake.Dispose();
        }
    }
}
