using System;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Settings;

namespace YARG.Audio.BASS
{
#nullable enable
    public abstract class BassTempoStream : IDisposable
    {
        protected readonly BassAudioManager BassManager;
        
        public int Handle { get; protected set; }
        public abstract bool IsDecodeStream { get; }
        public abstract bool IsPlaying { get; }
        public abstract int PlaybackDataHandle { get; }

        protected BassTempoStream(BassAudioManager bassManager, int mixerHandle, BassFlags flags)
        {
            BassManager = bassManager;
            Handle = BassFx.TempoCreate(mixerHandle, flags | BassFlags.SampleOverrideLowestVolume);
            if (Handle == 0)
            {
                YargLogger.LogFormatError("Failed to create tempo stream: {0}", Bass.LastError);
            }
        }

        public abstract void SetVolume(double logicalVolume);
        public abstract void FadeIn(double maxVolume, double duration);
        public abstract void FadeOut(double duration);
        public abstract bool Play(bool didSetPosition);
        public abstract bool Pause();
        
        public virtual void AddToMixer(OutputChannel? channel, bool paused) { }
        public virtual void RemoveFromMixer() { }

        public abstract void SetOutputChannel(OutputChannel? channel, bool wasPlaying);
        public abstract void SetDevice(int deviceId);
        public virtual void SetBufferLength(int lengthMs) { }
        
        public abstract double GetPosition(double positionOffset, Func<double> decodingPositionFallback);
        public abstract double GetAudibleSyncLatency();

        public void FlushBuffer()
        {
            if (Handle != 0)
            {
                if (!BassMix.ChannelSetPosition(Handle, 0, PositionFlags.Bytes))
                {
                    Bass.ChannelSetPosition(Handle, 0);
                }
            }
        }

        public void SetSpeed(float speed, bool shiftPitch)
        {
            if (Handle != 0)
            {
                BassAudioManager.SetSpeed(speed, Handle, shiftPitch);
            }
        }

        public virtual void Dispose()
        {
            if (Handle != 0)
            {
                if (!Bass.StreamFree(Handle))
                {
                    YargLogger.LogFormatError("Failed to free tempo stream: {0}!", Bass.LastError);
                }
                Handle = 0;
            }
        }
    }

    public sealed class DecodeBassTempoStream : BassTempoStream
    {
        private OutputChannel? _outputChannel;
        private bool _addedToPlaybackMixer;
        private int _positionFallbackCount;
        private bool _isPlaying;

        public override bool IsDecodeStream => true;
        public override bool IsPlaying => _isPlaying;
        public override int PlaybackDataHandle => BassManager.GetGlobalMusicPlaybackMixerHandle();

        public DecodeBassTempoStream(BassAudioManager bassManager, int mixerHandle)
            : base(bassManager, mixerHandle, BassFlags.Decode)
        {
        }

        public override void SetVolume(double logicalVolume)
        {
            double scaledVolume = BassAudioManager.ExponentialVolume(logicalVolume);
            if (!Bass.ChannelSetAttribute(Handle, ChannelAttribute.Volume, scaledVolume))
            {
                YargLogger.LogFormatError("Failed to set tempo stream volume: {0}", Bass.LastError);
            }
        }

