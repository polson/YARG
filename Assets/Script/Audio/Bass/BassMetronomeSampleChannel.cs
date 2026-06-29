using System.Collections.Generic;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public sealed class BassMetronomeSampleChannel : MetronomeSampleChannel
    {
#nullable enable
        public static BassMetronomeSampleChannel? Create(MetronomeSample sample, string hiPath, string loPath,
            BassAudioManager manager, OutputChannel? outputChannel)
#nullable disable
        {
            int hiHandle = BassHelpers.LoadSample(hiPath, 2, $"{sample} hi");
            if (hiHandle == 0)
            {
                return null;
            }

            int loHandle = BassHelpers.LoadSample(loPath, 2, $"{sample} lo");
            if (loHandle == 0)
            {
                Bass.SampleFree(hiHandle);
                return null;
            }

            return new BassMetronomeSampleChannel(sample, hiHandle, hiPath, loHandle, loPath, manager, outputChannel);
        }

        private readonly int              _hiHandle;
        private readonly int              _loHandle;
        private readonly BassSamplePlayer _samplePlayer;
        private          double           _volumeSetting = 1;

#nullable enable
        private BassMetronomeSampleChannel(MetronomeSample sample, int hiHandle, string hiPath, int loHandle, string loPath,
            BassAudioManager manager, OutputChannel? outputChannel)
            : base(sample, hiPath, loPath)
#nullable disable
        {
            _hiHandle = hiHandle;
            _loHandle = loHandle;
            _samplePlayer = new BassSamplePlayer(manager, 2);
            SetOutputChannel_Internal(outputChannel);
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.Metronome));
        }

        protected override void PlayHi_Internal()
        {
            _samplePlayer.PlaySample(_hiHandle, $"{Sample} hi", GetScaledVolume());
        }

        protected override void PlayLo_Internal()
        {
            _samplePlayer.PlaySample(_loHandle, $"{Sample} lo", GetScaledVolume());
        }

        protected override void SetVolume_Internal(double volume)
        {
            _volumeSetting = volume;
            double scaled = GetScaledVolume();
            _samplePlayer.SetVolume(scaled);
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _samplePlayer.OutputChannel = channel;
        }

        private double GetScaledVolume()
        {
            return _volumeSetting * AudioHelpers.MetronomeSamples[(int) Sample].Volume;
        }

        protected override void DisposeUnmanagedResources()
        {
            _samplePlayer.Dispose();
            Bass.SampleFree(_hiHandle);
            Bass.SampleFree(_loHandle);
        }
    }
}
