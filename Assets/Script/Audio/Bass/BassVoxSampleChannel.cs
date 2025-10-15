#nullable enable
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// A sample channel that uses BASS to play VOX files.
    ///
    /// Unlike all the others, this one will automatically queue samples and play them sequentially.
    /// </summary>
    public sealed class BassVoxSampleChannel : VoxSampleChannel
    {
        private static readonly List<BassVoxSampleChannel>  Channels = new();
        private static readonly Queue<BassVoxSampleChannel> Queue    = new();
        private readonly        int                         _sampleHandle;
        private static          bool                        _queueActive;

        public static BassVoxSampleChannel? Create(VoxSample sample, string path)
        {
            int handle = Bass.SampleLoad(path, 0, 0, 2, BassFlags.Decode);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to load {0} {1}: {2}!", sample, path, Bass.LastError);
                return null;
            }

            int channel = Bass.SampleGetChannel(handle);
            if (channel == 0)
            {
                Bass.SampleFree(handle);
                YargLogger.LogFormatError("Failed to create {0} channel: {1}!", sample, Bass.LastError);
                return null;
            }

            return new BassVoxSampleChannel(handle, channel, sample);
        }

        private static void QueuePlayback(BassVoxSampleChannel channel)
        {
            Queue.Enqueue(channel);
            if (!_queueActive)
            {
                PlayQueued();
            }
        }

        private static async void PlayQueued()
        {
            _queueActive = true;
            while (Queue.TryDequeue(out var channel))
            {
                await UniTask.WaitUntil(() => !IsAnyPlaying());
                channel.Play();
            }
            _queueActive = false;
        }

        private static bool IsAnyPlaying()
        {
            foreach (var channel in Channels)
            {
                if (channel.IsPlaying())
                {
                    return true;
                }
            }

            return false;
        }

        private readonly int _channel;

        private BassVoxSampleChannel(int handle, int channel, VoxSample sample)
            : base(sample)
        {
            _sampleHandle = handle;
            _channel = channel;
            Channels.Add(this);
            SettingsManager.Settings.SfxVolume.OnChange += OnVolumeChanged;
            SettingsManager.Settings.EnableVoxSamples.OnChange += OnVoxEnabledChanged;
        }

        private void OnVolumeChanged(float volume)
        {
            SetVolume_Internal(volume);
        }

        private void OnVoxEnabledChanged(bool enabled)
        {
            // This allows real-time updates when the setting changes
            // though the main check still happens in Play_Internal
        }

        protected override void Play_Internal()
        {
            // Don't particularly like doing it here, but this is the only place in the playback chain where we can
            // check for the vox enabled setting
            if (!SettingsManager.Settings.EnableVoxSamples.Value)
            {
                return;
            }

            if (IsAnyPlaying())
            {
                QueuePlayback(this);
                return;
            }

            if (!Bass.ChannelPlay(_channel, true))
            {
                YargLogger.LogFormatError("Failed to play {0} channel: {1}!", Sample, Bass.LastError);
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            volume *= AudioHelpers.SfxSamples[(int) Sample].Volume;
            if (!Bass.ChannelSetAttribute(_channel, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set {0} volume: {1}!", Sample, Bass.LastError);
            }
        }

        protected override bool IsPlaying_Internal()
        {
            return Bass.ChannelIsActive(_channel) == PlaybackState.Playing;
        }

        protected override void DisposeUnmanagedResources()
        {
            Bass.SampleFree(_sampleHandle);
        }

        protected override void DisposeManagedResources()
        {
            SettingsManager.Settings.EnableVoxSamples.OnChange -= OnVoxEnabledChanged;
            base.DisposeManagedResources();
        }
    }
}