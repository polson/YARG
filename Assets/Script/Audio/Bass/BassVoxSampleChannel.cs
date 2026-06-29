#nullable enable
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Settings;

namespace YARG.Audio.BASS
{
    public sealed class BassVoxSampleChannel : VoxSampleChannel
    {
        private static          BassVoxSampleChannel?       _currentlyPlaying;
        private static readonly Queue<BassVoxSampleChannel> Queue    = new();
        private static          bool                        _queueActive;

        private readonly int              _sampleHandle;
        private readonly BassSamplePlayer _samplePlayer;
        private          double           _volumeSetting = 1;
        private          bool             _disposed;

#nullable enable
        public static BassVoxSampleChannel? Create(VoxSample sample, string path, BassAudioManager manager, OutputChannel? outputChannel)
#nullable disable
        {
            int handle = BassHelpers.LoadSample(path, 2, sample.ToString());
            if (handle == 0)
            {
                return null;
            }

            return new BassVoxSampleChannel(handle, sample, path, manager, outputChannel);
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
                if (channel._disposed)
                {
                    continue;
                }

                await UniTask.WaitUntil(() => !IsAnyPlaying());
                if (!channel._disposed)
                {
                    channel.Play();
                }
            }
            _queueActive = false;
        }

        private static bool IsAnyPlaying()
        {
            var current = _currentlyPlaying;
            return current != null && current.IsPlaying();
        }

#nullable enable
        private BassVoxSampleChannel(int handle, VoxSample sample, string path, BassAudioManager manager, OutputChannel? outputChannel)
            : base(sample, path)
#nullable disable
        {
            _sampleHandle = handle;
            _samplePlayer = new BassSamplePlayer(manager, 1);
            SetOutputChannel_Internal(outputChannel);
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.VoxSample));
        }

        protected override void Play_Internal()
        {
            if (!SettingsManager.Settings.EnableVoxSamples.Value)
            {
                return;
            }

            if (IsAnyPlaying())
            {
                QueuePlayback(this);
                return;
            }

            _currentlyPlaying = this;
            _samplePlayer.PlaySample(_sampleHandle, Sample.ToString(), GetScaledVolume());
        }

        protected override void SetVolume_Internal(double volume)
        {
            _volumeSetting = volume;
            _samplePlayer.SetVolume(GetScaledVolume());
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _samplePlayer.OutputChannel = channel;
        }

        protected override bool IsPlaying_Internal()
        {
            return _samplePlayer.IsPlaying;
        }

        private double GetScaledVolume()
        {
            return _volumeSetting * AudioHelpers.VoxSamples[(int) Sample].Volume;
        }

        protected override void DisposeManagedResources()
        {
            _samplePlayer.Dispose();
        }

        protected override void DisposeUnmanagedResources()
        {
            _disposed = true;
            Bass.SampleFree(_sampleHandle);
        }
    }
}
