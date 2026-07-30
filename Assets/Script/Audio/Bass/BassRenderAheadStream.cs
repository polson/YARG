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
        private const int    RENDER_AHEAD_MILLISECONDS  = 30;
        private const int    RENDER_CHUNK_FRAMES        = 128;
        private const int    START_TIMEOUT_MILLISECONDS = 2000;
        private const double CLOCK_SMOOTHING_SECONDS    = 1.0;

        private readonly int                   _sourceMixerHandle;
        private readonly int                   _bassDeviceId;
        private readonly int                   _sampleRate;
        private readonly int                   _bytesPerFrame;
        private readonly float[]               _renderBuffer;
        private readonly object                _renderLock = new();
        private readonly AutoResetEvent        _renderWake = new(false);
        private readonly int                   _targetFrames;
        private readonly bool                  _outputRequestsReported;
        private readonly ContinuousOutputClock _outputClock;

        private          Thread? _renderThread;
        private volatile bool    _running;
        private volatile bool    _queueReady;
        private          int     _disposed;
        private          int     _queueGeneration;
        private          long    _generatedFrames;

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

        private BassRenderAheadStream(int sourceMixerHandle, int bassDeviceId, int sampleRate, int channels,
            int callbackFrames, bool outputRequestsReported, int handle)
        {
            _sourceMixerHandle = sourceMixerHandle;
            _bassDeviceId = bassDeviceId;
            _sampleRate = sampleRate;
            _bytesPerFrame = channels * sizeof(float);
            _renderBuffer = new float[RENDER_CHUNK_FRAMES * channels];
            _targetFrames = TargetFrames(callbackFrames);
            _outputRequestsReported = outputRequestsReported;
            _outputClock = new ContinuousOutputClock(sampleRate, callbackFrames);
            _outputClock.Reset(0, 0);
            Handle = handle;
        }

        public static BassRenderAheadStream? Create(int sourceMixerHandle, int bassDeviceId, int sampleRate,
            int channels, int callbackFrames, bool outputRequestsReported)
        {
            int handle = Bass.CreateStream(sampleRate, channels, BassFlags.Float | BassFlags.Decode,
                StreamProcedureType.Push);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to create ASIO render-ahead stream: {0}", Bass.LastError);
                return null;
            }

            var stream = new BassRenderAheadStream(sourceMixerHandle, bassDeviceId, sampleRate, channels,
                callbackFrames, outputRequestsReported, handle);
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
            _outputClock.ObserveCallback(queueGeneration, timestamp, frames, Math.Min(frames, queuedFrames));
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
                    YargLogger.LogFormatError("Failed to flush ASIO render-ahead stream: {0}", Bass.LastError);
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
                double fallbackHeardFrame = GetFallbackHeardFrame(outputLatencyFrames);
                double heardFrame = _outputClock.GetHeardFrame(timestamp, outputLatencyFrames, _generatedFrames,
                    fallbackHeardFrame);
                long delayFrames = (long) Math.Ceiling(Math.Max(0, _generatedFrames - heardFrame));
                int delayBytes = FramesToBytes(delayFrames);
                long position = BassMix.ChannelGetPosition(sourceHandle, PositionFlags.Bytes, delayBytes);

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

        private bool Start()
        {
            if (!ReserveRenderBuffer())
            {
                return false;
            }

            StartRenderThread();
            if (WaitForInitialQueue())
            {
                return true;
            }

            YargLogger.LogError("Failed to prefill ASIO render-ahead stream");
            return false;
        }

        private bool ReserveRenderBuffer()
        {
            int reserveFrames = _targetFrames + RENDER_CHUNK_FRAMES * 2;
            int reserveBytes = checked(reserveFrames * _bytesPerFrame);
            if (Bass.StreamPutData(Handle, IntPtr.Zero, reserveBytes) >= 0)
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to reserve ASIO render-ahead buffer: {0}", Bass.LastError);
            return false;
        }

        private void StartRenderThread()
        {
            _running = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "ASIO render-ahead",
                Priority = ThreadPriority.Highest,
            };
            _renderThread.Start();
        }

        private bool WaitForInitialQueue()
        {
            var timeout = Stopwatch.StartNew();
            while (!_queueReady && _running && timeout.ElapsedMilliseconds < START_TIMEOUT_MILLISECONDS)
            {
                Thread.Sleep(1);
            }

            return _queueReady;
        }

        private void RenderLoop()
        {
            try
            {
                Bass.CurrentDevice = _bassDeviceId;
                while (_running)
                {
                    int queuedFrames = QueuedFrames;
                    ObservePolledOutput(queuedFrames);

                    if (queuedFrames < _targetFrames)
                    {
                        RenderChunk();
                        continue;
                    }

                    if (!_queueReady)
                    {
                        _queueReady = true;
                    }

                    _renderWake.WaitOne(2);
                }
            }
            catch (Exception exception)
            {
                _running = false;
                YargLogger.LogException(exception, "ASIO render-ahead thread failed");
            }
        }

        private void ObservePolledOutput(int queuedFrames)
        {
            if (!_queueReady || _outputRequestsReported)
            {
                return;
            }

            _outputClock.ObserveQueueDepth(Volatile.Read(ref _queueGeneration), Stopwatch.GetTimestamp(),
                _generatedFrames, queuedFrames);
        }

        private void RenderChunk()
        {
            lock (_renderLock)
            {
                if (!_running)
                {
                    return;
                }

                int renderedBytes = RenderSourceChunk();
                if (renderedBytes <= 0)
                {
                    return;
                }

                if (!QueueRenderedChunk(renderedBytes))
                {
                    return;
                }

                _generatedFrames += renderedBytes / _bytesPerFrame;
            }
        }

        private int RenderSourceChunk()
        {
            int requestedBytes = _renderBuffer.Length * sizeof(float);
            int renderedBytes = Bass.ChannelGetData(_sourceMixerHandle, _renderBuffer, requestedBytes);
            if (renderedBytes < 0)
            {
                FailRender("Failed to render ASIO audio", Bass.LastError);
                return 0;
            }

            return renderedBytes - renderedBytes % _bytesPerFrame;
        }

        private bool QueueRenderedChunk(int renderedBytes)
        {
            if (Bass.StreamPutData(Handle, _renderBuffer, renderedBytes) >= 0)
            {
                return true;
            }

            FailRender("Failed to queue rendered ASIO audio", Bass.LastError);
            return false;
        }

        private void FailRender(string message, Errors error)
        {
            _running = false;
            YargLogger.LogFormatError("{0}: {1}", message, error);
        }

        private int TargetFrames(int callbackFrames) =>
            Math.Max((int) Math.Ceiling(_sampleRate * RENDER_AHEAD_MILLISECONDS / 1000.0), callbackFrames * 2);

        private double GetFallbackHeardFrame(int outputLatencyFrames)
        {
            if (_outputClock.IsInitialized)
            {
                return 0;
            }

            return Math.Max(0, _generatedFrames - QueuedFrames - outputLatencyFrames);
        }

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
            private readonly int    _sampleRate;
            private readonly int    _callbackFrames;

            private int    _queueGeneration;
            private bool   _initialized;
            private bool   _hasQueueDepth;
            private long   _anchorTimestamp;
            private long   _lastObservationTimestamp;
            private long   _nextOutputFrame;
            private long   _lastObservedOutputFrame;
            private long   _latestSubmittedFrame;
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

            public void ObserveCallback(int queueGeneration, long timestamp, int requestedFrames, int availableFrames)
            {
                lock (_lock)
                {
                    if (queueGeneration != _queueGeneration)
                    {
                        return;
                    }

                    long blockStart = _nextOutputFrame;
                    long submittedFrames = Math.Clamp(availableFrames, 0, requestedFrames);
                    _nextOutputFrame += submittedFrames;
                    UpdateClock(blockStart, _nextOutputFrame, timestamp);
                }
            }

            public void ObserveQueueDepth(int queueGeneration, long timestamp, long generatedFrames, int queuedFrames)
            {
                long outputFrame = Math.Max(0, generatedFrames - Math.Max(0, queuedFrames));
                lock (_lock)
                {
                    if (queueGeneration != _queueGeneration)
                    {
                        return;
                    }

                    if (!_hasQueueDepth)
                    {
                        _lastObservedOutputFrame = outputFrame;
                        _nextOutputFrame = outputFrame;
                        _hasQueueDepth = true;
                        return;
                    }

                    long advancedFrames = outputFrame - _lastObservedOutputFrame;
                    if (advancedFrames <= 0)
                    {
                        return;
                    }

                    // Poll may span several callbacks. Latest callback started one buffer (or the
                    // available underfill amount) before the newly observed output edge.
                    long blockFrames = Math.Min(_callbackFrames, advancedFrames);
                    long blockStart = outputFrame - blockFrames;
                    _lastObservedOutputFrame = outputFrame;
                    _nextOutputFrame = outputFrame;
                    UpdateClock(blockStart, outputFrame, timestamp);
                }
            }

            public double GetHeardFrame(long timestamp, int outputLatencyFrames, long generatedFrames,
                double fallbackHeardFrame)
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
                        double elapsed = (double) (timestamp - _anchorTimestamp) / Stopwatch.Frequency;
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
                    _hasQueueDepth = true;
                    _anchorTimestamp = 0;
                    _lastObservationTimestamp = 0;
                    _nextOutputFrame = nextOutputFrame;
                    _lastObservedOutputFrame = nextOutputFrame;
                    _latestSubmittedFrame = nextOutputFrame;
                }
            }

            private void UpdateClock(long blockStartFrame, long submittedFrame, long timestamp)
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
                    double elapsed = (double) (timestamp - _anchorTimestamp) / Stopwatch.Frequency;
                    double predictedFrame = _anchorFrame + Math.Max(0, elapsed) * _sampleRate;
                    double observationElapsed = (double) Math.Max(0, timestamp - _lastObservationTimestamp) /
                        Stopwatch.Frequency;
                    double blend = 1 - Math.Exp(-observationElapsed / CLOCK_SMOOTHING_SECONDS);
                    _anchorFrame = predictedFrame + blend * (blockStartFrame - predictedFrame);
                    _anchorTimestamp = timestamp;
                }

                _lastObservationTimestamp = timestamp;
                _latestSubmittedFrame = Math.Max(_latestSubmittedFrame, submittedFrame);
            }
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