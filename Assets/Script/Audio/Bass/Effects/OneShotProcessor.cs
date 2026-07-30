using System;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace YARG.Audio.BASS.Effects
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OneShotNativeContext
    {
        // Immutable sample and schedule data owned by this context.
        public float*  SampleData;
        public double* ScheduledSongPositions;
        public int*    ActivePlaybackFrames;
        public int     SampleFrameCount;
        public int     ScheduledPlaybackCount;
        public int     ChannelCount;
        public int     SampleRate;
        public double  LeadTime;

        // Audio-thread state.
        public int    ActivePlaybackCount;
        public int    NextScheduledPlayback;
        public int    AppliedAnchorVersion;
        public long   TimelineAnchorFrame;
        public long   CursorFrame;
        public double TimelineAnchorSongPosition;
        public float  PlaybackSpeed;

        // Atomic values shared by control and audio threads.
        public int PendingPlaybackCount;
        public int IsEnabled;
        public int IsPaused;
        public int VolumeBits;

        // AnchorVersion guards this control-thread snapshot. Odd versions are being
        // written; even versions are safe for the audio thread to consume.
        public int    AnchorVersion;
        public long   PendingAnchorFrame;
        public double PendingAnchorSongPosition;
        public float  PendingPlaybackSpeed;
        public int    PendingClearActivePlaybacks;
    }

    /// <summary>
    /// Burst-compatible source which mixes overlapping copies of one sample. Playback
    /// can be requested directly or scheduled against an anchored song timeline.
    /// </summary>
    [BurstCompile]
    internal static unsafe class OneShotProcessor
    {
        internal const int MAX_ACTIVE_PLAYBACKS = 64;

        public static OneShotNativeContext* Create(float[] sampleData, double[] scheduledSongPositions, int sampleRate,
            int channelCount, double leadTime)
        {
            bool hasInvalidSample = sampleData == null || sampleData.Length == 0 || channelCount <= 0 ||
                sampleData.Length % channelCount != 0;
            if (hasInvalidSample || sampleRate <= 0)
            {
                return null;
            }

            var context = (OneShotNativeContext*) UnsafeUtility.Malloc(
                sizeof(OneShotNativeContext), 16, Allocator.Persistent);
            if (context == null)
            {
                return null;
            }

            UnsafeUtility.MemClear(context, sizeof(OneShotNativeContext));

            context->SampleFrameCount = sampleData.Length / channelCount;
            context->ScheduledPlaybackCount = scheduledSongPositions?.Length ?? 0;
            context->ChannelCount = channelCount;
            context->SampleRate = sampleRate;
            context->LeadTime = Math.Max(0, leadTime);
            context->PlaybackSpeed = 1;
            context->PendingPlaybackSpeed = 1;
            context->IsEnabled = 1;
            context->VolumeBits = math.asint(1f);
            context->AppliedAnchorVersion = -1;

            context->SampleData = (float*) UnsafeUtility.Malloc(
                (long) sampleData.Length * sizeof(float), 16, Allocator.Persistent);
            context->ActivePlaybackFrames = (int*) UnsafeUtility.Malloc(
                MAX_ACTIVE_PLAYBACKS * sizeof(int), 16, Allocator.Persistent);
            if (context->ScheduledPlaybackCount > 0)
            {
                context->ScheduledSongPositions = (double*) UnsafeUtility.Malloc(
                    (long) context->ScheduledPlaybackCount * sizeof(double), 16, Allocator.Persistent);
            }

            bool allocationFailed = context->SampleData == null || context->ActivePlaybackFrames == null ||
                (context->ScheduledPlaybackCount > 0 && context->ScheduledSongPositions == null);
            if (allocationFailed)
            {
                Destroy(context);
                return null;
            }

            fixed (float* source = sampleData)
            {
                long sampleBytes = (long) sampleData.Length * sizeof(float);
                UnsafeUtility.MemCpy(context->SampleData, source, sampleBytes);
            }

            if (context->ScheduledPlaybackCount > 0)
            {
                fixed (double* source = scheduledSongPositions)
                {
                    long scheduleBytes = (long) context->ScheduledPlaybackCount * sizeof(double);
                    UnsafeUtility.MemCpy(context->ScheduledSongPositions, source, scheduleBytes);
                }
            }

            return context;
        }

        public static void Destroy(OneShotNativeContext* context)
        {
            if (context == null)
            {
                return;
            }

            if (context->SampleData != null)
            {
                UnsafeUtility.Free(context->SampleData, Allocator.Persistent);
            }

            if (context->ScheduledSongPositions != null)
            {
                UnsafeUtility.Free(context->ScheduledSongPositions, Allocator.Persistent);
            }

            if (context->ActivePlaybackFrames != null)
            {
                UnsafeUtility.Free(context->ActivePlaybackFrames, Allocator.Persistent);
            }

            UnsafeUtility.Free(context, Allocator.Persistent);
        }

        public static void SetAnchor(OneShotNativeContext* context, long outputFrame, double songPosition, float speed,
            bool clearActivePlaybacks)
        {
            // Publish anchor as a seqlock snapshot. Audio thread never reads fields while
            // control thread is partway through changing them.
            Interlocked.Increment(ref context->AnchorVersion);
            context->PendingAnchorFrame = outputFrame;
            context->PendingAnchorSongPosition = songPosition;
            context->PendingPlaybackSpeed = Math.Max(0.0001f, speed);
            context->PendingClearActivePlaybacks = clearActivePlaybacks ? 1 : 0;
            Interlocked.Increment(ref context->AnchorVersion);

            if (clearActivePlaybacks)
            {
                Interlocked.Exchange(ref context->PendingPlaybackCount, 0);
            }
        }

        public static void SetVolume(OneShotNativeContext* context, float volume)
        {
            Interlocked.Exchange(ref context->VolumeBits, math.asint(volume));
        }

        public static void SetEnabled(OneShotNativeContext* context, bool enabled)
        {
            Interlocked.Exchange(ref context->IsEnabled, enabled ? 1 : 0);
        }

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(BassNativeStreamProcedure))]
        public static int ProcessStream(int streamHandle, void* buffer, int byteCount, void* user)
        {
            var context = (OneShotNativeContext*) user;
            if (context == null || buffer == null || byteCount <= 0)
            {
                return 0;
            }

            int outputFrameCount = byteCount / (sizeof(float) * context->ChannelCount);
            if (outputFrameCount <= 0)
            {
                return byteCount;
            }

            float* output = (float*) buffer;
            ClearOutput(output, outputFrameCount, context->ChannelCount);

            if (AtomicRead(ref context->IsPaused) != 0)
            {
                return byteCount;
            }

            ApplyPendingAnchor(context);

            long bufferStartFrame = context->CursorFrame;
            long bufferEndFrame = bufferStartFrame + outputFrameCount;
            context->CursorFrame = bufferEndFrame;

            float volume = ReadOutputVolume(context);
            MixActivePlaybacks(context, output, outputFrameCount, volume);
            StartPendingPlaybacks(context, output, outputFrameCount, volume);
            StartScheduledPlaybacks(context, output, outputFrameCount, bufferStartFrame, bufferEndFrame, volume);

            return byteCount;
        }

        private static void ClearOutput(float* output, int frameCount, int channelCount)
        {
            long byteCount = (long) frameCount * channelCount * sizeof(float);
            UnsafeUtility.MemClear(output, byteCount);
        }

        private static void ApplyPendingAnchor(OneShotNativeContext* context)
        {
            int version = AtomicRead(ref context->AnchorVersion);
            bool isWriteInProgress = (version & 1) != 0;
            if (isWriteInProgress || version == context->AppliedAnchorVersion)
            {
                return;
            }

            long anchorFrame = context->PendingAnchorFrame;
            double songPosition = context->PendingAnchorSongPosition;
            float speed = context->PendingPlaybackSpeed;
            int clearActivePlaybacks = context->PendingClearActivePlaybacks;

            if (version != AtomicRead(ref context->AnchorVersion))
            {
                return;
            }

            context->AppliedAnchorVersion = version;
            context->TimelineAnchorFrame = anchorFrame;
            context->TimelineAnchorSongPosition = songPosition;
            context->PlaybackSpeed = speed;
            context->CursorFrame = anchorFrame;
            context->NextScheduledPlayback = FindNextScheduledPlayback(context);

            if (clearActivePlaybacks != 0)
            {
                context->ActivePlaybackCount = 0;
            }
        }

        private static float ReadOutputVolume(OneShotNativeContext* context)
        {
            bool isEnabled = AtomicRead(ref context->IsEnabled) != 0;
            int volumeBits = AtomicRead(ref context->VolumeBits);
            return isEnabled ? math.asfloat(volumeBits) : 0;
        }

        private static void MixActivePlaybacks(OneShotNativeContext* context, float* output, int outputFrameCount,
            float volume)
        {
            int activeWriteIndex = 0;
            for (int i = 0; i < context->ActivePlaybackCount; i++)
            {
                int sampleFrame = context->ActivePlaybackFrames[i];
                MixPlayback(context, output, outputFrameCount, 0, ref sampleFrame, volume);

                if (sampleFrame < context->SampleFrameCount)
                {
                    context->ActivePlaybackFrames[activeWriteIndex++] = sampleFrame;
                }
            }

            context->ActivePlaybackCount = activeWriteIndex;
        }

        private static void StartPendingPlaybacks(OneShotNativeContext* context, float* output, int outputFrameCount,
            float volume)
        {
            int pendingCount = Interlocked.Exchange(ref context->PendingPlaybackCount, 0);
            for (int i = 0; i < pendingCount && context->ActivePlaybackCount < MAX_ACTIVE_PLAYBACKS; i++)
            {
                StartPlayback(context, output, outputFrameCount, 0, volume);
            }
        }

        private static void StartScheduledPlaybacks(OneShotNativeContext* context, float* output, int outputFrameCount,
            long bufferStartFrame, long bufferEndFrame, float volume)
        {
            while (context->NextScheduledPlayback < context->ScheduledPlaybackCount)
            {
                double songPosition = context->ScheduledSongPositions[context->NextScheduledPlayback];
                long targetFrame = SongPositionToOutputFrame(context, songPosition);
                if (targetFrame >= bufferEndFrame)
                {
                    break;
                }

                context->NextScheduledPlayback++;
                if (targetFrame >= bufferStartFrame)
                {
                    int outputFrameOffset = (int) (targetFrame - bufferStartFrame);
                    StartPlayback(context, output, outputFrameCount, outputFrameOffset, volume);
                }
            }
        }

        private static int FindNextScheduledPlayback(OneShotNativeContext* context)
        {
            double scheduleBoundary = context->TimelineAnchorSongPosition + context->LeadTime * context->PlaybackSpeed;
            int scheduleIndex = 0;

            while (scheduleIndex < context->ScheduledPlaybackCount)
            {
                double scheduledPosition = context->ScheduledSongPositions[scheduleIndex];
                bool isBeforeBoundary = context->LeadTime > 0
                    ? scheduledPosition <= scheduleBoundary
                    : scheduledPosition < scheduleBoundary;
                if (!isBeforeBoundary)
                {
                    break;
                }

                scheduleIndex++;
            }

            return scheduleIndex;
        }

        private static long SongPositionToOutputFrame(OneShotNativeContext* context, double songPosition)
        {
            double secondsFromAnchor = (songPosition - context->TimelineAnchorSongPosition) / context->PlaybackSpeed;
            double outputSecondsFromAnchor = secondsFromAnchor - context->LeadTime;
            long frameOffset = (long) Math.Round(outputSecondsFromAnchor * context->SampleRate);
            return context->TimelineAnchorFrame + frameOffset;
        }

        private static void StartPlayback(OneShotNativeContext* context, float* output, int outputFrameCount,
            int outputFrameOffset, float volume)
        {
            int sampleFrame = 0;
            MixPlayback(context, output, outputFrameCount, outputFrameOffset, ref sampleFrame, volume);

            bool playbackContinues = sampleFrame < context->SampleFrameCount;
            if (playbackContinues && context->ActivePlaybackCount < MAX_ACTIVE_PLAYBACKS)
            {
                context->ActivePlaybackFrames[context->ActivePlaybackCount++] = sampleFrame;
            }
        }

        private static void MixPlayback(OneShotNativeContext* context, float* output, int outputFrameCount,
            int outputFrameOffset, ref int sampleFrame, float volume)
        {
            int availableOutputFrames = outputFrameCount - outputFrameOffset;
            int remainingSampleFrames = context->SampleFrameCount - sampleFrame;
            int framesToMix = Math.Min(availableOutputFrames, remainingSampleFrames);
            int samplesToMix = framesToMix * context->ChannelCount;
            int sourceSample = sampleFrame * context->ChannelCount;
            int outputSample = outputFrameOffset * context->ChannelCount;

            for (int i = 0; i < samplesToMix; i++)
            {
                output[outputSample++] += context->SampleData[sourceSample++] * volume;
            }

            sampleFrame += framesToMix;
        }

        private static int AtomicRead(ref int value) => Interlocked.CompareExchange(ref value, 0, 0);
    }
}