using System;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>Owns one Burst-generated source attached to a BASS mixer.</summary>
    internal sealed unsafe class BassOneShotStream : IDisposable
    {
        private const int TargetQueueFrames = 8820; // ~200ms at 44.1kHz
        private const int RenderChunkFrames = 256;
        private readonly object _lifecycleLock = new object();
        private readonly object _renderLock = new object();
        private readonly AutoResetEvent _renderWake = new AutoResetEvent(false);
        private readonly OneShotNativeContext* _context;
        private readonly string _name = "Burst one-shot push stream";
        private readonly float[] _renderBuffer;
        private readonly int _bytesPerFrame;
        private readonly int _targetFrames;
        private readonly Thread _renderThread;
        private int _streamHandle;
        private int _mixerHandle;
        private bool _disposed;
        private volatile bool _running = true;

        private float _volume = 1f;
        private bool _enabled = true;

        internal BassOneShotStream(int sampleRate, int channels, float[] sample,
            double[] schedule, double leadTime)
        {
            _context = OneShotProcessor.Create(sample, schedule, sampleRate, channels, leadTime);
            if (_context == null)
            {
                throw new InvalidOperationException("Failed to allocate one-shot state.");
            }

            _bytesPerFrame = channels * sizeof(float);
            _targetFrames = TargetQueueFrames;
            _renderBuffer = new float[RenderChunkFrames * channels];

            _streamHandle = Bass.CreateStream(sampleRate, channels, BassFlags.Float | BassFlags.Decode,
                StreamProcedureType.Push);
            if (_streamHandle == 0)
            {
                OneShotProcessor.Destroy(_context);
                throw new InvalidOperationException($"Failed to create push stream: {Bass.LastError}");
            }

            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "YARG.OneShotRenderAhead"
            };
            _renderThread.Start();
        }

        internal int StreamHandle => _streamHandle;

        internal void Attach(int mixerHandle)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (!BassNativeStream.AddToMixer(mixerHandle, _streamHandle))
                {
                    YargLogger.LogFormatError("Failed to attach {0}: {1}", _name, Bass.LastError);
                    return;
                }
                _mixerHandle = mixerHandle;
            }
        }

        internal void Detach()
        {
            lock (_lifecycleLock)
            {
                if (_mixerHandle == 0)
                {
                    return;
                }

                bool locked = Bass.ChannelLock(_mixerHandle, true);
                if (locked)
                {
                    try
                    {
                        BassNativeStream.RemoveFromMixer(_streamHandle);
                    }
                    finally
                    {
                        Bass.ChannelLock(_mixerHandle, false);
                    }
                }
                else
                {
                    YargLogger.LogFormatError("Failed to lock one-shot mixer for detach: {0}",
                        Bass.LastError);
                }
                _mixerHandle = 0;
            }
        }

        internal void SetVolume(float volume)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }
                _volume = volume;
                ApplyVolume();
            }
        }

        internal void SetEnabled(bool enabled)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }
                _enabled = enabled;
                ApplyVolume();
            }
        }

        internal void SetPaused(bool paused)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                lock (_renderLock)
                {
                    Volatile.Write(ref _context->Paused, paused ? 1 : 0);
                    if (!Bass.ChannelSetPosition(_streamHandle, 0, PositionFlags.Bytes))
                    {
                        YargLogger.LogFormatError("Failed to flush paused one-shot stream: {0}",
                            Bass.LastError);
                    }
                    FillQueue();
                }
                _renderWake.Set();
            }
        }

        internal void Play()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                int pending = Volatile.Read(ref _context->PendingPlays);
                while (pending < OneShotProcessor.MaxActive &&
                    Interlocked.CompareExchange(ref _context->PendingPlays, pending + 1, pending) != pending)
                {
                    pending = Volatile.Read(ref _context->PendingPlays);
                }
                _renderWake.Set();
            }
        }

        internal void SetAnchor(long outputFrame, double songPosition, float speed, bool clearActive)
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                lock (_renderLock)
                {
                    if (!Bass.ChannelSetPosition(_streamHandle, 0, PositionFlags.Bytes))
                    {
                        YargLogger.LogFormatError("Failed to flush one-shot push stream: {0}",
                            Bass.LastError);
                    }
                    OneShotProcessor.SetAnchor(_context, outputFrame, songPosition, speed,
                        clearActive);
                    FillQueue();
                }
                _renderWake.Set();
            }
        }

        private void ApplyVolume()
        {
            if (_streamHandle != 0 &&
                !Bass.ChannelSetAttribute(_streamHandle, ChannelAttribute.Volume,
                    _enabled ? _volume : 0))
            {
                YargLogger.LogFormatError("Failed to set one-shot stream volume: {0}",
                    Bass.LastError);
            }
        }

        private void RenderLoop()
        {
            try
            {
                while (_running)
                {
                    lock (_renderLock)
                    {
                        if (_running)
                        {
                            FillQueue();
                        }
                    }
                    _renderWake.WaitOne(2);
                }
            }
            catch (Exception exception)
            {
                _running = false;
                YargLogger.LogException(exception, "One-shot render-ahead thread failed");
            }
        }

        private void FillQueue()
        {
            while (_running)
            {
                int queuedBytes = Bass.StreamPutData(_streamHandle, IntPtr.Zero, 0);
                if (queuedBytes < 0)
                {
                    YargLogger.LogFormatError("Failed to query one-shot push stream: {0}",
                        Bass.LastError);
                    _running = false;
                    return;
                }

                int queuedFrames = queuedBytes / _bytesPerFrame;
                if (queuedFrames >= _targetFrames)
                {
                    return;
                }

                int frames = Math.Min(RenderChunkFrames, _targetFrames - queuedFrames);
                int bytes = frames * _bytesPerFrame;
                fixed (float* buffer = _renderBuffer)
                {
                    OneShotProcessor.Render(_context, buffer, bytes);
                }
                if (Bass.StreamPutData(_streamHandle, _renderBuffer, bytes) < 0)
                {
                    YargLogger.LogFormatError("Failed to fill one-shot push stream: {0}",
                        Bass.LastError);
                    _running = false;
                    return;
                }
            }
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                if (_mixerHandle != 0)
                {
                    bool locked = Bass.ChannelLock(_mixerHandle, true);
                    if (locked)
                    {
                        try
                        {
                            BassNativeStream.RemoveFromMixer(_streamHandle);
                        }
                        finally
                        {
                            Bass.ChannelLock(_mixerHandle, false);
                        }
                    }
                    else
                    {
                        YargLogger.LogFormatError("Failed to lock one-shot mixer for dispose: {0}",
                            Bass.LastError);
                    }
                }
                _mixerHandle = 0;
                _running = false;
                _renderWake.Set();
            }

            if (_renderThread != Thread.CurrentThread)
            {
                _renderThread.Join();
            }

            if (_streamHandle != 0)
            {
                Bass.StreamFree(_streamHandle);
                _streamHandle = 0;
            }
            OneShotProcessor.Destroy(_context);
            _renderWake.Dispose();
        }
    }
}
