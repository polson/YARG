using ManagedBass;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Input;

namespace YARG.Audio.BASS
{
    public sealed class BassMetronomeSampleChannel : MetronomeSampleChannel
    {
#nullable enable
        internal static BassMetronomeSampleChannel? Create(MetronomeSample sample, string hiPath, string loPath,
             BassAudioOutput output, OutputChannel? outputChannel)
#nullable disable
        {
            int hiHandle = Bass.SampleLoad(hiPath, 0, 0, 1, BassFlags.Decode);
            if (hiHandle == 0)
            {
                YargLogger.LogFormatError("Failed to load {0} hi {1}: {2}!", sample, hiPath, Bass.LastError);
                return null;
            }

            int loHandle = Bass.SampleLoad(loPath, 0, 0, 1, BassFlags.Decode);
            if (loHandle == 0)
            {
                Bass.SampleFree(hiHandle);
                YargLogger.LogFormatError("Failed to load {0} lo {1}: {2}!", sample, loPath, Bass.LastError);
                return null;
            }

            return new BassMetronomeSampleChannel(sample, hiHandle, hiPath, loHandle, loPath, output,
                outputChannel);
        }

        private readonly int _hiHandle;
        private readonly int _loHandle;
        private readonly BassSamplePlayer _hiPlayer;
        private readonly BassSamplePlayer _loPlayer;

#nullable enable
        private BassMetronomeSampleChannel(MetronomeSample sample, int hiHandle, string hiPath,
            int loHandle, string loPath, BassAudioOutput output, OutputChannel? outputChannel)
            : base(sample, hiPath, loPath)
#nullable disable
        {
            _hiHandle = hiHandle;
            _loHandle = loHandle;
            _hiPlayer = new BassSamplePlayer(output, hiHandle, 1, $"{sample} hi");
            _loPlayer = new BassSamplePlayer(output, loHandle, 1, $"{sample} lo");
            SetOutputChannel_Internal(outputChannel);
            SetVolume_Internal(GlobalAudioHandler.GetTrueVolume(SongStem.Metronome));
        }

        protected override void PlayHi_Internal()
        {
            if (!_hiPlayer.Play())
            {
                YargLogger.LogFormatError("Failed to play {0} hi channel: {1}!", Sample, Bass.LastError);
            }
        }

        protected override void PlayLo_Internal()
        {
            if (!_loPlayer.Play())
            {
                YargLogger.LogFormatError("Failed to play {0} lo channel: {1}!", Sample, Bass.LastError);
            }
        }

        protected override int CreateStream_Internal(MetronomePitch pitch)
        {
            // Use an independent file-backed stream rather than deriving one from the playback
            // sample. This keeps the one-shot decoder independent of sample channel state.
            string path = pitch == MetronomePitch.Hi ? _hiPath : _loPath;
            int stream = Bass.CreateStream(path, 0, 0, BassFlags.Float | BassFlags.Decode);
            if (stream == 0)
            {
                YargLogger.LogFormatError("Failed to create {0} {1} stream: {2}!", Sample, pitch,
                    Bass.LastError);
                return 0;
            }

            return stream;
        }

        protected override void SetVolume_Internal(double volume)
        {
            volume *= AudioHelpers.MetronomeSamples[(int) Sample].Volume;

            _hiPlayer.SetVolume(volume);
            _loPlayer.SetVolume(volume);
        }

#nullable enable
        protected override void SetOutputChannel_Internal(OutputChannel? channel)
#nullable disable
        {
            _hiPlayer.SetOutputChannel(channel);
            _loPlayer.SetOutputChannel(channel);
        }

        protected override void DisposeManagedResources()
        {
            _hiPlayer.Dispose();
            _loPlayer.Dispose();
        }

        protected override void DisposeUnmanagedResources()
        {
            Bass.SampleFree(_hiHandle);
            Bass.SampleFree(_loHandle);
        }
    }
}
