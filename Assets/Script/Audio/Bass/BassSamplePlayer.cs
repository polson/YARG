#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Reuses sample decode streams and routes active voices through configured audio output.
    /// </summary>
    internal sealed class BassSamplePlayer : IDisposable
    {
        // Native BASS_SAMCHAN_STREAM flag. ManagedBass does not expose it by name.
        private const BassFlags SAMPLE_CHANNEL_STREAM = (BassFlags) 2;
        private const int COMPLETION_POLL_PERIOD_MILLISECONDS = 20;

        private sealed class SampleVoice
        {
            public readonly int  ChannelHandle;
            public          bool IsFadingOut;
            public          bool IsPaused;
            public          bool IsCleanupQueued;

            public SampleVoice(int channelHandle)
            {
                ChannelHandle = channelHandle;
            }
        }

        private static readonly object                    RegisteredPlayersLock = new();
        private static readonly HashSet<BassSamplePlayer> RegisteredPlayers     = new();
        private static readonly Timer CompletionTimer = new(PollPlayersForCompletion, null,
            COMPLETION_POLL_PERIOD_MILLISECONDS, COMPLETION_POLL_PERIOD_MILLISECONDS);

        private readonly object            _stateLock = new();
        private readonly BassAudioOutput   _output;
        private readonly int               _sampleHandle;
        private readonly int               _maxVoices;
        private readonly string            _name;
        private readonly Action?           _playbackEnded;
        private readonly List<SampleVoice> _voices = new();

        private OutputChannel? _outputChannel;
        private double         _volume = 1;
        private int            _nextVoiceIndex;
        private bool           _disposed;

        public bool IsPlaying
        {
            get
            {
                lock (_stateLock)
                {
                    return ContainsVoiceInState(PlaybackState.Playing, PlaybackState.Stalled);
                }
            }
        }

        public bool IsPaused
        {
            get
            {
                lock (_stateLock)
                {
                    return _voices.Exists(voice => voice.IsPaused);
                }
            }
        }

        public BassSamplePlayer(BassAudioOutput output, int sampleHandle, int maxVoices, string name,
            Action? playbackEnded = null)
        {
            _output = output;
            _sampleHandle = sampleHandle;
            _maxVoices = maxVoices;
            _name = name;
            _playbackEnded = playbackEnded;
            lock (RegisteredPlayersLock)
            {
                RegisteredPlayers.Add(this);
            }
        }

        public bool Play(bool loop = false, int fadeInMilliseconds = 0)
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return false;
                }

                var voice = GetOrCreateAvailableVoice();
                if (voice == null)
                {
                    return false;
                }

                if (!PrepareVoiceForPlayback(voice, loop, fadeInMilliseconds))
                {
                    return false;
                }

                if (fadeInMilliseconds > 0 && !Bass.ChannelSlideAttribute(voice.ChannelHandle, ChannelAttribute.Volume,
                    (float) _volume, fadeInMilliseconds))
                {
                    YargLogger.LogFormatError("Failed to fade in {0}: {1}!", _name, Bass.LastError);
                }

                return true;
            }
        }

        public void Stop(int fadeOutMilliseconds = 0)
        {
            lock (_stateLock)
            {
                foreach (var voice in _voices)
                {
                    var state = Bass.ChannelIsActive(voice.ChannelHandle);
                    if (state is not (PlaybackState.Playing or PlaybackState.Stalled or PlaybackState.Paused))
                    {
                        continue;
                    }

                    if (fadeOutMilliseconds > 0 && state != PlaybackState.Paused &&
                        BeginFadeOut(voice, fadeOutMilliseconds))
                    {
                        continue;
                    }

                    StopVoice(voice);
                }
            }
        }

        public void Pause()
        {
            lock (_stateLock)
            {
                foreach (var voice in _voices)
                {
                    var state = Bass.ChannelIsActive(voice.ChannelHandle);
                    bool canBePaused = state is PlaybackState.Playing or PlaybackState.Stalled;
                    if (!canBePaused)
                    {
                        continue;
                    }

                    if (BassMix.ChannelFlags(voice.ChannelHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause) >=
                        0)
                    {
                        voice.IsPaused = true;
                    }
                }
            }
        }

        public void Resume()
        {
            lock (_stateLock)
            {
                foreach (var voice in _voices)
                {
                    if (!voice.IsPaused)
                    {
                        continue;
                    }

                    if (BassMix.ChannelFlags(voice.ChannelHandle, 0, BassFlags.MixerChanPause) >= 0)
                    {
                        voice.IsPaused = false;
                    }
                }
            }
        }

        public void SetVolume(double volume)
        {
            lock (_stateLock)
            {
                _volume = volume;
                foreach (var voice in _voices)
                {
                    if (Bass.ChannelIsActive(voice.ChannelHandle) != PlaybackState.Stopped &&
                        !Bass.ChannelSetAttribute(voice.ChannelHandle, ChannelAttribute.Volume, volume))
                    {
                        YargLogger.LogFormatError("Failed to set {0} sample volume: {1}!", _name, Bass.LastError);
                    }
                }
            }
        }

        public void SetOutputChannel(OutputChannel? outputChannel)
        {
            lock (_stateLock)
            {
                _outputChannel = outputChannel;
                foreach (var voice in _voices)
                {
                    if (Bass.ChannelIsActive(voice.ChannelHandle) != PlaybackState.Stopped)
                    {
                        _output.SetSampleOutputChannel(voice.ChannelHandle, outputChannel);
                    }
                }
            }
        }

        private bool PrepareVoiceForPlayback(SampleVoice voice, bool loop, int fadeInMilliseconds)
        {
            // Stopped voices may remain mixer sources until queued cleanup runs. Detach before
            // rewinding so mixer decoding cannot race the seek.
            _output.RemoveSample(voice.ChannelHandle);
            if (!Bass.ChannelSetPosition(voice.ChannelHandle, 0, PositionFlags.Bytes))
            {
                YargLogger.LogFormatError("Failed to reset {0} sample voice: {1}!", _name, Bass.LastError);
                return false;
            }

            SetVoiceLooping(voice.ChannelHandle, loop);
            voice.IsFadingOut = false;
            voice.IsPaused = false;
            voice.IsCleanupQueued = false;

            double initialVolume = fadeInMilliseconds > 0 ? 0 : _volume;
            if (!Bass.ChannelSetAttribute(voice.ChannelHandle, ChannelAttribute.Volume, initialVolume))
            {
                YargLogger.LogFormatError("Failed to set {0} sample volume: {1}!", _name, Bass.LastError);
            }

            return _output.PlaySample(voice.ChannelHandle, _outputChannel);
        }

        private bool BeginFadeOut(SampleVoice voice, int fadeOutMilliseconds)
        {
            SetVoiceLooping(voice.ChannelHandle, false);
            voice.IsFadingOut = true;
            if (Bass.ChannelSlideAttribute(voice.ChannelHandle, ChannelAttribute.Volume, 0, fadeOutMilliseconds))
            {
                return true;
            }

            YargLogger.LogFormatError("Failed to fade out {0}: {1}!", _name, Bass.LastError);
            return false;
        }

        private bool ContainsVoiceInState(params PlaybackState[] expectedStates)
        {
            foreach (var voice in _voices)
            {
                var state = Bass.ChannelIsActive(voice.ChannelHandle);
                foreach (var expectedState in expectedStates)
                {
                    if (state == expectedState)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool HasActiveVoices()
        {
            return _voices.Exists(voice => voice.IsPaused ||
                Bass.ChannelIsActive(voice.ChannelHandle) is PlaybackState.Playing or PlaybackState.Stalled);
        }

        private SampleVoice? GetOrCreateAvailableVoice()
        {
            int voiceCount = _voices.Count;
            for (int offset = 0; offset < voiceCount; offset++)
            {
                int index = (_nextVoiceIndex + offset) % voiceCount;
                var voice = _voices[index];
                if (Bass.ChannelIsActive(voice.ChannelHandle) == PlaybackState.Stopped)
                {
                    _nextVoiceIndex = (index + 1) % voiceCount;
                    return voice;
                }
            }

            if (_voices.Count >= _maxVoices)
            {
                return null;
            }

            return CreateVoice();
        }

        private SampleVoice? CreateVoice()
        {
            int channelHandle = Bass.SampleGetChannel(_sampleHandle, BassFlags.Decode | SAMPLE_CHANNEL_STREAM);
            if (channelHandle == 0)
            {
                YargLogger.LogFormatError("Failed to create {0} sample voice: {1}!", _name, Bass.LastError);
                return null;
            }

            var voice = new SampleVoice(channelHandle);
            _voices.Add(voice);
            return voice;
        }

        private static void PollPlayersForCompletion(object? _)
        {
            BassSamplePlayer[] players;
            lock (RegisteredPlayersLock)
            {
                players = new BassSamplePlayer[RegisteredPlayers.Count];
                RegisteredPlayers.CopyTo(players);
            }

            foreach (var player in players)
            {
                player.QueueCompletedVoicesForCleanup();
            }
        }

        private void QueueCompletedVoicesForCleanup()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var voice in _voices)
                {
                    if (voice.IsPaused || voice.IsCleanupQueued)
                    {
                        continue;
                    }

                    bool fadeCompleted = voice.IsFadingOut &&
                        !Bass.ChannelIsSliding(voice.ChannelHandle, ChannelAttribute.Volume);
                    bool playbackFinished = Bass.ChannelIsActive(voice.ChannelHandle) == PlaybackState.Stopped;
                    if (!fadeCompleted && !playbackFinished)
                    {
                        continue;
                    }

                    voice.IsCleanupQueued = true;
                    QueueCleanupOnMainThread(voice.ChannelHandle, fadeCompleted);
                }
            }
        }

        private void QueueCleanupOnMainThread(int channelHandle, bool fadeCompleted)
        {
            UnityMainThreadCallback.QueueEvent(() => FinishVoiceCleanup(channelHandle, fadeCompleted));
        }

        private void FinishVoiceCleanup(int channelHandle, bool fadeCompleted)
        {
            bool playbackEnded = false;
            lock (_stateLock)
            {
                var voice = FindVoice(channelHandle);
                if (_disposed)
                {
                    if (voice != null)
                    {
                        voice.IsCleanupQueued = false;
                    }

                    return;
                }

                bool playbackContinues = !fadeCompleted && Bass.ChannelIsActive(channelHandle) != PlaybackState.Stopped;
                if (playbackContinues)
                {
                    if (voice != null)
                    {
                        voice.IsCleanupQueued = false;
                    }

                    return;
                }

                if (fadeCompleted)
                {
                    if (voice == null || !voice.IsFadingOut)
                    {
                        return;
                    }

                    voice.IsFadingOut = false;
                    voice.IsCleanupQueued = false;
                    StopVoice(voice);
                }
                else
                {
                    if (voice != null)
                    {
                        voice.IsCleanupQueued = false;
                    }

                    _output.RemoveSample(channelHandle);
                }

                playbackEnded = !HasActiveVoices();
            }

            if (playbackEnded)
            {
                _playbackEnded?.Invoke();
            }
        }

        private SampleVoice? FindVoice(int channelHandle)
        {
            return _voices.Find(voice => voice.ChannelHandle == channelHandle);
        }

        private void StopVoice(SampleVoice voice)
        {
            voice.IsPaused = false;
            _output.RemoveSample(voice.ChannelHandle);
            Bass.ChannelSetPosition(voice.ChannelHandle, 0, PositionFlags.Bytes);
        }

        private static void SetVoiceLooping(int channelHandle, bool loop)
        {
            if (loop)
            {
                Bass.ChannelAddFlag(channelHandle, BassFlags.Loop);
            }
            else
            {
                Bass.ChannelRemoveFlag(channelHandle, BassFlags.Loop);
            }
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                lock (RegisteredPlayersLock)
                {
                    RegisteredPlayers.Remove(this);
                }

                foreach (var voice in _voices)
                {
                    _output.RemoveSample(voice.ChannelHandle);
                    if (!Bass.StreamFree(voice.ChannelHandle))
                    {
                        YargLogger.LogFormatError("Failed to free {0} sample voice: {1}!", _name, Bass.LastError);
                    }
                }

                _voices.Clear();
            }
        }
    }
}
