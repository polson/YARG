#nullable enable
using System;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassVenueSampleChannel : VenueSampleChannel
    {
        private readonly int _sampleHandle;
        private readonly BassSamplePlayer _samplePlayer;
        private bool _isPlaying;

        internal byte[] SampleData { get; }
        internal OutputChannel? OutputChannel { get; private set; }

        internal static BassVenueSampleChannel? Create(string name, byte[] sampleData, BassSfxMixer mixer,
            OutputChannel? outputChannel)
        {
            int handle = Bass.SampleLoad(sampleData, 0, sampleData.Length, 1, BassFlags.Default);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to load venue sample {0}: {1}", name, Bass.LastError);
                return null;
            }

            return new BassVenueSampleChannel(handle, name, sampleData, mixer, outputChannel);
        }

        private BassVenueSampleChannel(int handle, string name, byte[] sampleData, BassSfxMixer mixer,
            OutputChannel? outputChannel)
            : base(name, sampleData)
        {
            _sampleHandle = handle;
            SampleData = sampleData;
            _samplePlayer = new BassSamplePlayer(mixer, handle, 1, name, OnPlaybackEnded);
            SetOutputChannel_Internal(outputChannel);
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.VenueSample));
        }

        protected override void Play_Internal()
        {
            _samplePlayer.Stop();
            if (_samplePlayer.Play())
            {
                _isPlaying = true;
            }
        }

        protected override void Pause_Internal()
        {
            if (_isPlaying)
            {
                _samplePlayer.Pause();
            }
        }

        protected override void Resume_Internal()
        {
            if (_isPlaying)
            {
                _samplePlayer.Resume();
            }
        }

        protected override void Stop_Internal()
        {
            _samplePlayer.Stop();
            _isPlaying = false;
        }

        protected override void SetVolume_Internal(double volume)
        {
            _samplePlayer.SetVolume(volume);
        }

        protected override void SetEndCallback_Internal()
        {
        }

        protected override void SetOutputChannel_Internal(OutputChannel? channel)
        {
            OutputChannel = channel;
            _samplePlayer.SetOutputChannel(channel);
        }

        protected override void EndCallback_Internal(int _, int __, int ___, IntPtr ____)
        {
        }

        protected override bool IsPlaying_Internal()
        {
            return _isPlaying && _samplePlayer.IsPlaying;
        }

        protected override bool IsPaused_Internal()
        {
            return _isPlaying && _samplePlayer.IsPaused;
        }

        private void OnPlaybackEnded()
        {
            _isPlaying = false;
        }

        protected override void DisposeManagedResources()
        {
            _samplePlayer.Dispose();
        }

        protected override void DisposeUnmanagedResources()
        {
            Bass.SampleFree(_sampleHandle);
        }
    }
}
