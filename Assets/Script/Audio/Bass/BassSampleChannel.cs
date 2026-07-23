using System;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;

namespace YARG.Audio.BASS
{
    public sealed class BassSampleChannel : SampleChannel
    {
#nullable enable
        internal static BassSampleChannel? Create(SfxSample sample, string path, int playbackCount,
            BassAudioOutput output, OutputChannel? outputChannel, bool loop = false)
#nullable disable
        {
            int handle = Bass.SampleLoad(path, 0, 0, playbackCount, BassFlags.Default);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to load {0} {1}: {2}!", sample, path, Bass.LastError);
                return null;
            }

            return new BassSampleChannel(handle, sample, path, playbackCount, output, outputChannel, loop);
        }

        private readonly int _sfxHandle;
        private readonly bool _canLoop;
        private readonly BassSamplePlayer _samplePlayer;

        private double _lastPlaybackTime = -1;
        private double _volumeSetting = 1;

#nullable enable
        private BassSampleChannel(int handle, SfxSample sample, string path, int playbackCount,
            BassAudioOutput output, OutputChannel? outputChannel, bool canLoop)
            : base(sample, path, playbackCount)
#nullable disable
        {
            _sfxHandle = handle;
            _canLoop = canLoop;
            _samplePlayer = new BassSamplePlayer(output, handle, playbackCount, sample.ToString(), OnPlaybackEnded);
            SetOutputChannel_Internal(outputChannel);
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.Sfx));
        }

        protected override void Play_Internal(double duration)
        {
            if (InputManager.CurrentInputTime - _lastPlaybackTime < PLAYBACK_SUPPRESS_THRESHOLD)
            {
                return;
            }

            int fadeInMilliseconds = duration > 0 ? (int) Math.Round(duration * 1000) : 0;
            if (!_samplePlayer.Play(_canLoop, fadeInMilliseconds))
            {
                return;
            }

            AudioHelpers.SfxSamples[(int) Sample].IsPlaying = true;
            _lastPlaybackTime = InputManager.CurrentInputTime;
        }

        protected override int CreateStream_Internal()
        {
            int stream = Bass.CreateStream(_path, 0, 0, BassFlags.Float | BassFlags.Decode);
            if (stream == 0)
            {
                YargLogger.LogFormatError("Failed to create {0} decode stream: {1}!", Sample, Bass.LastError);
            }
            return stream;
        }

        protected override void Stop_Internal(double duration)
        {
            int fadeOutMilliseconds = duration > 0 ? (int) Math.Round(duration * 1000) : 0;
            _samplePlayer.Stop(fadeOutMilliseconds);
            AudioHelpers.SfxSamples[(int) Sample].IsPlaying = false;
        }

        protected override void Pause_Internal()
        {
            _samplePlayer.Pause();
        }

        protected override void Resume_Internal()
        {
            if (AudioHelpers.SfxSamples[(int) Sample].IsPlaying)
            {
                _samplePlayer.Resume();
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            _volumeSetting = volume;
            _samplePlayer.SetVolume(volume * AudioHelpers.SfxSamples[(int) Sample].Volume);
        }

        protected override void SetEndCallback_Internal()
        {
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _samplePlayer.SetOutputChannel(channel);
        }

        protected override void EndCallback_Internal(int _, int __, int ___, IntPtr ____)
        {
        }

        private void OnPlaybackEnded()
        {
            AudioHelpers.SfxSamples[(int) Sample].IsPlaying = false;
        }

        protected override void DisposeManagedResources()
        {
            _samplePlayer.Dispose();
        }

        protected override void DisposeUnmanagedResources()
        {
            Bass.SampleFree(_sfxHandle);
        }
    }
}
