using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassDrumSampleChannel : DrumSampleChannel
    {
#nullable enable
        internal static BassDrumSampleChannel? Create(DrumSfxSample sample, string path, int playbackCount,
            BassAudioOutput output, OutputChannel? outputChannel)
#nullable disable
        {
            int handle = Bass.SampleLoad(path, 0, 0, playbackCount, BassFlags.Default);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to load {0} {1}: {2}!", sample, path, Bass.LastError);
                return null;
            }

            return new BassDrumSampleChannel(handle, sample, path, playbackCount, output, outputChannel);
        }

        private readonly int _sfxHandle;
        private readonly BassSamplePlayer _samplePlayer;

#nullable enable
        private BassDrumSampleChannel(int handle, DrumSfxSample sample, string path, int playbackCount,
            BassAudioOutput output, OutputChannel? outputChannel)
            : base(sample, path, playbackCount)
#nullable disable
        {
            _sfxHandle = handle;
            _samplePlayer = new BassSamplePlayer(output, handle, playbackCount, sample.ToString());
            SetOutputChannel_Internal(outputChannel);
        }

        protected override void Play_Internal()
        {
            _samplePlayer.Play();
        }

        protected override void SetVolume_Internal(double volume)
        {
            _samplePlayer.SetVolume(volume);
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _samplePlayer.SetOutputChannel(channel);
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
