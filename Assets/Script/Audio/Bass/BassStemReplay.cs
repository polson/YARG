#nullable enable
using System;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Fx;
using YARG.Core.Audio;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    internal sealed class BassStemReplay : IDisposable
    {
        private const double RECENT_SECONDS       = 3.0;
        private const double REPLAY_MIN_SECONDS   = 0.1;
        private const double REPLAY_MAX_SECONDS   = 2.0;
        private const double REPLAY_FADE_SECONDS  = 0.08;
        private const int    CAPTURE_DSP_PRIORITY = 1;
        private const int    FLOAT_BYTES          = sizeof(float);

        private readonly object                     _lock = new();
        private readonly int                        _stream;
        private readonly int                        _captureDsp;
        private readonly DSPProcedure               _captureProc;
        private readonly int                        _channelCount;
        private readonly int                        _frequency;
        private readonly float[]                    _ring;
        private readonly int                        _ringFrames;
        private readonly PushStream                 _pushStream;
        private readonly int                        _pitchFx;
        private readonly PitchShiftParametersStruct _pitchParams;
        private          long                       _capturedFrames;
        private          double                     _originSeconds;
        private          long                       _holdNextFrame;
        private          long                       _holdEndFrame;
        private          bool                       _holding;

        public BassStemReplay(AudioManager manager, int stream, PitchShiftParametersStruct pitchParams)
        {
            Bass.ChannelGetInfo(stream, out ChannelInfo info);
            _stream = stream;
            _frequency = info.Frequency;
            _channelCount = info.Channels;
            _ringFrames = (int) (RECENT_SECONDS * _frequency);
            _ring = new float[_ringFrames * _channelCount];

            _captureProc = OnCapture;
            _captureDsp = Bass.ChannelSetDSP(stream, _captureProc, IntPtr.Zero, CAPTURE_DSP_PRIORITY);
            if (_captureDsp == 0)
            {
                YargLogger.LogFormatError("Failed to capture stem audio for replay: {0}!", Bass.LastError);
            }

            _pushStream = manager.CreatePushStream(_frequency, _channelCount);
            _pitchParams = pitchParams;
            _pitchFx = BassHelpers.FXAddParameters(_pushStream.Handle, EffectType.PitchShift, pitchParams);
        }

        public void ResetAfterSeek(double seconds)
        {
            lock (_lock)
            {
                _capturedFrames = 0;
                _originSeconds = seconds;
                _holding = false;
            }
        }

        public void Play(int semitones, double noteSeconds, double durationSeconds, double volume)
        {
            lock (_lock)
            {
                long startFrame = (long) Math.Round((noteSeconds - _originSeconds) * _frequency);
                if (startFrame < 0 || startFrame < _capturedFrames - _ringFrames || startFrame >= _capturedFrames)
                {
                    return;
                }

                var pitch = _pitchParams;
                pitch.fPitchShift = (float) Math.Pow(2, semitones / 12.0);
                if (!BassHelpers.FXSetParameters(_pitchFx, pitch))
                {
                    YargLogger.LogFormatError("Failed to set replay pitch: {0}!", Bass.LastError);
                }

                _pushStream.SetVolume(volume);
                _pushStream.Clear();

                double seconds = Math.Clamp(durationSeconds, REPLAY_MIN_SECONDS, REPLAY_MAX_SECONDS);
                _holdNextFrame = startFrame;
                _holdEndFrame = startFrame + (long) Math.Round(seconds * _frequency);
                _holding = true;
                PushHeldAudio();
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _holding = false;
            }

            _pushStream.Clear();
        }

        public void Dispose()
        {
            if (!Bass.ChannelRemoveDSP(_stream, _captureDsp) && Bass.LastError != Errors.Handle)
            {
                YargLogger.LogFormatError("Failed to remove stem capture: {0}!", Bass.LastError);
            }

            _pushStream.Dispose();
        }

        private void OnCapture(int _, int __, IntPtr buffer, int length, IntPtr ___)
        {
            int sampleCount = length / FLOAT_BYTES;
            lock (_lock)
            {
                int ringStart = (int) (_capturedFrames % _ringFrames) * _channelCount;
                int firstCount = Math.Min(sampleCount, _ring.Length - ringStart);
                Marshal.Copy(buffer, _ring, ringStart, firstCount);
                Marshal.Copy(IntPtr.Add(buffer, firstCount * FLOAT_BYTES), _ring, 0, sampleCount - firstCount);
                _capturedFrames += sampleCount / _channelCount;
                PushHeldAudio();
            }
        }

        private void PushHeldAudio()
        {
            if (!_holding)
            {
                return;
            }

            long endFrame = Math.Min(_capturedFrames, _holdEndFrame);
            if (endFrame > _holdNextFrame)
            {
                int frames = (int) (endFrame - _holdNextFrame);
                var chunk = new float[frames * _channelCount];
                int fadeFrames = (int) (REPLAY_FADE_SECONDS * _frequency);
                for (int frame = 0; frame < frames; frame++)
                {
                    long absoluteFrame = _holdNextFrame + frame;
                    float gain = (float) Math.Clamp((double) (_holdEndFrame - absoluteFrame) / fadeFrames, 0, 1);
                    long ringFrame = absoluteFrame % _ringFrames;
                    for (int channel = 0; channel < _channelCount; channel++)
                    {
                        chunk[frame * _channelCount + channel] = _ring[ringFrame * _channelCount + channel] * gain;
                    }
                }

                var pinned = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                try
                {
                    _pushStream.Push(pinned.AddrOfPinnedObject(), chunk.Length * FLOAT_BYTES);
                }
                finally
                {
                    pinned.Free();
                }

                _holdNextFrame = endFrame;
            }

            if (_holdNextFrame >= _holdEndFrame)
            {
                _holding = false;
            }
        }
    }
}
