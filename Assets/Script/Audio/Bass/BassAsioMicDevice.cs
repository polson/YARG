using System;
using System.Diagnostics;
using ManagedBass.Asio;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;

namespace YARG.Audio.BASS
{
    public sealed class BassAsioMicDevice : MicDevice
    {
        private readonly int _deviceId;
        private readonly AsioProcedure _asioProcedure;
        private MicDataProcessor _processor;

        private float _sampleRate;
        private bool _isInitialized;

        private double _syncTime;
        private long _syncTimestamp;

        internal static BassAsioMicDevice Create(int deviceId, string name)
        {
            var device = new BassAsioMicDevice(deviceId, name);
            if (!device.Initialize())
            {
                device.Dispose();
                return null;
            }
            return device;
        }

        private BassAsioMicDevice(int deviceId, string name) : base(name)
        {
            _deviceId = deviceId;
            _asioProcedure = AsioCallback;
        }

        private bool Initialize()
        {
            if (!BassAsio.Init(_deviceId, AsioInitFlags.None))
            {
                if (BassAsio.LastError != ManagedBass.Errors.Already)
                {
                    YargLogger.LogFormatError("Failed to initialize ASIO device: {0}!", BassAsio.LastError);
                    return false;
                }
            }

            // By default use ASIO format 32-bit float
            BassAsio.ChannelEnable(true, 0, _asioProcedure, IntPtr.Zero);
            BassAsio.ChannelSetFormat(true, 0, AsioSampleFormat.Float);

            // Get sample rate
            _sampleRate = (float)BassAsio.ChannelGetRate(true, 0);
            if (_sampleRate <= 0)
            {
                _sampleRate = (float)BassAsio.Rate;
            }

            _processor = new MicDataProcessor(_sampleRate);

            SyncTime();

            if (!BassAsio.Start(0, 1))
            {
                YargLogger.LogFormatError("Failed to start ASIO device: {0}!", BassAsio.LastError);
                return false;
            }

            _isInitialized = true;
            return true;
        }

        private void SyncTime()
        {
            _syncTime = InputManager.CurrentInputTime;
            _syncTimestamp = Stopwatch.GetTimestamp();
        }

        public override int Reset()
        {
            SyncTime();
            _processor?.Clear();
            return 0;
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame)
        {
            // Occasionally sync time on the main thread to prevent drift
            SyncTime();

            if (_processor != null)
            {
                return _processor.DequeueFrame(out frame);
            }
            frame = default;
            return false;
        }

        public override void ClearOutputQueue()
        {
            _processor?.Clear();
        }

        public override void SetMonitoringLevel(float volume)
        {
            // Not supported currently for ASIO. Monitor playback requires output handling.
        }

        public override SerializedMic Serialize()
        {
            return new SerializedMic(DisplayName);
        }

        private int AsioCallback(bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (!IsRecordingOutput || !input)
            {
                return length;
            }

            double elapsed = (Stopwatch.GetTimestamp() - _syncTimestamp) / (double)Stopwatch.Frequency;
            double currentTime = _syncTime + elapsed;

            int sampleCount = length / sizeof(float);
            unsafe
            {
                var floatSpan = new ReadOnlySpan<float>(buffer.ToPointer(), sampleCount);
                _processor.Process(floatSpan, currentTime);
            }

            return length;
        }

        protected override void DisposeUnmanagedResources()
        {
            if (_isInitialized)
            {
                BassAsio.Stop();
                BassAsio.Free();
            }
        }
    }
}
