#nullable enable
using System;
using System.Runtime.InteropServices;
using YARG.Audio.BASS.Native;
using YARG.Core.Logging;

namespace YARG.Audio.BASS.Effects
{
    internal sealed class BassLeadAmpDsp : NativeDspHandle
    {
        private const string EFFECT_NAME   = "native lead amp DSP";
        private const int    INVALID_STATE = -2;

        private BassLeadAmpDsp()
        {
        }

        internal static BassLeadAmpDsp? Attach(int channelHandle, float gain, BassCabinetSet? cabinets, int priority = 0)
        {
            if (channelHandle == 0 || !YargAudioNative.IsFinite(gain))
            {
                YargLogger.LogFormatError("Cannot attach {0}: channel={1}, gain={2}, priority={3}.",
                    EFFECT_NAME, channelHandle, gain, priority);
                return null;
            }

            return YargAudioNative.Attach(EFFECT_NAME, channelHandle,
                (out BassLeadAmpDsp handle, out int bassError) =>
                    YargAudioBindings.LeadAmpDspAttach(channelHandle, gain, cabinets?.Samples, cabinets?.Frames,
                        cabinets?.Rates, cabinets?.Count ?? 0, priority, out handle, out bassError));
        }

        internal static BassLeadAmpTone? Match(int sampleRate, BassCabinetSet? cabinets, float[] stem, float[] di,
            uint[] noteFrames)
        {
            try
            {
                if (!YargAudioNative.CheckAbi())
                {
                    return null;
                }

                var tone = BassLeadAmpTone.Default.ToNative();
                int result = YargAudioBindings.LeadAmpMatch(sampleRate, cabinets?.Samples, cabinets?.Frames,
                    cabinets?.Rates, cabinets?.Count ?? 0, stem, di, noteFrames, noteFrames.Length, ref tone);
                if (result != 0)
                {
                    YargLogger.LogFormatInfo("Lead amp tone match skipped for {0} notes: result {1}",
                        noteFrames.Length, result);
                    return null;
                }

                return new BassLeadAmpTone(tone, BassLeadAmpTone.DEFAULT_REVERB_WET, BassLeadAmpTone.DEFAULT_REVERB_ROOM, 1);
            }
            catch (Exception exception) when (exception is DllNotFoundException or
                EntryPointNotFoundException or BadImageFormatException)
            {
                YargLogger.LogException(exception, "Failed to load the lead amp tone match");
                return null;
            }
        }

        internal static BassLeadAmpTone MatchSpace(BassLeadAmpTone tone, int sampleRate, float[]? region,
            double[] pickSeconds)
        {
            try
            {
                if (!YargAudioNative.CheckAbi())
                {
                    return tone;
                }

                var space = new SoloSpaceParams
                {
                    Size = (uint) Marshal.SizeOf<SoloSpaceParams>(),
                    EchoSeconds = tone.EchoSeconds,
                    EchoLevel = tone.EchoLevel,
                    ReverbWet = tone.ReverbWet,
                    ReverbRoom = tone.ReverbRoom,
                    LevelGain = 1,
                };
                int result = YargAudioBindings.SoloSpaceMatch(sampleRate, region, (ulong) (region?.Length ?? 0),
                    pickSeconds, pickSeconds.Length, ref space);
                if (result == INVALID_STATE)
                {
                    YargLogger.LogFormatInfo("Solo space kept its defaults: {0} picks had too few gaps",
                        pickSeconds.Length);
                }
                else if (result != 0)
                {
                    YargLogger.LogFormatInfo("Solo space match failed for {0} picks: result {1}",
                        pickSeconds.Length, result);
                    return tone;
                }

                return tone.WithSpace(space);
            }
            catch (Exception exception) when (exception is DllNotFoundException or
                EntryPointNotFoundException or BadImageFormatException)
            {
                YargLogger.LogException(exception, "Failed to load the solo space match");
                return tone;
            }
        }

        internal bool SetTone(BassLeadAmpTone tone)
        {
            var native = tone.ToNative();
            return YargAudioNative.TryInvoke(this, handle => YargAudioBindings.LeadAmpDspSetTone(handle, ref native));
        }

