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
        private readonly BassAudioOutput             _audioOutput;
        private readonly HashSet<BassOneShotChannel> _oneShotChannels = new();
        private          bool                        _isValid;
        private          bool                        _resumeAfterOutputChange;
        private          double                      _outputVolume = 1;
        private          int                         _bufferLengthMilliseconds;
#nullable enable
        private OutputChannel? _outputChannel;
#nullable disable

        internal event Action OutputChanged;

        internal int  TempoStreamHandle { get; }
        public   bool IsValid           => _isValid;
        public   bool IsPlaying         => _isValid && _audioOutput.IsSongPlaying(TempoStreamHandle);

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
            DetachOneShotChannels();
        }

        internal void RestoreAfterOutputChange()
        {
            _isValid = true;
            RestoreOutputSettings();
            AttachOneShotChannels();

            if (_resumeAfterOutputChange)
            {
                Play(false);
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
                SetOneShotPlaybackPaused(false);
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
                SetOneShotPlaybackPaused(true);
            }

            return result;
        }

        public void PrepareForSeek()
        {
            _audioOutput.PrepareSongForSeek(TempoStreamHandle);
            foreach (var channel in _oneShotChannels)
            {
                channel.PrepareForSeek();
            }
        }

        public void ResetAfterSeek()
        {
            _audioOutput.ResetSongAfterSeek(TempoStreamHandle);
            foreach (var channel in _oneShotChannels)
            {
                channel.ResetAfterSeek();
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
            _audioOutput.FadeSong(TempoStreamHandle, BassAudioManager.ExponentialVolume(maxVolume),
                (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public void FadeOut(double duration)
        {
            _audioOutput.FadeSong(TempoStreamHandle, 0, (int) (duration * SongMetadata.MILLISECOND_FACTOR));
        }

        public double GetVolume() => BassAudioManager.LogarithmicVolume(_audioOutput.GetSongVolume(TempoStreamHandle));

        public void SetVolume(double volume)
        {
            _outputVolume = BassAudioManager.ExponentialVolume(volume);
            _audioOutput.SetSongVolume(TempoStreamHandle, _outputVolume);
        }

        public int GetFFTData(float[] buffer, int fftSize, bool complex)
        {
            int flags = GetFFTDataFlags(fftSize);
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

        private static int GetFFTDataFlags(int fftSize)
        {
            return (1 << fftSize) switch
            {
                256  => (int) DataFlags.FFT256,
                512  => (int) DataFlags.FFT512,
                1024 => (int) DataFlags.FFT1024,
                2048 => (int) DataFlags.FFT2048,
                4096 => (int) DataFlags.FFT4096,
                _    => -1,
            };
        }

        public int GetSampleData(float[] buffer) =>
            GetData(buffer, (buffer.Length * sizeof(float)) | (int) DataFlags.Float);

        private int GetData(float[] buffer, int flags)
        {
            int result = _audioOutput.GetSongData(TempoStreamHandle, buffer, flags);
            return result < 0 ? (int) Bass.LastError : result;
        }

        public int GetLevel(float[] level) => _audioOutput.GetSongLevel(TempoStreamHandle, level);
        public long GetPosition() => _audioOutput.GetSongPosition(TempoStreamHandle);
        public double GetLatency() => _audioOutput.GetTempoCommandDelay(TempoStreamHandle);
        public double GetPlaybackStartDelay() => _audioOutput.GetPlaybackStartDelay();

        public void SetBufferLength(int length)
        {
            _bufferLengthMilliseconds = length;
            _audioOutput.SetSongBufferLength(TempoStreamHandle, length);
        }

#nullable enable
        public void SetOutputChannel(OutputChannel? channel)
        {
            _outputChannel = channel;
            _audioOutput.SetSongOutputChannel(TempoStreamHandle, channel);
        }
#nullable disable

        public OneShotChannel CreateOneShotChannel(int sampleStream, IReadOnlyList<double> scheduledPlays,
            Func<long, double> getSongPosition, Func<float> getSpeed, double outputLeadTime)
        {
            var channel = new BassOneShotChannel(_audioOutput.GetSongMixerHandle(TempoStreamHandle), TempoStreamHandle,
                sampleStream, scheduledPlays, getSongPosition, getSpeed, outputLeadTime,
                _audioOutput.OneShotStartsPaused(TempoStreamHandle));
            channel.Disposed += OnOneShotDisposed;
            _oneShotChannels.Add(channel);
            return channel;
        }

        private void OnOneShotDisposed(BassOneShotChannel channel) => _oneShotChannels.Remove(channel);

        private void RestoreOutputSettings()
        {
            _audioOutput.SetSongVolume(TempoStreamHandle, _outputVolume);
            _audioOutput.SetSongBufferLength(TempoStreamHandle, _bufferLengthMilliseconds);
            _audioOutput.SetSongOutputChannel(TempoStreamHandle, _outputChannel);
        }

        private void DetachOneShotChannels()
        {
            foreach (var channel in _oneShotChannels)
            {
                channel.DetachOutput();
            }
        }

        private void AttachOneShotChannels()
        {
            foreach (var channel in _oneShotChannels)
            {
                channel.AttachOutput(_audioOutput.GetSongMixerHandle(TempoStreamHandle),
                    _audioOutput.OneShotStartsPaused(TempoStreamHandle));
            }
        }

        private void SetOneShotPlaybackPaused(bool paused)
        {
            foreach (var channel in _oneShotChannels)
            {
                channel.SetPlaybackPaused(paused);
            }
        }

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
