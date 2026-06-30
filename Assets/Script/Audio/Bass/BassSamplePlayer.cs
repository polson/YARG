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
    /// Sample channel helper class for adding samples to the SFX playback mixer, and playback
    /// </summary>
    internal sealed class BassSamplePlayer : IDisposable
    {


        /// <summary>
        /// Native BASS flag BASS_SAMCHAN_STREAM (value 2).
        /// When passed to Bass.SampleGetChannel, forces it to return an HSTREAM
        /// instead of an HCHANNEL. Required for adding sample channels to a mixer.
        /// Not exposed by ManagedBass's BassFlags enum.
        /// </summary>
        private const BassFlags SAMPLE_CHANNEL_STREAM = (BassFlags) 2;

        private readonly BassAudioManager     _manager;
        private readonly int                  _maxPlaybacks;
        private readonly List<PlayingSample>  _playingSamples = new();
        private readonly object               _lock = new();

        public OutputChannel? OutputChannel { get; set; }

        private bool IsAtPlaybackLimitLocked()
        {
            return _playingSamples.Count >= _maxPlaybacks;
        }

        /// <summary>
        /// True if there are any currently playing samples.
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                lock (_lock)
                {
                    return _playingSamples.Count > 0;
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BassSamplePlayer"/> class.
        /// </summary>
        /// <param name="manager">The BASS audio manager.</param>
        /// <param name="maxPlaybacks">The maximum number of simultaneous samples that can play.</param>
        public BassSamplePlayer(BassAudioManager manager, int maxPlaybacks)
        {
            _manager = manager;
            _maxPlaybacks = maxPlaybacks;
        }

        /// <summary>
        /// Obtains a playback channel from the sample handle and starts playing it.
        /// </summary>
        /// <param name="sampleHandle">The BASS sample handle.</param>
        /// <param name="name">The name of the sample for logging.</param>
        /// <param name="volume">The playback volume (0.0 to 1.0).</param>
        /// <param name="loop">True to loop the sample; otherwise, false.</param>
        /// <returns>The BASS channel handle if successful; otherwise, 0.</returns>
        public int PlaySample(int sampleHandle, string name, double volume, bool loop = false)
        {
            lock (_lock)
            {
                if (IsAtPlaybackLimitLocked())
                {
                    return 0;
                }
            }

            int channel = Bass.SampleGetChannel(sampleHandle, BassFlags.Decode | SAMPLE_CHANNEL_STREAM);
            if (channel == 0)
            {
                YargLogger.LogFormatError("Failed to get {0} sample channel: {1}!", name, Bass.LastError);
                return 0;
            }

            if (loop)
            {
                Bass.ChannelAddFlag(channel, BassFlags.Loop);
            }

            var playingSample = new PlayingSample(_manager, channel, name, OnPlayingSampleStopped);
            playingSample.SetVolume(volume);

            lock (_lock)
            {
                if (IsAtPlaybackLimitLocked())
                {
                    if (!Bass.StreamFree(channel))
                    {
                        YargLogger.LogFormatError("Failed to free unused {0} stream: {1}!", name, Bass.LastError);
                    }
                    return 0;
                }

                if (playingSample.AddToSfxPlaybackMixer(OutputChannel))
                {
                    _playingSamples.Add(playingSample);
                }
                else
                {
                    playingSample.Stop();
                    return 0;
                }
            }

            return channel;
        }

        private void OnPlayingSampleStopped(PlayingSample playingSample)
        {
            lock (_lock)
            {
                _playingSamples.Remove(playingSample);
            }
        }

        /// <summary>
        /// Stops all playing samples, with an optional fade.
        /// </summary>
        /// <param name="durationMs">The fade out duration in milliseconds.</param>
        public void Stop(int durationMs)
        {
            lock (_lock)
            {
                if (_playingSamples.Count == 0)
                {
                    return;
                }

                foreach (var playingSample in _playingSamples.ToArray())
                {
                    playingSample.Stop(durationMs);
                }
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                foreach (var playingSample in _playingSamples)
                {
                    playingSample.Pause();
                }
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                foreach (var playingSample in _playingSamples)
                {
                    playingSample.Resume();
                }
            }
        }

        /// <summary>
        /// Sets the playback volume for all active sources.
        /// </summary>
        /// <param name="volume">The new volume level (typically 0.0 to 1.0).</param>
        public void SetVolume(double volume)
        {
            lock (_lock)
            {
                foreach (var playingSample in _playingSamples)
                {
                    playingSample.SetVolume(volume);
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var playingSample in _playingSamples.ToArray())
                {
                    playingSample.Stop();
                }
                _playingSamples.Clear();
            }
        }

        private sealed class PlayingSample
        {
            private readonly BassAudioManager _manager;
            private readonly string           _name;
            private readonly Action<PlayingSample> _onStopped;

            private readonly object           _sampleLock = new();
            private readonly SyncProcedure _endSync;
            private readonly SyncProcedure _fadeOutSync;
            private          int           _endSyncHandle;
            private          int           _fadeOutSyncHandle;
            private          bool          _stopped;

            private int Channel { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="PlayingSample"/> class.
            /// </summary>
            /// <param name="manager">The BASS audio manager.</param>
            /// <param name="channel">The BASS channel handle for the sample.</param>
            /// <param name="name">The name of the sample for logging.</param>
            /// <param name="onStopped">Callback invoked when the sample stops playing and its resources are freed.</param>
            public PlayingSample(BassAudioManager manager, int channel, string name, Action<PlayingSample> onStopped)
            {
                _manager = manager;
                Channel = channel;
                _name = name;
                _onStopped = onStopped;
                _endSync = OnEnd;
                _fadeOutSync = OnFadeOutComplete;
            }

            /// <summary>
            /// Adds the sample's channel to the SFX playback mixer (which starts playback) and sets up the end-of-channel sync callback for automatic cleanup.
            /// </summary>
            /// <param name="outputChannel">The output channel configuration, or null for default routing.</param>
            /// <param name="extraFlags">Additional flags to apply to the mixer source channel.</param>
            /// <returns>True if successfully added to the SFX playback mixer; otherwise, false.</returns>
            public bool AddToSfxPlaybackMixer(OutputChannel? outputChannel, BassFlags extraFlags = BassFlags.Default)
            {
                if (!_manager.AddToSfxPlaybackMixer(Channel, outputChannel, extraFlags))
                {
                    return false;
                }

                _endSyncHandle = Bass.ChannelSetSync(Channel, SyncFlags.End, 0, _endSync);
                if (_endSyncHandle == 0)
                {
                    YargLogger.LogFormatError("Failed to set {0} end sync: {1}!", _name, Bass.LastError);
                }
                return true;
            }

            /// <summary>
            /// Sets the volume level for this specific playing sample's channel.
            /// </summary>
            /// <param name="volume">The volume level (typically 0.0 to 1.0).</param>
            public void SetVolume(double volume)
            {
                if (_stopped)
                {
                    return;
                }

                if (!Bass.ChannelSetAttribute(Channel, ChannelAttribute.Volume, volume))
                {
                    YargLogger.LogFormatError("Failed to set {0} volume: {1}!", _name, Bass.LastError);
                }
            }

            public void Pause()
            {
                if (!_stopped)
                {
                    BassMix.ChannelFlags(Channel, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
                }
            }

            public void Resume()
            {
                if (!_stopped)
                {
                    BassMix.ChannelFlags(Channel, 0, BassFlags.MixerChanPause);
                }
            }

            /// <summary>
            /// Stops playback of the sample and frees its associated resources.
            /// </summary>
            /// <param name="fadeDurationMs">Fade out duration in milliseconds. If 0 or less, playback stops immediately.</param>
            public void Stop(int fadeDurationMs = 0)
            {
                if (fadeDurationMs > 0 && FadeOut(fadeDurationMs))
                {
                    return;
                }

                StopImmediate();
            }

            private bool FadeOut(int durationMs)
            {
                // Turn off looping so that the sample is allowed to end naturally before the fade if need
                Bass.ChannelRemoveFlag(Channel, BassFlags.Loop);

                if (_fadeOutSyncHandle == 0)
                {
                    _fadeOutSyncHandle = Bass.ChannelSetSync(Channel, SyncFlags.Slided, 0, _fadeOutSync);
                    if (_fadeOutSyncHandle == 0)
                    {
                        YargLogger.LogFormatError("Failed to set {0} slide sync: {1}!", _name, Bass.LastError);
                    }
                }

                if (!Bass.ChannelSlideAttribute(Channel, ChannelAttribute.Volume, 0f, durationMs))
                {
                    YargLogger.LogFormatError("Failed to set volume slide for {0}: {1}!", _name, Bass.LastError);
                    return false;
                }
                return true;
            }

            private void StopImmediate()
            {
                lock (_sampleLock)
                {
                    if (_stopped)
                    {
                        return;
                    }

                    _stopped = true;
                }

                _manager.RemoveFromPlaybackMixer(Channel);
                if (!Bass.StreamFree(Channel))
                {
                    YargLogger.LogFormatError("Failed to free {0} stream: {1}!", _name, Bass.LastError);
                }
                _onStopped(this);
            }

            private void OnEnd(int handle, int channel, int data, IntPtr user)
            {
                UnityMainThreadCallback.QueueEvent(() => StopImmediate());
            }

            private void OnFadeOutComplete(int handle, int channel, int data, IntPtr user)
            {
                UnityMainThreadCallback.QueueEvent(() => StopImmediate());
            }
        }
    }
}
