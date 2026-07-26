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
        private const int RENDER_AHEAD_MILLISECONDS = 15;
        private const int RENDER_CHUNK_FRAMES = 128;
        private const int START_TIMEOUT_MILLISECONDS = 2000;

        private readonly int _sourceMixerHandle;
        private readonly int _bassDeviceId;
        private readonly int _sampleRate;
        private readonly int _bytesPerFrame;
        private readonly float[] _renderBuffer;
        private readonly object _renderLock = new();
        private readonly AutoResetEvent _renderWake = new(false);
        private readonly int _targetFrames;

        private Thread? _renderThread;
        private volatile bool _running;
        private volatile bool _queueReady;
        private int _disposed;
        private long _maximumRenderTicks;
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

        public long UnderrunCount => Volatile.Read(ref _underruns);

        private BassRenderAheadStream(int sourceMixerHandle, int bassDeviceId, int sampleRate,
            int channels, int callbackFrames, int handle)
        {
            _sourceMixerHandle = sourceMixerHandle;
            _bassDeviceId = bassDeviceId;
            _sampleRate = sampleRate;
            _bytesPerFrame = channels * sizeof(float);
            _renderBuffer = new float[RENDER_CHUNK_FRAMES * channels];
            _targetFrames = TargetFrames(callbackFrames);
            Handle = handle;
        }

        public static BassRenderAheadStream? Create(int sourceMixerHandle, int bassDeviceId,
            int sampleRate, int channels, int callbackFrames)
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
                channels, callbackFrames, handle);
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
        public void OnOutputRequested(int frames)
        {
            int queuedFrames = QueuedFrames;
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
            }
            _renderWake.Set();
        }

        /// <summary>
        /// Gets source position at output, accounting for push queue and ASIO driver latency.
        /// Producer lock keeps source decode position fixed while queue depth is sampled.
        /// </summary>
        public long GetSourcePosition(int sourceHandle, int outputLatencyFrames)
        {
            lock (_renderLock)
            {
                int delayBytes = FramesToBytes(outputLatencyFrames + QueuedFrames);
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
            Interlocked.Exchange(ref _maximumRenderTicks, 0);
            Interlocked.Exchange(ref _minimumQueuedFrames, QueuedFrames);
            Interlocked.Exchange(ref _underruns, 0);
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
                    if (QueuedFrames >= _targetFrames)
                    {
                        _queueReady = true;
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

        private void RenderChunk()
        {
            lock (_renderLock)
            {
                if (!_running)
                {
                    return;
                }

                long start = Stopwatch.GetTimestamp();
                try
                {
                    int requestedBytes = _renderBuffer.Length * sizeof(float);
                    int bytesRead = Bass.ChannelGetData(
                        _sourceMixerHandle, _renderBuffer, requestedBytes);
                    if (bytesRead < 0)
                    {
                        FailRender("Failed to render ASIO audio", Bass.LastError);
                        return;
                    }

                    bytesRead -= bytesRead % _bytesPerFrame;
                    if (bytesRead > 0 && Bass.StreamPutData(Handle, _renderBuffer, bytesRead) < 0)
                    {
                        FailRender("Failed to queue rendered ASIO audio", Bass.LastError);
                    }
                }
                finally
                {
                    UpdateMaximum(ref _maximumRenderTicks, Stopwatch.GetTimestamp() - start);
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

        private int FramesToBytes(int frames)
        {
            long bytes = Math.Max(0, frames) * (long) _bytesPerFrame;
            return (int) Math.Min(bytes, int.MaxValue);
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