        internal bool SetGain(float gain)
        {
            if (!YargAudioNative.IsFinite(gain))
            {
                YargLogger.LogFormatError("Ignoring non-finite gain for {0}: {1}.", EFFECT_NAME, gain);
                return false;
            }

            return YargAudioNative.TryInvoke(this, handle => YargAudioBindings.LeadAmpDspSetGain(handle, gain));
        }

        protected override void Destroy(IntPtr handle) =>
            YargAudioBindings.LeadAmpDspDestroy(handle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct LeadAmpToneParams
        {
            public uint  Size;
            public uint  Cabinet;
            public float Drive;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = BassLeadAmpTone.BAND_COUNT)]
            public float[] BandsDb;
            public float WahStartHz;
            public float WahEndHz;
            public float EchoSeconds;
            public float EchoLevel;
            public float LevelGain;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SoloSpaceParams
        {
            public uint  Size;
            public float EchoSeconds;
            public float EchoLevel;
            public float ReverbWet;
            public float ReverbRoom;
            public float LevelGain;
        }
    }

    internal sealed class BassLeadAmpTone
    {
        internal const int   BAND_COUNT           = 25;
        internal const float DEFAULT_ECHO_SECONDS = 0.37f;
        internal const float DEFAULT_ECHO_LEVEL   = 0.35f;
        internal const float DEFAULT_REVERB_WET   = 0.25f;
        internal const float DEFAULT_REVERB_ROOM  = 0.55f;

        internal static readonly BassLeadAmpTone Default = new(new BassLeadAmpDsp.LeadAmpToneParams
        {
            Drive = 1,
            BandsDb = new float[BAND_COUNT],
            EchoSeconds = DEFAULT_ECHO_SECONDS,
            EchoLevel = DEFAULT_ECHO_LEVEL,
            LevelGain = 1,
        }, DEFAULT_REVERB_WET, DEFAULT_REVERB_ROOM, 1);

        public readonly int     Cabinet;
        public readonly float   Drive;
        public readonly float[] BandsDb;
        public readonly float   WahStartHz;
        public readonly float   WahEndHz;
        public readonly float   EchoSeconds;
        public readonly float   EchoLevel;
        public readonly float   LevelGain;
        public readonly float   ReverbWet;
        public readonly float   ReverbRoom;
        public readonly float   ReverbLevelGain;

        public bool HasWah => WahStartHz > 0;

        internal BassLeadAmpTone(BassLeadAmpDsp.LeadAmpToneParams tone, float reverbWet, float reverbRoom,
            float reverbLevelGain)
        {
            Cabinet = (int) tone.Cabinet;
            Drive = tone.Drive;
            BandsDb = tone.BandsDb;
            WahStartHz = tone.WahStartHz;
            WahEndHz = tone.WahEndHz;
            EchoSeconds = tone.EchoSeconds;
            EchoLevel = tone.EchoLevel;
            LevelGain = tone.LevelGain;
            ReverbWet = reverbWet;
            ReverbRoom = reverbRoom;
            ReverbLevelGain = reverbLevelGain;
        }

        internal BassLeadAmpDsp.LeadAmpToneParams ToNative()
        {
            return new BassLeadAmpDsp.LeadAmpToneParams
            {
                Size = (uint) Marshal.SizeOf<BassLeadAmpDsp.LeadAmpToneParams>(),
                Cabinet = (uint) Cabinet,
                Drive = Drive,
                BandsDb = (float[]) BandsDb.Clone(),
                WahStartHz = WahStartHz,
                WahEndHz = WahEndHz,
                EchoSeconds = EchoSeconds,
                EchoLevel = EchoLevel,
                LevelGain = LevelGain,
            };
        }

        internal BassLeadAmpTone WithSpace(BassLeadAmpDsp.SoloSpaceParams space)
        {
            var native = ToNative();
            native.EchoSeconds = space.EchoSeconds;
            native.EchoLevel = space.EchoLevel;
            return new BassLeadAmpTone(native, space.ReverbWet, space.ReverbRoom, space.LevelGain);
        }
    }
}
