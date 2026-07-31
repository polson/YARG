#nullable enable
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Microphone adapter for an input owned by the active ASIO output backend.
    /// Monitoring stays entirely in the native ASIO graph; the shared worker only drains the
    /// analysis branch.
    /// </summary>
    internal sealed class BassAsioMicDevice : MicDevice
    {
        private readonly BassAsioInputLease _lease;
        private readonly BassMicAnalysisPipeline _analysisPipeline;

        internal static BassAsioMicDevice? Create(BassAudioManager manager, AsioInputDescriptor descriptor,
            string displayName)
        {
            var result = manager.TryAcquireAsioInput(descriptor.DriverId, descriptor.ChannelIndex, out var lease);
            if (result != AsioInputAcquireResult.Success || lease == null)
            {
                YargLogger.LogWarning($"Failed to acquire ASIO microphone '{displayName}': {result}");
                return null;
            }

            try
            {
                var device = new BassAsioMicDevice(lease, displayName);
                device.SetMonitoringLevel(SettingsManager.Settings.VocalMonitoring.Value);
                return device;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private BassAsioMicDevice(BassAsioInputLease lease, string displayName) : base(displayName)
        {
            _lease = lease;
            _analysisPipeline = new BassMicAnalysisPipeline(lease,
                () => IsRecordingOutput,
                () => InputManager.CurrentInputTime);
        }

        public override int Reset()
        {
            bool analysisReset = _analysisPipeline.Reset();
            bool monitorReset = _lease.ResetMonitorToLive();
            return analysisReset && monitorReset ? 0 : -1;
        }

        public override bool DequeueOutputFrame(out MicOutputFrame frame) =>
            _analysisPipeline.DequeueOutputFrame(out frame);

        public override void ClearOutputQueue() => _analysisPipeline.ClearOutputQueue();

        public override void SetMonitoringLevel(float volume)
        {
            if (!_lease.EnableMonitoring(volume))
            {
                YargLogger.LogWarning($"Failed to enable monitoring for ASIO microphone '{DisplayName}'");
            }
        }

        public override SerializedMic Serialize() => new(DisplayName);

        protected override void DisposeUnmanagedResources()
        {
            if (!_analysisPipeline.StopAndJoin())
            {
                // Backend invalidation will make the lease unreadable, but do not release it while
                // a native read could still be active.
                YargLogger.LogError($"Keeping ASIO microphone '{DisplayName}' lease alive after worker shutdown failure");
                return;
            }

            _lease.Dispose();
        }
    }
}
