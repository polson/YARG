using System;
using JetBrains.Annotations;
using ManagedBass;
using ManagedBass.Fx;
using TMPro;
using UnityEngine.UIElements;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    public class BassTempoStream: IDisposable
    {
        public readonly int    handle;
        private         double _positionOffset = 0.0;
        private         bool   _didSetPosition = false;
        public          double Length => BassAudioManager.GetLengthInSeconds(handle);
        private bool IsPlaying
        {
            get
            {
                var playbackState = Bass.ChannelIsActive(handle);
                return playbackState == PlaybackState.Playing;
            }
        }

        private BassTempoStream(int handle)
        {
            this.handle = handle;
        }

        public void SetPosition(double position)
        {
            _didSetPosition = true;
            _positionOffset = position;
        }

        public void SetVolume(double volume)
        {
            if (!Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set tempo stream volume: {0}", Bass.LastError);
            }
        }

        public void SetSpeed(double speed)
        {
            if (!Bass.ChannelSetAttribute(handle, ChannelAttribute.Tempo, speed))
            {
                YargLogger.LogFormatError("Failed to set channel speed: {0}!", Bass.LastError);
            }
        }

        public double GetPosition()
        {
            YargLogger.LogDebug($"position result: {GetTempoStreamPositionSeconds()} + {_positionOffset}");
            return GetTempoStreamPositionSeconds() + _positionOffset;
        }

        public double GetVolume()
        {
            if (!Bass.ChannelGetAttribute(handle, ChannelAttribute.Volume, out float volume))
            {
                YargLogger.LogFormatError("Failed to get volume: {0}", Bass.LastError);
            }
            return volume;
        }

        public int Play()
        {
            if (IsPlaying)
            {
                return 0;
            }

            if (!Bass.ChannelPlay(handle, _didSetPosition))
            {
                return (int) Bass.LastError;
            }

            _didSetPosition = false;

            return 0;
        }

        public int Pause()
        {
            if (!IsPlaying)
            {
                return 0;
            }

            if (!Bass.ChannelPause(handle))
            {
                return (int) Bass.LastError;
            }

            return 0;
        }

        public double GetTempoStreamPositionSeconds()
        {
            long positionBytes = Bass.ChannelGetPosition(handle);
            if (positionBytes < 0)
            {
                YargLogger.LogFormatError("Failed to get byte position: {0}!", Bass.LastError);
                return 0.0f;
            }

            double seconds = Bass.ChannelBytes2Seconds(handle, positionBytes);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to convert bytes to seconds: {0}!", Bass.LastError);
                return 0.0f;
            }

            return seconds;
        }

        public void SetPitch(float semitoneShift)
        {
            if (!Bass.ChannelSetAttribute(handle, ChannelAttribute.Pitch, semitoneShift))
            {
                YargLogger.LogFormatError("Failed to set channel pitch: {0}!", Bass.LastError);
            }
        }

        public static bool CreateFromMixer(int mixerHandle, out BassTempoStream tempoStream)
        {
            tempoStream = null;

            var tempoHandle = BassFx.TempoCreate(mixerHandle, BassFlags.Default);
            if (tempoHandle == 0)
            {
                return false;
            }
            tempoStream = new BassTempoStream(tempoHandle);
            return true;
        }

        public void Dispose()
        {
            if (handle == 0)
            {
                return;
            }

            if (!Bass.StreamFree(handle))
            {
                YargLogger.LogFormatError("Failed to free tempo stream: {0}!", Bass.LastError);
            }
        }
    }
}