#nullable enable
using System;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class AsioInputDescriptor
    {
        public string DriverId { get; }
        public string DriverName { get; }
        public int ChannelIndex { get; }
        public string Name { get; }
        public int Group { get; }
        public int SampleRate { get; }
        public int InputLatencyFrames { get; }

        internal AsioInputDescriptor(string driverId, string driverName, int channelIndex,
            string name, int group, int sampleRate, int inputLatencyFrames)
        {
            DriverId = driverId;
            DriverName = driverName;
            ChannelIndex = channelIndex;
            Name = name;
            Group = group;
            SampleRate = sampleRate;
            InputLatencyFrames = inputLatencyFrames;
        }

        public override string ToString() => $"{ChannelIndex}: {Name}";
    }

    internal enum AsioInputAcquireResult
    {
        Success,
        NoAsioBackend,
        DriverMismatch,
        UnavailableChannel,
        AlreadyInUse,
    }

    /// <summary>
    /// Backend-owned BASS streams for one ASIO input channel.
    /// </summary>
    internal sealed class BassAsioInput
    {
        private readonly object _lock = new();
        private readonly int _outputMixerHandle;

        private BassAsioInputLease? _lease;
        private bool _valid = true;
        private bool _monitorAttached;

        private int _rootHandle;
        private int _pumpHandle;
        private int _analysisHandle;
        private int _monitorHandle;

        public int ChannelIndex { get; }
        public AsioInputDescriptor Descriptor { get; private set; }
        internal int RootHandle => _rootHandle;

        private BassAsioInput(string driverId, string driverName, int channelIndex,
            string name, int group, int sampleRate, int outputMixerHandle,
            int rootHandle, int pumpHandle, int analysisHandle, int monitorHandle)
        {
            ChannelIndex = channelIndex;
            _outputMixerHandle = outputMixerHandle;
            _rootHandle = rootHandle;
            _pumpHandle = pumpHandle;
            _analysisHandle = analysisHandle;
            _monitorHandle = monitorHandle;
            Descriptor = new AsioInputDescriptor(driverId, driverName, channelIndex,
                name, group, sampleRate, 0);
        }

        public static BassAsioInput? Create(string driverId, string driverName,
            int channelIndex, string name, int group, int sampleRate, int outputMixerHandle)
        {
            int rootHandle = 0;
            int pumpHandle = 0;
            int analysisHandle = 0;
            int monitorHandle = 0;
            bool pumpAttached = false;
            try
            {
                rootHandle = Bass.CreateStream(sampleRate, 1,
                    BassFlags.Float | BassFlags.Decode, StreamProcedureType.Push);
                if (rootHandle == 0)
                {
                    return null;
                }

                var splitFlags = BassFlags.Decode | BassFlags.SplitPosition;
                pumpHandle = BassMix.CreateSplitStream(rootHandle, splitFlags, null);
                analysisHandle = BassMix.CreateSplitStream(rootHandle,
                    splitFlags | BassFlags.SplitSlave, null);
                monitorHandle = BassMix.CreateSplitStream(rootHandle,
                    splitFlags | BassFlags.SplitSlave, null);
                if (pumpHandle == 0 || analysisHandle == 0 || monitorHandle == 0 ||
                    !Bass.ChannelSetAttribute(pumpHandle, ChannelAttribute.Volume, 0) ||
                    !BassMix.MixerAddChannel(outputMixerHandle, pumpHandle,
                        BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin))
                {
                    return null;
                }
                pumpAttached = true;

                var input = new BassAsioInput(driverId, driverName, channelIndex, name, group,
                    sampleRate, outputMixerHandle, rootHandle, pumpHandle,
                    analysisHandle, monitorHandle);
                rootHandle = pumpHandle = analysisHandle = monitorHandle = 0;
                return input;
            }
            finally
            {
                if (monitorHandle != 0)
                {
                    Bass.StreamFree(monitorHandle);
                }
                if (analysisHandle != 0)
                {
                    Bass.StreamFree(analysisHandle);
                }
                if (pumpHandle != 0)
                {
                    if (pumpAttached)
                    {
                        BassMix.MixerRemoveChannel(pumpHandle);
                    }
                    Bass.StreamFree(pumpHandle);
                }
                if (rootHandle != 0)
                {
                    Bass.StreamFree(rootHandle);
                }
            }
        }

        public void SetInputLatency(int frames)
        {
            Descriptor = new AsioInputDescriptor(Descriptor.DriverId, Descriptor.DriverName,
                Descriptor.ChannelIndex, Descriptor.Name, Descriptor.Group,
                Descriptor.SampleRate, Math.Max(0, frames));
        }

        public AsioInputAcquireResult TryAcquire(out BassAsioInputLease? lease)
        {
            lock (_lock)
            {
                lease = null;
                if (!_valid)
                {
                    return AsioInputAcquireResult.UnavailableChannel;
                }
                if (_lease != null)
                {
                    return AsioInputAcquireResult.AlreadyInUse;
                }
                if (!BassMix.SplitStreamReset(_analysisHandle, 0))
                {
                    YargLogger.LogFormatError("Failed to reset ASIO input {0}: {1}",
                        ChannelIndex, Bass.LastError);
                    return AsioInputAcquireResult.UnavailableChannel;
                }

                _lease = lease = new BassAsioInputLease(this, Descriptor);
                return AsioInputAcquireResult.Success;
            }
        }

        public int Read(BassAsioInputLease lease, float[] buffer)
        {
            lock (_lock)
            {
                if (!_valid || !ReferenceEquals(_lease, lease))
                {
                    return -1;
                }
                return Bass.ChannelGetData(_analysisHandle, buffer,
                    checked(buffer.Length * sizeof(float)));
            }
        }

        public bool Owns(BassAsioInputLease lease)
        {
            lock (_lock)
            {
                return _valid && ReferenceEquals(_lease, lease);
            }
        }

        public bool EnableMonitoring(BassAsioInputLease lease, double volume)
        {
            lock (_lock)
            {
                if (!_valid || !ReferenceEquals(_lease, lease))
                {
                    return false;
                }
                if (_monitorAttached)
                {
                    return Bass.ChannelSetAttribute(
                        _monitorHandle, ChannelAttribute.Volume, volume);
                }
                if (!BassMix.SplitStreamReset(_monitorHandle, 0) ||
                    !Bass.ChannelSetAttribute(_monitorHandle, ChannelAttribute.Volume, volume) ||
                    !BassMix.MixerAddChannel(_outputMixerHandle, _monitorHandle,
                        BassFlags.MixerChanDownMix | BassFlags.MixerChanNoRampin))
                {
                    YargLogger.LogFormatError("Failed to monitor ASIO input {0}: {1}",
                        ChannelIndex, Bass.LastError);
                    return false;
                }
                _monitorAttached = true;
                return true;
            }
        }

        public void SetMonitoringLevel(BassAsioInputLease lease, double volume)
        {
            lock (_lock)
            {
                if (_valid && _monitorAttached && ReferenceEquals(_lease, lease))
                {
                    Bass.ChannelSetAttribute(_monitorHandle, ChannelAttribute.Volume, volume);
                }
            }
        }

        public void DisableMonitoring(BassAsioInputLease lease)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_lease, lease))
                {
                    DetachMonitor();
                }
            }
        }

        public void Release(BassAsioInputLease lease)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_lease, lease))
                {
                    DetachMonitor();
                    _lease = null;
                }
            }
        }

        public void Invalidate()
        {
            BassAsioInputLease? lease;
            lock (_lock)
            {
                if (!_valid)
                {
                    return;
                }
                _valid = false;
                DetachMonitor();
                lease = _lease;
                _lease = null;
            }
            lease?.InvalidateFromBackend();
        }

        public void FreeNativeStreams()
        {
            if (_monitorHandle != 0)
            {
                Bass.StreamFree(_monitorHandle);
                _monitorHandle = 0;
            }
            if (_analysisHandle != 0)
            {
                Bass.StreamFree(_analysisHandle);
                _analysisHandle = 0;
            }
            if (_pumpHandle != 0)
            {
                BassMix.MixerRemoveChannel(_pumpHandle);
                Bass.StreamFree(_pumpHandle);
                _pumpHandle = 0;
            }
            if (_rootHandle != 0)
            {
                Bass.StreamFree(_rootHandle);
                _rootHandle = 0;
            }
        }

        private void DetachMonitor()
        {
            if (_monitorAttached)
            {
                BassMix.MixerRemoveChannel(_monitorHandle);
                _monitorAttached = false;
            }
        }
    }

    /// <summary>
    /// Exclusive access to one pre-created ASIO input. Backend invalidates lease on shutdown.
    /// </summary>
    internal sealed class BassAsioInputLease : IDisposable
    {
        private readonly BassAsioInput _input;
        private int _valid = 1;

        public AsioInputDescriptor Descriptor { get; }
        public bool IsValid => Volatile.Read(ref _valid) != 0 && _input.Owns(this);

        internal BassAsioInputLease(BassAsioInput input, AsioInputDescriptor descriptor)
        {
            _input = input;
            Descriptor = descriptor;
        }

        public int Read(float[] buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            return Volatile.Read(ref _valid) != 0 ? _input.Read(this, buffer) : -1;
        }

        public bool EnableMonitoring(double volume)
        {
            ValidateVolume(volume);
            return IsValid && _input.EnableMonitoring(this, volume);
        }

        public void SetMonitoringLevel(double volume)
        {
            ValidateVolume(volume);
            if (Volatile.Read(ref _valid) != 0)
            {
                _input.SetMonitoringLevel(this, volume);
            }
        }

        public void DisableMonitoring() => _input.DisableMonitoring(this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _valid, 0) == 0)
            {
                return;
            }
            _input.Release(this);
        }

        internal void InvalidateFromBackend()
        {
            Interlocked.Exchange(ref _valid, 0);
        }

        private static void ValidateVolume(double volume)
        {
            if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }
        }
    }
}
