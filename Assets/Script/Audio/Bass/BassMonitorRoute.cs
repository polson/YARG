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
        private const BassFlags RequiredStreamFlags = BassFlags.Float | BassFlags.Decode;

        private readonly ResetKind _resetKind;
        private readonly Action?   _resetEffects;

        public int Handle { get; }

        private BassMonitorSource(int handle, ResetKind resetKind, Action? resetEffects)
        {
            Handle = handle;
            _resetKind = resetKind;
            _resetEffects = resetEffects;
        }

        public static BassMonitorSource? CreateSplit(int handle, Action? resetEffects = null) =>
            Create(handle, ResetKind.Split, resetEffects);

        private static BassMonitorSource? Create(int handle, ResetKind resetKind, Action? resetEffects)
        {
            var streamInfo = Bass.ChannelGetInfo(handle);
            if (handle == 0 || !HasRequiredStreamFlags(streamInfo.Flags))
            {
                YargLogger.LogFormatError("Monitor source {0} must be a float decoding stream (flags: {1})", handle,
                    streamInfo.Flags);
                return null;
            }

            if (!SupportsResetKind(handle, resetKind))
            {
                return null;
            }

            return new BassMonitorSource(handle, resetKind, resetEffects);
        }

        public int GetDevice() => Bass.ChannelGetDevice(Handle);

        public bool MoveToDevice(int deviceId)
        {
            int sourceDevice = GetDevice();
            if (sourceDevice < 0)
            {
                YargLogger.LogFormatError("Failed to get monitor source device: {0}", Bass.LastError);
                return false;
            }

            if (sourceDevice == deviceId)
            {
                return true;
            }

            if (Bass.ChannelSetDevice(Handle, deviceId))
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to move monitor source to BASS device {0}: {1}", deviceId,
                Bass.LastError);
            return false;
        }

        public bool ResetToLive()
        {
            if (!ResetStream())
            {
                return false;
            }

            return ResetEffects();
        }

        private static bool HasRequiredStreamFlags(BassFlags flags) =>
            (flags & RequiredStreamFlags) == RequiredStreamFlags;

        private static bool SupportsResetKind(int handle, ResetKind resetKind)
        {
            switch (resetKind)
            {
                case ResetKind.Split:
                    if (BassMix.SplitStreamGetSource(handle) != 0)
                    {
                        return true;
                    }

                    YargLogger.LogFormatError("Monitor source {0} is not a splitter stream: {1}", handle,
                        Bass.LastError);
                    return false;
                default:
                    return false;
            }
        }

        private bool ResetStream()
        {
            bool succeeded = _resetKind switch
            {
                ResetKind.Split => BassMix.SplitStreamReset(Handle, 0),
                _               => false,
            };
            if (succeeded)
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to reset monitor source: {0}", Bass.LastError);
            return false;
        }

        private bool ResetEffects()
        {
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
            Split,
        }
    }

    /// <summary>
    /// Registration token for one monitor source. Disposal synchronously detaches the source.
    /// </summary>
    internal sealed class BassMonitorRoute : IDisposable
    {
        private BassAudioOutput? _owner;
        private Action? _attached;
        private Action? _detached;

        internal BassMonitorSource Source     { get; }
        internal bool              IsAttached { get; set; }
        public   double            Volume     { get; private set; }

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

        internal void SetLifecycleCallbacks(Action? attached, Action? detached)
        {
            _attached = attached;
            _detached = detached;
        }

        internal void MarkAttached()
        {
            IsAttached = true;
            InvokeLifecycleCallback(_attached);
        }

        internal void MarkDetached()
        {
            if (!IsAttached)
            {
                return;
            }

            IsAttached = false;
            InvokeLifecycleCallback(_detached);
        }

        internal void InvalidateOwner()
        {
            Interlocked.Exchange(ref _owner, null);
            MarkDetached();
        }

        private static void InvokeLifecycleCallback(Action? callback)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception exception)
            {
                YargLogger.LogException(exception, "Monitor route lifecycle callback failed");
            }
        }
    }
}
