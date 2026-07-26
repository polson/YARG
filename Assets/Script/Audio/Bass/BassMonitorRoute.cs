#nullable enable
using System;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Non-owning description of a decoding stream that can be routed to an output backend.
    /// The stream owner must keep this source alive until its route has been disposed.
    /// </summary>
    internal sealed class BassMonitorSource
    {
        private readonly ResetKind _resetKind;
        private readonly Action? _resetEffects;

        public int Handle { get; }

        private BassMonitorSource(int handle, ResetKind resetKind, Action? resetEffects)
        {
            Handle = handle;
            _resetKind = resetKind;
            _resetEffects = resetEffects;
        }

        public static BassMonitorSource? CreatePush(int handle, Action? resetEffects = null) =>
            Create(handle, ResetKind.Push, resetEffects);

        public static BassMonitorSource? CreateSplit(int handle, Action? resetEffects = null) =>
            Create(handle, ResetKind.Split, resetEffects);

        private static BassMonitorSource? Create(int handle, ResetKind resetKind,
            Action? resetEffects)
        {
            var info = Bass.ChannelGetInfo(handle);
            const BassFlags requiredFlags = BassFlags.Float | BassFlags.Decode;
            if (handle == 0 || (info.Flags & requiredFlags) != requiredFlags)
            {
                YargLogger.LogFormatError(
                    "Monitor source {0} must be a float decoding stream (flags: {1})",
                    handle, info.Flags);
                return null;
            }

            if (resetKind == ResetKind.Push && Bass.StreamPutData(handle, IntPtr.Zero, 0) < 0)
            {
                YargLogger.LogFormatError("Monitor source {0} is not a push stream: {1}",
                    handle, Bass.LastError);
                return null;
            }
            if (resetKind == ResetKind.Split && BassMix.SplitStreamGetSource(handle) == 0)
            {
                YargLogger.LogFormatError("Monitor source {0} is not a splitter stream: {1}",
                    handle, Bass.LastError);
                return null;
            }

            return new BassMonitorSource(handle, resetKind, resetEffects);
        }

        public int GetDevice() => Bass.ChannelGetDevice(Handle);

        public bool MoveToDevice(int deviceId)
        {
            int currentDevice = GetDevice();
            if (currentDevice < 0)
            {
                YargLogger.LogFormatError("Failed to get monitor source device: {0}", Bass.LastError);
                return false;
            }
            if (currentDevice == deviceId)
            {
                return true;
            }
            if (Bass.ChannelSetDevice(Handle, deviceId))
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to move monitor source to BASS device {0}: {1}",
                deviceId, Bass.LastError);
            return false;
        }

        public bool ResetToLive()
        {
            bool reset = _resetKind switch
            {
                ResetKind.Push => Bass.ChannelSetPosition(Handle, 0, PositionFlags.Bytes),
                ResetKind.Split => BassMix.SplitStreamReset(Handle, 0),
                _ => false,
            };
            if (!reset)
            {
                YargLogger.LogFormatError("Failed to reset monitor source: {0}", Bass.LastError);
                return false;
            }

            try
            {
                _resetEffects?.Invoke();
                return true;
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Failed to reset monitor source effects");
                return false;
            }
        }

        private enum ResetKind
        {
            Push,
            Split,
        }
    }

    /// <summary>
    /// Registration token for one monitor source. Disposal synchronously detaches the source.
    /// </summary>
    internal sealed class BassMonitorRoute : IDisposable
    {
        private BassAudioOutput? _owner;

        internal BassMonitorSource Source { get; }
        internal bool IsAttached { get; set; }
        public double Volume { get; private set; }

        internal BassMonitorRoute(BassAudioOutput owner, BassMonitorSource source, double volume)
        {
            _owner = owner;
            Source = source;
            Volume = volume;
        }

        public void SetVolume(double volume)
        {
            if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }

            Volume = volume;
            Volatile.Read(ref _owner)?.SetMonitorVolume(this, volume);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Remove(this);
        }

        internal void InvalidateOwner()
        {
            Interlocked.Exchange(ref _owner, null);
            IsAttached = false;
        }
    }
}
