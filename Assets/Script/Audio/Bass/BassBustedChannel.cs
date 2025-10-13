using System;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Song;
using Random = UnityEngine.Random;

namespace YARG.Audio.BASS
{
    public class BassBustedChannel
    {
        private StreamHandle _bustedHandle;
        private int _dspHandle;
        PitchShiftParametersStruct _bustedPitchParams = new(
                Mathf.Pow(2, 1 / 12f), // Default to +1 semitone
            0,
            GlobalAudioHandler.WHAMMY_FFT_DEFAULT,
            GlobalAudioHandler.WHAMMY_OVERSAMPLE_DEFAULT
            );

        private int      _lastPitchShift;
        private SongStem _stem;

        public BassBustedChannel(SongStem stem, int[] indices, int sourceHandle, int streamHandle)
        {
            _stem = stem;
            if (AudioHelpers.PitchBendAllowedStems.Contains(stem))
            {
                CreateStreamHandles(sourceHandle, streamHandle, indices);
                SetupPitchShift();
                SetupRMSNormalizationDSP();
                Mute();
            }
        }

        private bool CreateStreamHandles(int sourceHandle, int streamHandle, int[] indices)
        {
            // Get the mixer handle from the source channel
            int mixerHandle = BassMix.ChannelGetMixer(streamHandle);
            if (mixerHandle == 0)
            {
                YargLogger.LogError("Failed to get mixer handle from source channel. " + Bass.LastError );
                return false;
            }

            // Get the channel info from the source
            var channelInfo = Bass.ChannelGetInfo(streamHandle);
            int sourceChannels = channelInfo.Channels;

            _bustedHandle = StreamHandle.Create(sourceHandle, indices);
            if (_bustedHandle == null)
            {
                YargLogger.LogError("Failed to create busted stream.");
                return false;
            }

            // // Add the busted stream to the mixer
            BassFlags originalFlags = BassMix.ChannelFlags(streamHandle, 0, 0);
            if (!BassMix.MixerAddChannel(mixerHandle, _bustedHandle.Stream, originalFlags))
            {
                YargLogger.LogError("Failed to add busted stream to mixer.");
                return false;
            }

            // Get and apply the matrix if it exists
            if (originalFlags.HasFlag(BassFlags.MixerChanMatrix))
            {
                float[,] matrix = new float[2, sourceChannels];
                if (!BassMix.ChannelGetMatrix(streamHandle, matrix))
                {
                    YargLogger.LogError("Failed to get channel matrix for busted channel: " + Bass.LastError);
                    return false;
                }
                if (!BassMix.ChannelSetMatrix(_bustedHandle.Stream, matrix))
                {
                    YargLogger.LogError("Failed to set channel matrix for busted channel.");
                    return false;
                }
            }

            return true;
        }

        //TODO: This should not happen on main thread, also pass in time to next note
        public void PlayBustedNote(double durationMs)
        {
            if (_bustedHandle.PitchFX != 0)
            {
                int randomSemitones;
                do
                {
                    randomSemitones = Random.Range(-2, 2);
                }
                while (randomSemitones == 0 || randomSemitones == _lastPitchShift);
                _lastPitchShift = randomSemitones;
                _bustedPitchParams.fPitchShift = Mathf.Pow(2, randomSemitones / 12f);
                if (!BassHelpers.FXSetParameters(_bustedHandle.PitchFX, _bustedPitchParams))
                {
                    YargLogger.LogFormatError("Failed to set pitch on stream: {0}", Bass.LastError);
                }
            }

            if (!Bass.ChannelSlideAttribute(_bustedHandle.Stream, ChannelAttribute.Volume, 1.0f, 0))
            {
                YargLogger.LogFormatError("Failed to set busted volume: {0}!", Bass.LastError);
            }

            // Wait 750ms then hard cut to 0
            Task.Run(async () =>
            {
                YargLogger.LogDebug($"Delaying for {durationMs}ms");
                var delay = (int) Math.Clamp(durationMs, 500, 2000);
                await Task.Delay(delay);
                if (!Bass.ChannelSlideAttribute(_bustedHandle.Stream, ChannelAttribute.Volume, 0, 250))
                {
                    YargLogger.LogFormatError("Failed to set busted volume: {0}!", Bass.LastError);
                }
            });
        }

        private void Mute(int duration = 0)
        {
            if (!Bass.ChannelSlideAttribute(_bustedHandle.Stream, ChannelAttribute.Volume, 0, duration))
            {
                YargLogger.LogFormatError("Failed to set busted volume: {0}!", Bass.LastError);
            }
        }

        private bool SetupPitchShift()
        {
            _bustedHandle.PitchFX = BassHelpers.FXAddParameters(_bustedHandle.Stream, EffectType.PitchShift, _bustedPitchParams);
            if (_bustedHandle.PitchFX == 0)
            {
                YargLogger.LogError("Failed to set up pitch bend for busted stream!");
                return false;
            }
            return true;
        }

        private bool SetupRMSNormalizationDSP()
        {
            _dspHandle = Bass.ChannelSetDSP(_bustedHandle.Stream, RMSNormalizationDSP, IntPtr.Zero, 0);
            if (_dspHandle == 0)
            {
                YargLogger.LogFormatError("Failed to set up RMS normalization DSP: {0}", Bass.LastError);
                return false;
            }
            return true;
        }

        //TODO: handle stereo vs mono
        private void RMSNormalizationDSP(int handle, int channel, IntPtr buffer, int length, IntPtr user)
        {
            int sampleCount = length / 4;
            unsafe
            {
                float* samples = (float*)buffer;

                // Find peak (maximum absolute value) in buffer
                float peak = 0.0f;
                for (int i = 0; i < sampleCount; i++)
                {
                    float absSample = Math.Abs(samples[i]);
                    if (absSample > peak)
                    {
                        peak = absSample;
                    }
                }

                // Calculate gain needed to reach target peak
                float targetPeak = 0.5f;
                float gain = targetPeak / (peak + 0.0001f);
                gain = Math.Min(gain, 15.0f);

                // Apply gain to all samples
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = samples[i] * gain;
                }
            }
        }

        public void Dispose()
        {
            // Remove DSP if it exists
            if (_dspHandle != 0)
            {
                Bass.ChannelRemoveDSP(_bustedHandle.Stream, _dspHandle);
                _dspHandle = 0;
            }

            //TODO: do we need this
            if (_bustedHandle != null)
            {
                if (_bustedHandle.Stream != 0)
                {
                    if (!Bass.StreamFree(_bustedHandle.Stream))
                    {
                        //TODO: we can ignore this
                        YargLogger.LogFormatError("Failed to free busted stream: {0}", Bass.LastError);
                    }
                }
                _bustedHandle = null;
            }
        }
    }
}