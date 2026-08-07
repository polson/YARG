#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    /// <summary>
    ///     Manages all recording devices and the mics opened on them. Each physical device
    ///     is captured once and shared by every mic on it, so multi-input devices (e.g. a
    ///     USB audio interface with several inputs) only open the device a single time.
    ///     The manager probes devices to discover their real channel count, caches the
    ///     results, tracks which channels are already claimed, and handles
    ///     <see cref="RecordingSession" /> instances as mics are added and removed.
    /// </summary>
    internal sealed class BassMicManager
    {
        private readonly List<ActiveMic>         _activeMics   = new();
        private readonly Dictionary<string, int> _channelCache = new(StringComparer.Ordinal);
        private readonly object                  _lock         = new();

        /// <summary>
        ///     Opens a mic on a physical device and claims its capture channel. All mics on
        ///     the same device share one <see cref="RecordingSession" />. Returns null if the
        ///     channel is already claimed or the device can't be opened.
        /// </summary>
        public MicDevice? CreateMic(InputDeviceInfo device)
        {
            if (device.DeviceId < 0)
            {
                int resolved = FindDeviceIndexByName(device.Name);
                if (resolved < 0)
                {
                    return null;
                }

                device = new InputDeviceInfo(resolved, device.Name, device.Channel, device.ChannelCount);
            }

            if (IsChannelClaimed(device.DeviceId, device.Channel))
            {
                return null;
            }

            int captureChannels = GetChannelCount(device.DeviceId, device.Name);

            lock (_lock)
            {
                var session = GetOrCreateSession(device.DeviceId, device.Name, captureChannels);
                if (session == null)
                {
                    return null;
                }

                var mic = BassMicDevice.Create(device.DeviceId, device.DisplayName, session, device.Channel);
                if (mic == null)
                {
                    if (FindActive(device.DeviceId) == null)
                    {
                        session.Dispose();
                        FreeDevice(device.DeviceId);
                    }

                    return null;
                }

                var entry = new ActiveMic(device.DeviceId, session);
                _activeMics.Add(entry);
                mic.Disposed += () => ReleaseMic(entry);
                mic.SetMonitoringLevel(SettingsManager.Settings.VocalMonitoring.Value);
                return mic;
            }
        }

        /// <summary>
        ///     Returns every usable input device and the unclaimed channel(s) on each.
        ///     Probing devices to learn their channel count is cached per device name.
        /// </summary>
        public List<InputDeviceInfo> GetAllDevices()
        {
            var usable = GetDevices()
                .Where(d => IsUsable(d.Info))
                .ToList();

            ProbeChannels(usable);

            var result = new List<InputDeviceInfo>();
            foreach ((int id, var info) in usable)
            {
                result.AddRange(GetUnclaimedInputs(id, info.Name));
            }

            return result;
        }

        public Task<List<InputDeviceInfo>> GetAllDevicesAsync(CancellationToken ct = default) =>
            Task.Run(GetAllDevices, ct);

        private static IEnumerable<(int Id, DeviceInfo Info)> GetDevices()
        {
            for (int i = 0; Bass.RecordGetDeviceInfo(i, out var info); i++)
            {
                yield return (i, info);
            }
        }

        private static bool IsUsable(DeviceInfo info)
        {
            if (!info.IsEnabled || info.IsLoopback)
            {
                return false;
            }

            if (info.Name == "Default")
            {
                return false;
            }

            if (info.Name.StartsWith("Loopback", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static int FindDeviceIndexByName(string name)
        {
            foreach ((int id, var info) in GetDevices())
            {
                if (IsUsable(info) && info.Name == name)
                {
                    return id;
                }
            }

            return -1;
        }

        private List<InputDeviceInfo> GetUnclaimedInputs(int deviceId, string name)
        {
            int channels = GetChannelCount(deviceId, name);
            var list = new List<InputDeviceInfo>(channels);

            for (int ch = 0; ch < channels; ch++)
            {
                if (IsChannelClaimed(deviceId, ch))
                {
                    continue;
                }

                list.Add(new InputDeviceInfo(deviceId, name, ch, channels));
            }

            return list;
        }

        private int GetChannelCount(int deviceId, string name)
        {
            lock (_lock)
            {
                var active = FindActive(deviceId);
                if (active != null)
                {
                    return active.Session.Channels;
                }

                if (_channelCache.TryGetValue(name, out int cached))
                {
                    return Math.Max(1, cached);
                }
            }

            int probed;
            lock (_lock)
            {
                probed = ChannelProbe.Probe(deviceId, name);
                _channelCache[name] = probed;
            }

            return probed;
        }

        private void ProbeChannels(List<(int Id, DeviceInfo Info)> devices)
        {
            lock (_lock)
            {
                foreach ((int id, var info) in devices)
                {
                    GetChannelCount(id, info.Name);
                }
            }
        }

        private bool IsChannelClaimed(int deviceId, int channel)
        {
            lock (_lock)
            {
                return FindActive(deviceId)?.Session.IsChannelClaimed(channel) ?? false;
            }
        }

        private RecordingSession? GetOrCreateSession(int deviceId, string name, int channels)
        {
            var active = FindActive(deviceId);
            if (active != null)
            {
                return active.Session;
            }

            if (!Bass.RecordInit(deviceId) && Bass.LastError != Errors.Already)
            {
                YargLogger.LogFormatError("Failed to init recording device [{0}] '{1}': {2}", deviceId, name,
                    Bass.LastError);
                return null;
            }

            Bass.CurrentRecordingDevice = deviceId;
            var session = RecordingSession.Create(deviceId, channels);
            if (session == null)
            {
                FreeDevice(deviceId);
                return null;
            }

            return session;
        }

        private void ReleaseMic(ActiveMic mic)
        {
            lock (_lock)
            {
                _activeMics.Remove(mic);
                if (FindActive(mic.DeviceId) != null)
                {
                    return;
                }

                mic.Session.Dispose();
                FreeDevice(mic.DeviceId);
            }
        }

        private static void FreeDevice(int deviceId)
        {
            if (!Bass.RecordGetDeviceInfo(deviceId, out var info) || !info.IsInitialized)
            {
                return;
            }

            Bass.CurrentRecordingDevice = deviceId;
            if (!Bass.RecordFree())
            {
                YargLogger.LogFormatWarning("Failed to free recording device [{0}]: {1}", deviceId, Bass.LastError);
            }
        }

        private ActiveMic? FindActive(int deviceId) => _activeMics.FirstOrDefault(m => m.DeviceId == deviceId);

        private sealed class ActiveMic
        {
            public ActiveMic(int deviceId, RecordingSession session)
            {
                DeviceId = deviceId;
                Session = session;
            }

            public int              DeviceId { get; }
            public RecordingSession Session  { get; }
        }

        private sealed class ChannelProbe : IDisposable
        {
            private const int TIMEOUT_MS = 250;

            private static readonly (int Channels, int Rate)[] Attempts =
            {
                (8, 48000),
                (8, 44100),
                (2, 48000),
                (2, 44100),
                (1, 48000),
                (1, 44100),
            };

            private readonly ManualResetEventSlim _gotFrame = new(false);
            private readonly int                  _reportedChannels;
            private          short[]              _frame = Array.Empty<short>();

            private ChannelProbe(int reportedChannels)
            {
                _reportedChannels = reportedChannels;
            }

            public void Dispose() => _gotFrame.Dispose();

            public static int Probe(int deviceId, string name)
            {
                bool initialized = Bass.RecordInit(deviceId);
                if (!initialized && Bass.LastError != Errors.Already)
                {
                    return 1;
                }

                Bass.CurrentRecordingDevice = deviceId;
                int devicePeriod = Bass.GetConfig(Configuration.DevicePeriod);
                try
                {
                    foreach ((int channels, int rate) in Attempts)
                    {
                        var probe = new ChannelProbe(channels);
                        int handle = Bass.RecordStart(rate, channels, BassFlags.Default,
                            devicePeriod, probe.Callback, IntPtr.Zero);

                        if (handle == 0)
                        {
                            continue;
                        }

                        int useableChannels = probe.WaitAndAnalyze(deviceId, name);
                        return Math.Max(1, useableChannels);
                    }
                }
                finally
                {
                    if (initialized)
                    {
                        Bass.RecordFree();
                    }
                }

                return 1;
            }

            private int WaitAndAnalyze(int deviceId, string name)
            {
                try
                {
                    bool received = _gotFrame.Wait(TIMEOUT_MS);
                    if (!received)
                    {
                        YargLogger.LogWarning($"Channel probe: no frame from [{deviceId}] '{name}'");
                        return 0;
                    }

                    int usable = CountUsableChannels();
                    YargLogger.LogInfo($"Channel probe: [{deviceId}] '{name}' has {usable} usable channel(s)");
                    return usable;
                }
                finally
                {
                    Bass.RecordFree();
                    Dispose();
                }
            }

            private int CountUsableChannels()
            {
                int frameCount = _frame.Length / _reportedChannels;
                if (frameCount == 0)
                {
                    return 0;
                }

                short[][] deinterleaved = Deinterleave(_frame, _reportedChannels, frameCount);

                int usable = 0;
                for (int ch = 0; ch < _reportedChannels; ch++)
                {
                    if (IsSilent(deinterleaved[ch]))
                    {
                        break;
                    }

                    if (!IsDuplicate(deinterleaved, ch))
                    {
                        usable++;
                    }
                }

                return usable;
            }

            private bool Callback(int handle, IntPtr buffer, int length, IntPtr user)
            {
                if (length <= 0)
                {
                    return true;
                }

                unsafe
                {
                    var span = new Span<short>((short*) buffer, length / sizeof(short));
                    foreach (short s in span)
                    {
                        if (s == 0)
                        {
                            continue;
                        }

                        _frame = span.ToArray();
                        _gotFrame.Set();
                        break;
                    }
                }

                return true;
            }

            private static short[][] Deinterleave(short[] interleaved, int channels, int frameCount)
            {
                short[][] outBufs = new short[channels][];
                for (int ch = 0; ch < channels; ch++)
                {
                    short[] buf = new short[frameCount];
                    for (int i = 0; i < frameCount; i++)
                    {
                        buf[i] = interleaved[i * channels + ch];
                    }

                    outBufs[ch] = buf;
                }

                return outBufs;
            }

            private static bool IsSilent(short[] samples) => Array.TrueForAll(samples, s => s == 0);

            private static bool IsDuplicate(short[][] bufs, int channel)
            {
                for (int i = 0; i < channel; i++)
                {
                    if (bufs[channel].AsSpan().SequenceEqual(bufs[i]))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}