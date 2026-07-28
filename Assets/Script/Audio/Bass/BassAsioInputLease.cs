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

        private BassAsioInputLease? _lease;
        private bool _valid = true;
        private bool _monitorEnabled;
        private bool _attached;

        private int _rootHandle;

        public int ChannelIndex { get; }
        public AsioInputDescriptor Descriptor { get; private set; }
        internal int RootHandle => _rootHandle;
        internal bool IsAttached => _attached;

        private BassAsioInput(string driverId, string driverName, int channelIndex,
            string name, int group, int sampleRate, int rootHandle)
        {
            ChannelIndex = channelIndex;
            _rootHandle = rootHandle;
            Descriptor = new AsioInputDescriptor(driverId, driverName, channelIndex,
                name, group, sampleRate, 0);
        }

        public static BassAsioInput? Create(string driverId, string driverName,
            int channelIndex, string name, int group, int sampleRate)
        {
            int rootHandle = 0;
            try
            {
                rootHandle = Bass.CreateStream(sampleRate, 1,
                    BassFlags.Float | BassFlags.Decode, StreamProcedureType.Push);
                if (rootHandle == 0)
                {
                    return null;
                }

                if (!Bass.ChannelSetAttribute(rootHandle, ChannelAttribute.Volume, 0))
                {
                    return null;
                }

                var input = new BassAsioInput(driverId, driverName, channelIndex, name, group,
                    sampleRate, rootHandle);
                rootHandle = 0;
                return input;
            }
            finally
            {
                if (rootHandle != 0)
                {
                    Bass.StreamFree(rootHandle);
                }
            }
        }

        internal bool AttachToOutputMixer(int outputMixerHandle)
        {
            if (_attached)
            {
                return true;
            }

            // Only acquired microphones enter the realtime graph. Mixer buffering provides a
            // non-consuming analysis tap without enabling every physical ASIO input.
            var flags = BassFlags.MixerChanBuffer | BassFlags.MixerChanDownMix |
                BassFlags.MixerChanNoRampin;
            if (!BassMix.MixerAddChannel(outputMixerHandle, _rootHandle, flags))
            {
                return false;
            }
            _attached = true;
            return true;
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
                return BassMix.ChannelGetData(_rootHandle, buffer,
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
                if (!Bass.ChannelSetAttribute(_rootHandle, ChannelAttribute.Volume, volume))
                {
                    YargLogger.LogFormatError("Failed to monitor ASIO input {0}: {1}",
                        ChannelIndex, Bass.LastError);
                    return false;
                }
                _monitorEnabled = true;
                return true;
            }
        }

        public void SetMonitoringLevel(BassAsioInputLease lease, double volume)
        {
            lock (_lock)
            {
                if (_valid && _monitorEnabled && ReferenceEquals(_lease, lease))
                {
                    Bass.ChannelSetAttribute(_rootHandle, ChannelAttribute.Volume, volume);
                }
            }
        }

        public void DisableMonitoring(BassAsioInputLease lease)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_lease, lease))
                {
                    DisableMonitor();
                }
            }
        }

        public void Release(BassAsioInputLease lease)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_lease, lease))
                {
                    DisableMonitor();
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
                DisableMonitor();
                lease = _lease;
                _lease = null;
            }
            lease?.InvalidateFromBackend();
        }

        public void FreeNativeStreams()
        {
            if (_rootHandle != 0)
            {
                if (_attached)
                {
                    BassMix.MixerRemoveChannel(_rootHandle);
                    _attached = false;
                }
                Bass.StreamFree(_rootHandle);
                _rootHandle = 0;
            }
        }

        private void DisableMonitor()
        {
            if (_monitorEnabled)
            {
                Bass.ChannelSetAttribute(_rootHandle, ChannelAttribute.Volume, 0);
                _monitorEnabled = false;
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
