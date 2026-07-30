using System;
using System.Threading;
using ManagedBass;
using Unity.Burst;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    /// <summary>Owns one Burst-generated source attached to a BASS mixer.</summary>
    internal sealed unsafe class BassOneShotStream : IDisposable
    {
        private static readonly FunctionPointer<BassNativeStreamProcedure> Callback =
            BurstCompiler.CompileFunctionPointer<BassNativeStreamProcedure>(OneShotProcessor.ProcessStream);

        private readonly object _lifecycleLock = new object();
        private readonly OneShotNativeContext* _context;
        private readonly string _name = "Burst one-shot stream";
        private int _streamHandle;
        private int _mixerHandle;
        private bool _disposed;

        internal BassOneShotStream(int sampleRate, int channels, float[] sample,
            double[] schedule, double leadTime)
        {
            _context = OneShotProcessor.Create(sample, schedule, sampleRate, channels, leadTime);
            if (_context == null)
            {
                throw new InvalidOperationException("Failed to allocate one-shot state.");
            }

            _streamHandle = BassNativeStream.Create(
                sampleRate, channels, Callback.Value, (IntPtr) _context);
            if (_streamHandle == 0)
            {
                OneShotProcessor.Destroy(_context);
                throw new InvalidOperationException($"Failed to create one-shot stream: {Bass.LastError}");
            }
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

                bool locked = Bass.ChannelLock(_mixerHandle);
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
                OneShotProcessor.SetVolume(_context, volume);
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
                OneShotProcessor.SetEnabled(_context, enabled);
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

                Volatile.Write(ref _context->IsPaused, paused ? 1 : 0);
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

                int pending = Volatile.Read(ref _context->PendingPlaybackCount);
                while (pending < OneShotProcessor.MAX_ACTIVE_PLAYBACKS &&
                    Interlocked.CompareExchange(
                        ref _context->PendingPlaybackCount, pending + 1, pending) != pending)
                {
                    pending = Volatile.Read(ref _context->PendingPlaybackCount);
                }
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

                OneShotProcessor.SetAnchor(_context, outputFrame, songPosition, speed, clearActive);
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
                    bool locked = Bass.ChannelLock(_mixerHandle);
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
                if (_streamHandle != 0)
                {
                    Bass.StreamFree(_streamHandle);
                    _streamHandle = 0;
                }
                OneShotProcessor.Destroy(_context);
            }
        }
    }
}
