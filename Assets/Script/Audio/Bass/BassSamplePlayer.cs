#nullable enable
using System;
using System.Collections.Generic;
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

        private sealed class Voice
        {
            public readonly int Channel;
            public readonly SyncProcedure EndSync;
            public readonly SyncProcedure FadeSync;
            public bool FadingOut;
            public bool Paused;

            public Voice(int channel, SyncProcedure endSync, SyncProcedure fadeSync)
            {
                Channel = channel;
                EndSync = endSync;
                FadeSync = fadeSync;
            }
        }

        private readonly object _lock = new();
        private readonly BassAudioOutput _output;
        private readonly int _sampleHandle;
        private readonly int _maxVoices;
        private readonly string _name;
        private readonly Action? _playbackEnded;
        private readonly List<Voice> _voices = new();

        private OutputChannel? _outputChannel;
        private double _volume = 1;
        private int _nextVoiceIndex;
        private bool _disposed;

        public bool IsPlaying
        {
            get
            {
                lock (_lock)
                {
                    return HasVoiceInState(PlaybackState.Playing, PlaybackState.Stalled);
                }
            }
        }

        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    return _voices.Exists(voice => voice.Paused);
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
        }

        public bool Play(bool loop = false, int fadeInMilliseconds = 0)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                Voice? voice = GetAvailableVoice();
                if (voice == null)
                {
                    return false;
                }

                // A stopped voice may still be registered as a mixer source until its queued
                // end cleanup runs. Detach it before rewinding so mixer decoding cannot race the seek.
                _output.RemoveSample(voice.Channel);
                if (!Bass.ChannelSetPosition(voice.Channel, 0, PositionFlags.Bytes))
                {
                    YargLogger.LogFormatError("Failed to reset {0} sample voice: {1}!", _name, Bass.LastError);
                    return false;
                }

                SetLooping(voice.Channel, loop);
                voice.FadingOut = false;
                voice.Paused = false;

                double initialVolume = fadeInMilliseconds > 0 ? 0 : _volume;
                if (!Bass.ChannelSetAttribute(voice.Channel, ChannelAttribute.Volume, initialVolume))
                {
                    YargLogger.LogFormatError("Failed to set {0} sample volume: {1}!", _name, Bass.LastError);
                }

                if (!_output.PlaySample(voice.Channel, _outputChannel))
                {
                    return false;
                }

                if (fadeInMilliseconds > 0 &&
                    !Bass.ChannelSlideAttribute(voice.Channel, ChannelAttribute.Volume, (float) _volume,
                        fadeInMilliseconds))
                {
                    YargLogger.LogFormatError("Failed to fade in {0}: {1}!", _name, Bass.LastError);
                }

                return true;
            }
        }

        public void Stop(int fadeOutMilliseconds = 0)
        {
            lock (_lock)
            {
                foreach (var voice in _voices)
                {
                    PlaybackState state = Bass.ChannelIsActive(voice.Channel);
                    if (state is not (PlaybackState.Playing or PlaybackState.Stalled or PlaybackState.Paused))
                    {
                        continue;
                    }

                    if (fadeOutMilliseconds > 0 && state != PlaybackState.Paused)
                    {
                        SetLooping(voice.Channel, false);
                        voice.FadingOut = true;
                        if (Bass.ChannelSlideAttribute(voice.Channel, ChannelAttribute.Volume, 0,
                                fadeOutMilliseconds))
                        {
                            continue;
                        }

                        YargLogger.LogFormatError("Failed to fade out {0}: {1}!", _name, Bass.LastError);
                    }

                    StopVoice(voice);
                }
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                foreach (var voice in _voices)
                {
                    if (Bass.ChannelIsActive(voice.Channel) is PlaybackState.Playing or PlaybackState.Stalled)
                    {
                        if (BassMix.ChannelFlags(voice.Channel, BassFlags.MixerChanPause,
                                BassFlags.MixerChanPause) >= 0)
                        {
                            voice.Paused = true;
                        }
                    }
                }
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                foreach (var voice in _voices)
                {
                    if (voice.Paused)
                    {
                        if (BassMix.ChannelFlags(voice.Channel, 0, BassFlags.MixerChanPause) >= 0)
                        {
                            voice.Paused = false;
                        }
                    }
                }
            }
        }

        public void SetVolume(double volume)
        {
            lock (_lock)
            {
                _volume = volume;
                foreach (var voice in _voices)
                {
                    if (Bass.ChannelIsActive(voice.Channel) != PlaybackState.Stopped &&
                        !Bass.ChannelSetAttribute(voice.Channel, ChannelAttribute.Volume, volume))
                    {
                        YargLogger.LogFormatError("Failed to set {0} sample volume: {1}!", _name, Bass.LastError);
                    }
                }
            }
        }

        public void SetOutputChannel(OutputChannel? outputChannel)
        {
            lock (_lock)
            {
                _outputChannel = outputChannel;
                foreach (var voice in _voices)
                {
                    if (Bass.ChannelIsActive(voice.Channel) != PlaybackState.Stopped)
                    {
                        _output.SetSampleOutputChannel(voice.Channel, outputChannel);
                    }
                }
            }
        }

        private bool HasVoiceInState(params PlaybackState[] states)
        {
            foreach (var voice in _voices)
            {
                PlaybackState state = Bass.ChannelIsActive(voice.Channel);
                foreach (var expected in states)
                {
                    if (state == expected)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool HasActiveVoice()
        {
            return _voices.Exists(voice => voice.Paused ||
                Bass.ChannelIsActive(voice.Channel) is PlaybackState.Playing or PlaybackState.Stalled);
        }

        private Voice? GetAvailableVoice()
        {
            int voiceCount = _voices.Count;
            for (int offset = 0; offset < voiceCount; offset++)
            {
                int index = (_nextVoiceIndex + offset) % voiceCount;
                Voice voice = _voices[index];
                if (Bass.ChannelIsActive(voice.Channel) == PlaybackState.Stopped)
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

        private Voice? CreateVoice()
        {
            int channel = Bass.SampleGetChannel(_sampleHandle, BassFlags.Decode | SAMPLE_CHANNEL_STREAM);
            if (channel == 0)
            {
                YargLogger.LogFormatError("Failed to create {0} sample voice: {1}!", _name, Bass.LastError);
                return null;
            }

            Voice? voice = null;
            SyncProcedure endSync = (_, _, _, _) => QueueVoiceCleanup(channel, false);
            SyncProcedure fadeSync = (_, _, _, _) =>
            {
                if (voice!.FadingOut)
                {
                    QueueVoiceCleanup(channel, true);
                }
            };
            voice = new Voice(channel, endSync, fadeSync);

            if (Bass.ChannelSetSync(channel, SyncFlags.End, 0, endSync) == 0)
            {
                YargLogger.LogFormatError("Failed to set {0} end sync: {1}!", _name, Bass.LastError);
            }
            if (Bass.ChannelSetSync(channel, SyncFlags.Slided, 0, fadeSync) == 0)
            {
                YargLogger.LogFormatError("Failed to set {0} fade sync: {1}!", _name, Bass.LastError);
            }

            _voices.Add(voice);
            return voice;
        }

        private void QueueVoiceCleanup(int channel, bool stop)
        {
            UnityMainThreadCallback.QueueEvent(() => CleanupVoice(channel, stop));
        }

        private void CleanupVoice(int channel, bool stop)
        {
            bool playbackEnded = false;
            lock (_lock)
            {
                if (_disposed || (!stop && Bass.ChannelIsActive(channel) != PlaybackState.Stopped))
                {
                    return;
                }

                if (stop)
                {
                    Voice? voice = _voices.Find(candidate => candidate.Channel == channel);
                    if (voice == null || !voice.FadingOut)
                    {
                        return;
                    }
                    voice.FadingOut = false;
                    StopVoice(voice);
                }
                else
                {
                    _output.RemoveSample(channel);
                }
                playbackEnded = !HasActiveVoice();
            }

            if (playbackEnded)
            {
                _playbackEnded?.Invoke();
            }
        }

        private void StopVoice(Voice voice)
        {
            voice.Paused = false;
            _output.RemoveSample(voice.Channel);
            Bass.ChannelSetPosition(voice.Channel, 0, PositionFlags.Bytes);
        }

        private static void SetLooping(int channel, bool loop)
        {
            if (loop)
            {
                Bass.ChannelAddFlag(channel, BassFlags.Loop);
            }
            else
            {
                Bass.ChannelRemoveFlag(channel, BassFlags.Loop);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (var voice in _voices)
                {
                    _output.RemoveSample(voice.Channel);
                    if (!Bass.StreamFree(voice.Channel))
                    {
                        YargLogger.LogFormatError("Failed to free {0} sample voice: {1}!", _name, Bass.LastError);
                    }
                }
                _voices.Clear();
            }
        }
    }
}
