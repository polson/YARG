using System.Collections.Generic;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassDrumSampleChannel : DrumSampleChannel
    {
#nullable enable
        public static BassDrumSampleChannel? Create(DrumSfxSample sample, string path, int playbackCount,
            BassAudioManager manager, OutputChannel? outputChannel)
#nullable disable
        {
            int handle = BassHelpers.LoadSample(path, playbackCount, sample.ToString());
            if (handle == 0)
            {
                return null;
            }

            return new BassDrumSampleChannel(handle, sample, path, playbackCount, manager, outputChannel);
        }

        private readonly int              _sfxHandle;
        private readonly BassSamplePlayer _samplePlayer;
        private double                    _currentVolume = 1;

#nullable enable
        private BassDrumSampleChannel(int handle, DrumSfxSample sample, string path, int playbackCount,
            BassAudioManager manager, OutputChannel? outputChannel)
            : base(sample, path, playbackCount)
#nullable disable
        {
            _sfxHandle = handle;
            _samplePlayer = new BassSamplePlayer(manager, playbackCount);
            SetOutputChannel_Internal(outputChannel);
        }

        protected override void Play_Internal()
        {
            _samplePlayer.PlaySample(_sfxHandle, Sample.ToString(), _currentVolume);
        }

        protected override void SetVolume_Internal(double volume)
        {
            _currentVolume = volume;
            _samplePlayer.SetVolume(volume);
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _samplePlayer.OutputChannel = channel;
        }

        protected override void DisposeUnmanagedResources()
        {
            _samplePlayer.Dispose();
            Bass.SampleFree(_sfxHandle);
        }
    }
}
