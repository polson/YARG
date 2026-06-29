using System;
using System.Collections.Generic;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;

namespace YARG.Audio.BASS
{
    public sealed class BassSampleChannel : SampleChannel
    {
#nullable enable
        public static BassSampleChannel? Create(SfxSample sample, string path, int playbackCount,
            BassAudioManager manager, OutputChannel? outputChannel, bool loop = false)
#nullable disable
        {
            int handle = BassHelpers.LoadSample(path, playbackCount, sample.ToString());
            if (handle == 0)
            {
                return null;
            }

            return new BassSampleChannel(handle, sample, path, playbackCount, manager, outputChannel, loop);
        }

        private readonly int              _sfxHandle;
        private readonly bool             _canLoop;
        private readonly BassSamplePlayer _samplePlayer;

        private double _lastPlaybackTime;
        private double _volumeSetting = 1;

#nullable enable
        private BassSampleChannel(int handle, SfxSample sample, string path, int playbackCount,
            BassAudioManager manager, OutputChannel? outputChannel, bool canLoop)
            : base(sample, path, playbackCount)
#nullable disable
        {
            _sfxHandle = handle;
            _canLoop = canLoop;
            _lastPlaybackTime = -1;
            _samplePlayer = new BassSamplePlayer(manager, playbackCount);
            SetOutputChannel_Internal(outputChannel);
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.Sfx));
        }

        protected override void Play_Internal(double duration)
        {
            if (InputManager.CurrentInputTime - _lastPlaybackTime < PLAYBACK_SUPPRESS_THRESHOLD)
            {
                return;
            }

            var sfxVolume = AudioHelpers.SfxSamples[(int) Sample].Volume * (float) _volumeSetting;
            double initialVolume = duration > 0 ? 0 : sfxVolume;

            int channel = _samplePlayer.PlaySample(_sfxHandle, Sample.ToString(), initialVolume, _canLoop);
            if (channel == 0)
            {
                return;
            }

            if (duration > 0)
            {
                var time = (int) Math.Round(duration * 1000);
                if (Bass.ChannelSlideAttribute(channel, ChannelAttribute.Volume, sfxVolume, time))
                {
                    if (!Bass.ChannelIsSliding(channel, ChannelAttribute.Volume))
                    {
                        YargLogger.LogFormatError("Failed to set volume slide for {0} even though duration is set!", Sample);
                    }
                }
                else
                {
                    YargLogger.LogFormatError("Failed to set volume slide for {0}: {1}!", Sample, Bass.LastError);
                }
            }

            _lastPlaybackTime = InputManager.CurrentInputTime;
        }

        protected override void Stop_Internal(double duration)
        {
            var time = duration > 0 ? (int) Math.Round(duration * 1000) : 0;
            _samplePlayer.Stop(time);
        }

        protected override void Pause_Internal()
        {
            _samplePlayer.Pause();
        }

        protected override void Resume_Internal()
        {
            _samplePlayer.Resume();
        }

        protected override void SetVolume_Internal(double volume)
        {
            _volumeSetting = volume;
            volume *= AudioHelpers.SfxSamples[(int) Sample].Volume;
            _samplePlayer.SetVolume(volume);
        }

        protected override void SetEndCallback_Internal()
        {
            // Dynamic per-play sources set their own end syncs when added to the master mixer.
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _samplePlayer.OutputChannel = channel;
        }

        protected override void EndCallback_Internal(int _, int __, int ___, IntPtr ____)
        {
        }

        protected override bool IsPlaying_Internal => _samplePlayer.IsPlaying;

        protected override void DisposeUnmanagedResources()
        {
            _samplePlayer.Dispose();
            Bass.SampleFree(_sfxHandle);
        }
    }
}
