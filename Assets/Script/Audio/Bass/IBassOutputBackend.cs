#nullable enable
using System;
using YARG.Core.Audio;

namespace YARG.Audio.BASS
{
    internal interface IBassOutputBackend : IDisposable
    {
        int HeardLatencyMilliseconds { get; }
        bool SongMixerRunsContinuously { get; }
        double PlaybackStartDelay { get; }
        int SongMixerHandle(int tempoStreamHandle);

        bool Initialize(BassOutputDevice device);
        bool AttachSong(int tempoStreamHandle);
        void DetachSong(int tempoStreamHandle);
        bool IsSongPlaying(int tempoStreamHandle);
        int PlaySong(int tempoStreamHandle, bool restart);
        int PauseSong(int tempoStreamHandle);
        void PrepareSongForSeek(int tempoStreamHandle);
        void ResetSongAfterSeek(int tempoStreamHandle);
        void FadeSong(int tempoStreamHandle, double volume, int durationMilliseconds);
        double GetSongVolume(int tempoStreamHandle);
        void SetSongVolume(int tempoStreamHandle, double volume);
        int GetSongData(int tempoStreamHandle, float[] buffer, int flags);
        int GetSongLevel(int tempoStreamHandle, float[] level);
        long GetSongPosition(int tempoStreamHandle);
        double GetTempoCommandDelay(int tempoStreamHandle);
        void SetSongBufferLength(int tempoStreamHandle, int length);
        void SetSongOutputChannel(int tempoStreamHandle, OutputChannel? channel);

        bool AttachMonitor(int sourceHandle, double volume);
        void DetachMonitor(int sourceHandle);
        bool SetMonitorVolume(int sourceHandle, double volume);

        bool PlaySample(int sourceHandle, OutputChannel? outputChannel);
        void RemoveSample(int sourceHandle);
        void SetSampleOutputChannel(int sourceHandle, OutputChannel? outputChannel);
        void SetVolume(double volume);
    }
}
