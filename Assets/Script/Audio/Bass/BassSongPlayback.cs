using System;
using System.Collections.Generic;
using System.Linq;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Song;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Owns playback intent and one-shot state for one song.
    /// </summary>
    internal sealed class BassSongPlayback : IDisposable
    {
        private readonly BassAudioOutput _audioOutput;
        private readonly HashSet<BassOneShotChannel> _oneShotChannels = new();
        private bool _isValid;
        private bool _resumeAfterOutputChange;
        private double _volume = 1;
        private int _bufferLength;
#nullable enable
        private OutputChannel? _outputChannel;
#nullable disable

        internal event Action OutputChanged;

        internal int TempoStreamHandle { get; }
        public bool IsValid => _isValid;
        public bool IsPlaying => _isValid && _audioOutput.IsSongPlaying(TempoStreamHandle);

        internal BassSongPlayback(int tempoStreamHandle, BassAudioOutput audioOutput)
        {
            TempoStreamHandle = tempoStreamHandle;
            _audioOutput = audioOutput;
        }

        internal void MarkValid() => _isValid = true;
        internal void Invalidate() => _isValid = false;

        internal void PrepareForOutputChange()
        {
            _resumeAfterOutputChange = IsPlaying;
            foreach (var channel in _oneShotChannels)
            {
                channel.DetachOutput();
            }
        }

        internal void RestoreAfterOutputChange()
        {
            _isValid = true;
            _audioOutput.SetSongVolume(TempoStreamHandle, _volume);
            _audioOutput.SetSongBufferLength(TempoStreamHandle, _bufferLength);
            _audioOutput.SetSongOutputChannel(TempoStreamHandle, _outputChannel);
            foreach (var channel in _oneShotChannels)
            {
                channel.AttachOutput(
                    _audioOutput.GetSongMixerHandle(TempoStreamHandle),
                    _audioOutput.OneShotStartsPaused(TempoStreamHandle));
            }
            if (_resumeAfterOutputChange)
            {
                Play(restart: false);
            }
            OutputChanged?.Invoke();
        }

        public int Play(bool restart)
        {
            if (IsPlaying)
            {
                return 0;
            }

            int result = _audioOutput.PlaySong(TempoStreamHandle, restart);
            if (result == 0)
            {
                foreach (var channel in _oneShotChannels)
                {
                    channel.SetPlaybackPaused(false);
                }
            }
            return result;
        }

        public int Pause()
        {
            if (!IsPlaying)
            {
                return 0;
            }

            int result = _audioOutput.PauseSong(TempoStreamHandle);
            if (result == 0)
            {
                foreach (var channel in _oneShotChannels)
                {
                    channel.SetPlaybackPaused(true);
                }
            }
            return result;
        }

        public void ResetAfterSeek()
        {
            _audioOutput.ResetSongAfterSeek(TempoStreamHandle);
            foreach (var channel in _oneShotChannels)
            {
                channel.ResetAfterSeek();
            }
        }

        public void PrepareForSeek()
        {
            foreach (var channel in _oneShotChannels)
            {
                channel.PrepareForSeek();
            }
        }

        public void ResetAfterSpeedChange()
        {
            foreach (var channel in _oneShotChannels)
            {
                channel.ResetAfterSpeedChange();
            }
        }

        public void FadeIn(double maxVolume, double duration)
        {
            _audioOutput.FadeSong(TempoStreamHandle,
                BassAudioManager.ExponentialVolume(maxVolume),
                (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public void FadeOut(double duration)
        {
            _audioOutput.FadeSong(TempoStreamHandle, 0,
                (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public double GetVolume()
        {
            return BassAudioManager.LogarithmicVolume(_audioOutput.GetSongVolume(TempoStreamHandle));
        }

        public void SetVolume(double volume)
        {
            _volume = BassAudioManager.ExponentialVolume(volume);
            _audioOutput.SetSongVolume(TempoStreamHandle, _volume);
        }

        public int GetFFTData(float[] buffer, int fftSize, bool complex)
        {
            int flags = (1 << fftSize) switch
            {
                256  => (int) DataFlags.FFT256,
                512  => (int) DataFlags.FFT512,
                1024 => (int) DataFlags.FFT1024,
                2048 => (int) DataFlags.FFT2048,
                4096 => (int) DataFlags.FFT4096,
                _    => -1,
            };
            if (flags < 0)
            {
                return -1;
            }
            if (complex)
            {
                flags |= (int) DataFlags.FFTComplex;
            }
            return GetData(buffer, flags);
        }

        public int GetSampleData(float[] buffer)
        {
            return GetData(buffer, buffer.Length * sizeof(float) | (int) DataFlags.Float);
        }

        private int GetData(float[] buffer, int flags)
        {
            int result = _audioOutput.GetSongData(TempoStreamHandle, buffer, flags);
            return result < 0 ? (int) Bass.LastError : result;
        }

        public int GetLevel(float[] level) => _audioOutput.GetSongLevel(TempoStreamHandle, level);
        public double GetLatency() => _audioOutput.GetTempoCommandDelay(TempoStreamHandle);
        public double GetPlaybackStartDelay() => _audioOutput.GetPlaybackStartDelay();
        public void SetBufferLength(int length)
        {
            _bufferLength = length;
            _audioOutput.SetSongBufferLength(TempoStreamHandle, length);
        }

#nullable enable
        public void SetOutputChannel(OutputChannel? channel)
        {
            _outputChannel = channel;
            _audioOutput.SetSongOutputChannel(TempoStreamHandle, channel);
        }
#nullable disable

        public OneShotChannel CreateOneShotChannel(int sampleStream,
            IReadOnlyList<double> scheduledPlays, Func<long, double> getSongPosition,
            Func<float> getSpeed, double outputLeadTime)
        {
            var channel = new BassOneShotChannel(
                _audioOutput.GetSongMixerHandle(TempoStreamHandle),
                TempoStreamHandle,
                sampleStream,
                scheduledPlays,
                getSongPosition,
                getSpeed,
                outputLeadTime,
                _audioOutput.OneShotStartsPaused(TempoStreamHandle));
            channel.Disposed += OnOneShotDisposed;
            _oneShotChannels.Add(channel);
            return channel;
        }

        private void OnOneShotDisposed(BassOneShotChannel channel) => _oneShotChannels.Remove(channel);

        public void Dispose()
        {
            foreach (var channel in _oneShotChannels.ToArray())
            {
                channel.Dispose();
            }
            _oneShotChannels.Clear();
            _audioOutput.Remove(this);
            OutputChanged = null;
            _isValid = false;
        }
    }
}