        public override void FadeIn(double maxVolume, double duration)
        {
            double scaledVolume = BassAudioManager.ExponentialVolume(maxVolume);
            Bass.ChannelSlideAttribute(Handle, ChannelAttribute.Volume, (float) scaledVolume, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public override void FadeOut(double duration)
        {
            Bass.ChannelSlideAttribute(Handle, ChannelAttribute.Volume, 0f, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public override bool Play(bool didSetPosition)
        {
            if (!AddToPlaybackMixer(paused: true))
            {
                return false;
            }

            if ((int) BassMix.ChannelFlags(Handle, BassFlags.Default, BassFlags.MixerChanPause) == -1)
            {
                YargLogger.LogFormatError("Failed to resume tempo stream: {0}", Bass.LastError);
                return false;
            }

            _isPlaying = true;
            return true;
        }

        public override bool Pause()
        {
            if (!_addedToPlaybackMixer)
            {
                return true;
            }

            if ((int) BassMix.ChannelFlags(Handle, BassFlags.MixerChanPause, BassFlags.MixerChanPause) == -1)
            {
                YargLogger.LogFormatError("Failed to pause tempo stream: {0}", Bass.LastError);
                return false;
            }

            _isPlaying = false;
            return true;
        }

        public override void AddToMixer(OutputChannel? channel, bool paused)
        {
            AddToPlaybackMixer(paused);
        }

        public override void RemoveFromMixer()
        {
            RemoveFromPlaybackMixer();
        }

        public override void SetOutputChannel(OutputChannel? channel, bool wasPlaying)
        {
            _outputChannel = channel;
            RemoveFromPlaybackMixer();
            AddToPlaybackMixer(paused: !wasPlaying);
        }

        public override void SetDevice(int deviceId)
        {
            if (Handle != 0 && !Bass.ChannelSetDevice(Handle, deviceId))
            {
                YargLogger.LogFormatError("Failed to change device for tempo stream handle: {0}", Bass.LastError);
            }
        }

        public override double GetPosition(double positionOffset, Func<double> decodingPositionFallback)
        {
            long playedBytes = BassMix.ChannelGetPosition(Handle, PositionFlags.Bytes);
            if (playedBytes < 0)
            {
                _positionFallbackCount++;
                if (_positionFallbackCount == 1 || _positionFallbackCount % 1000 == 0)
                {
                    YargLogger.LogFormatWarning(
                        "Failed to get tempo stream playback position. " +
                        "Falling back to decoding position. Count: {0}, played bytes: {1}, error: {2}",
                        _positionFallbackCount, playedBytes, Bass.LastError);
                }
                return decodingPositionFallback();
            }

            double seconds = Bass.ChannelBytes2Seconds(Handle, playedBytes);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to convert played bytes to seconds: {0}!", Bass.LastError);
                return decodingPositionFallback();
            }

            return seconds + positionOffset;
        }

        public override double GetAudibleSyncLatency()
        {
            return GetConfiguredOutputLatency() + GetDeviceOutputLatency();
        }

        internal static double GetDeviceOutputLatency()
        {
            return Math.Max(0, GlobalAudioHandler.PlaybackLatency) / 1000.0;
        }

        internal static double GetConfiguredOutputLatency()
        {
            int bufferLength = SettingsManager.Settings?.PlaybackBufferLength.Value ?? 0;
            int minimumLength = GlobalAudioHandler.MinimumBufferLength;
            if (bufferLength > 0 && minimumLength > 0 && bufferLength < minimumLength)
            {
                bufferLength = minimumLength;
            }

            return Math.Max(0, bufferLength) / 1000.0;
        }

        private bool AddToPlaybackMixer(bool paused)
        {
            if (_addedToPlaybackMixer)
            {
                return true;
            }

            var pausedFlag = paused ? BassFlags.MixerChanPause : BassFlags.Default;
            var flags = BassFlags.MixerChanBuffer | pausedFlag;
            bool added = BassManager.AddToGlobalMusicPlaybackMixer(Handle, _outputChannel, flags);
            _addedToPlaybackMixer = added;
            return added;
        }

        private void RemoveFromPlaybackMixer()
        {
            if (!_addedToPlaybackMixer)
            {
                return;
            }

            BassManager.RemoveFromPlaybackMixer(Handle);
            _addedToPlaybackMixer = false;
        }

        public override void Dispose()
        {
            RemoveFromPlaybackMixer();
            base.Dispose();
        }
    }

    public sealed class DirectBassTempoStream : BassTempoStream
    {
        private double _logicalVolume = 1.0;
        private int _positionFallbackCount;

        public override bool IsDecodeStream => false;
        public override bool IsPlaying => Bass.ChannelIsActive(Handle) == PlaybackState.Playing;
        public override int PlaybackDataHandle => Handle;

        public DirectBassTempoStream(BassAudioManager bassManager, int mixerHandle)
            : base(bassManager, mixerHandle, BassFlags.Default)
        {
            BassManager.MasterVolumeChanged += OnMasterVolumeChanged;
        }

        public override void SetVolume(double logicalVolume)
        {
            _logicalVolume = logicalVolume;
            UpdateVolume();
        }

        private void UpdateVolume()
        {
            double scaledVolume = BassAudioManager.ExponentialVolume(_logicalVolume) * BassManager.MasterVolume;
            if (!Bass.ChannelSetAttribute(Handle, ChannelAttribute.Volume, scaledVolume))
            {
                YargLogger.LogFormatError("Failed to set tempo stream volume: {0}", Bass.LastError);
            }
        }

        private void OnMasterVolumeChanged(double volume)
        {
            UpdateVolume();
        }

        public override void FadeIn(double maxVolume, double duration)
        {
            _logicalVolume = maxVolume;
            double scaledVolume = BassAudioManager.ExponentialVolume(maxVolume) * BassManager.MasterVolume;
            Bass.ChannelSlideAttribute(Handle, ChannelAttribute.Volume, (float) scaledVolume, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public override void FadeOut(double duration)
        {
            _logicalVolume = 0;
            Bass.ChannelSlideAttribute(Handle, ChannelAttribute.Volume, 0f, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public override bool Play(bool didSetPosition)
        {
            if (!Bass.ChannelPlay(Handle, didSetPosition))
            {
                YargLogger.LogFormatError("Failed to play tempo stream: {0}", Bass.LastError);
                return false;
            }
            return true;
        }

        public override bool Pause()
        {
            if (!Bass.ChannelPause(Handle))
            {
                YargLogger.LogFormatError("Failed to pause tempo stream: {0}", Bass.LastError);
                return false;
            }
            return true;
        }

        public override void SetOutputChannel(OutputChannel? channel, bool wasPlaying)
        {
            BassHelpers.UpdateOutputChannels(Handle, channel);
        }

        public override void SetDevice(int deviceId)
        {
            if (Handle != 0 && !Bass.ChannelSetDevice(Handle, deviceId))
            {
                YargLogger.LogFormatError("Failed to change device for tempo stream handle: {0}", Bass.LastError);
            }
        }

        public override void SetBufferLength(int lengthMs)
        {
            if (lengthMs > 0)
            {
                lengthMs = Math.Max(lengthMs, GlobalAudioHandler.MinimumBufferLength);
            }

            float lengthInSeconds = lengthMs / 1000f;
            if (!Bass.ChannelSetAttribute(Handle, ChannelAttribute.Buffer, lengthInSeconds))
            {
                YargLogger.LogFormatError("Failed to set tempo stream buffer: {0}!", Bass.LastError);
            }
        }

        public override double GetPosition(double positionOffset, Func<double> decodingPositionFallback)
        {
            long playedBytes = Bass.ChannelGetPosition(Handle, PositionFlags.Bytes);
            if (playedBytes < 0)
            {
                _positionFallbackCount++;
                if (_positionFallbackCount == 1 || _positionFallbackCount % 1000 == 0)
                {
                    YargLogger.LogFormatWarning(
                        "Failed to get tempo stream playback position. " +
                        "Falling back to decoding position. Count: {0}, played bytes: {1}, error: {2}",
                        _positionFallbackCount, playedBytes, Bass.LastError);
                }
                return decodingPositionFallback();
            }

            double seconds = Bass.ChannelBytes2Seconds(Handle, playedBytes);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to convert played bytes to seconds: {0}!", Bass.LastError);
                return decodingPositionFallback();
            }

            return seconds + positionOffset;
        }

        public override double GetAudibleSyncLatency()
        {
            return 0;
        }

        public override void Dispose()
        {
            BassManager.MasterVolumeChanged -= OnMasterVolumeChanged;
            base.Dispose();
        }
    }
#nullable disable
}
