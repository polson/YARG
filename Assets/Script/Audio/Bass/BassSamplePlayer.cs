#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Reuses sample decode streams and routes active voices through the SFX mixer.
    /// </summary>
    internal sealed class BassSamplePlayer : IDisposable
    {
        // Native BASS_SAMCHAN_STREAM flag. ManagedBass does not expose it by name.
        private const BassFlags SAMPLE_CHANNEL_STREAM = (BassFlags) 2;

        private sealed class Voice
        {
            public readonly int Channel;

            public Voice(int channel)
            {
                Channel = channel;
            }
        }

        private readonly object _lock = new();
        private readonly BassSfxMixer _mixer;
        private readonly int _sampleHandle;
        private readonly int _maxVoices;
        private readonly string _name;
        private readonly List<Voice> _voices = new();

        private OutputChannel? _outputChannel;
        private double _volume = 1;
        private int _nextVoiceIndex;
        private bool _disposed;

        public BassSamplePlayer(BassSfxMixer mixer, int sampleHandle, int maxVoices, string name)
        {
            _mixer = mixer;
            _sampleHandle = sampleHandle;
            _maxVoices = maxVoices;
            _name = name;
        }

        public void Play()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                Voice? voice = GetAvailableVoice();
                if (voice == null)
                {
                    return;
                }

                if (!Bass.ChannelSetPosition(voice.Channel, 0, PositionFlags.Bytes))
                {
                    YargLogger.LogFormatError("Failed to reset {0} sample voice: {1}!", _name, Bass.LastError);
                    return;
                }

                if (!Bass.ChannelSetAttribute(voice.Channel, ChannelAttribute.Volume, _volume))
                {
                    YargLogger.LogFormatError("Failed to set {0} sample volume: {1}!", _name, Bass.LastError);
                }

                _mixer.Play(voice.Channel, _outputChannel);
            }
        }

        public void SetVolume(double volume)
        {
            lock (_lock)
            {
                _volume = volume;
                foreach (var voice in _voices)
                {
                    if (Bass.ChannelIsActive(voice.Channel) == PlaybackState.Playing &&
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
            }
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
            int channel = Bass.SampleGetChannel(_sampleHandle,
                BassFlags.Decode | SAMPLE_CHANNEL_STREAM);
            if (channel == 0)
            {
                YargLogger.LogFormatError("Failed to create {0} sample voice: {1}!", _name, Bass.LastError);
                return null;
            }

            var voice = new Voice(channel);
            _voices.Add(voice);
            return voice;
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
                    _mixer.Remove(voice.Channel);
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
