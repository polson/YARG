using System;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace YARG.Audio.BASS
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OneShotNativeContext
    {
        public float* Sample;
        public double* Schedule;
        public int* Active;
        public int SampleFrames;
        public int ScheduleCount;
        public int Channels;
        public int SampleRate;
        public int ActiveCount;
        public int NextSchedule;
        public int PendingPlays;
        public int Enabled;
        public int Paused;
        public int VolumeBits;
        public int AnchorGeneration;
        public int AppliedGeneration;
        public long AnchorFrame;
        public long CursorFrame;
        public int ClearActive;
        public double AnchorSongPosition;
        public float Speed;
        public double LeadTime;
    }

    [BurstCompile]
    internal static unsafe class OneShotProcessor
    {
        internal const int MaxActive = 64;

        public static OneShotNativeContext* Create(float[] sample, double[] schedule,
            int sampleRate, int channels, double leadTime)
        {
            if (sample == null || sample.Length == 0 || channels <= 0 || sample.Length % channels != 0)
                return null;

            var context = (OneShotNativeContext*)UnsafeUtility.Malloc(
                sizeof(OneShotNativeContext), 16, Allocator.Persistent);
            if (context == null) return null;
            UnsafeUtility.MemClear(context, sizeof(OneShotNativeContext));

            context->SampleFrames = sample.Length / channels;
            context->ScheduleCount = schedule?.Length ?? 0;
            context->Channels = channels;
            context->SampleRate = sampleRate;
            context->LeadTime = Math.Max(0, leadTime);
            context->Speed = 1;
            context->Enabled = 1;
            float initialVolume = 1;
            context->VolumeBits = *(int*) &initialVolume;
            context->AppliedGeneration = -1;

            context->Sample = (float*)UnsafeUtility.Malloc(
                (long)sample.Length * sizeof(float), 16, Allocator.Persistent);
            context->Active = (int*)UnsafeUtility.Malloc(
                MaxActive * sizeof(int), 16, Allocator.Persistent);
            if (context->ScheduleCount > 0)
                context->Schedule = (double*)UnsafeUtility.Malloc(
                    (long)context->ScheduleCount * sizeof(double), 16, Allocator.Persistent);

            if (context->Sample == null || context->Active == null ||
                (context->ScheduleCount > 0 && context->Schedule == null))
            {
                Destroy(context);
                return null;
            }

            fixed (float* source = sample)
                UnsafeUtility.MemCpy(context->Sample, source, (long)sample.Length * sizeof(float));
            if (context->ScheduleCount > 0)
            {
                fixed (double* source = schedule)
                    UnsafeUtility.MemCpy(context->Schedule, source,
                        (long)context->ScheduleCount * sizeof(double));
            }
            return context;
        }

        public static void Destroy(OneShotNativeContext* context)
        {
            if (context == null) return;
            if (context->Sample != null) UnsafeUtility.Free(context->Sample, Allocator.Persistent);
            if (context->Schedule != null) UnsafeUtility.Free(context->Schedule, Allocator.Persistent);
            if (context->Active != null) UnsafeUtility.Free(context->Active, Allocator.Persistent);
            UnsafeUtility.Free(context, Allocator.Persistent);
        }

        public static void SetAnchor(OneShotNativeContext* c, long outputFrame,
            double songPosition, float speed, bool clearActive)
        {
            c->AnchorFrame = outputFrame;
            c->AnchorSongPosition = songPosition;
            c->Speed = Math.Max(0.0001f, speed);
            c->ClearActive = clearActive ? 1 : 0;
            if (clearActive) c->PendingPlays = 0;
            Interlocked.Exchange(ref c->AnchorGeneration, c->AnchorGeneration + 1);
        }

        public static void SetVolume(OneShotNativeContext* c, float volume)
        {
            int bits = *(int*) &volume;
            Interlocked.Exchange(ref c->VolumeBits, bits);
        }

        public static void SetEnabled(OneShotNativeContext* c, bool enabled)
        {
            Interlocked.Exchange(ref c->Enabled, enabled ? 1 : 0);
        }

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(BassNativeStreamProcedure))]
        public static int ProcessStream(int stream, void* buffer, int length, void* user)
        {
            var c = (OneShotNativeContext*)user;
            if (c == null || buffer == null || length <= 0) return 0;
            int frames = length / (sizeof(float) * c->Channels);
            if (frames <= 0) return length;
            float* output = (float*)buffer;
            for (int i = 0; i < frames * c->Channels; i++) output[i] = 0;
            if (c->Paused != 0) return length;

            int generation = Interlocked.CompareExchange(ref c->AnchorGeneration, 0, 0);
            if (generation != c->AppliedGeneration)
            {
                c->AppliedGeneration = generation;
                c->NextSchedule = FindFirst(c);
                c->CursorFrame = c->AnchorFrame;
                if (c->ClearActive != 0) c->ActiveCount = 0;
            }

            long start = c->CursorFrame;
            c->CursorFrame += frames;
            int volumeBits = Interlocked.CompareExchange(ref c->VolumeBits, 0, 0);
            float volume = c->Enabled != 0 ? *(float*) &volumeBits : 0;
            MixActive(c, output, frames, volume);
            int pending = Interlocked.Exchange(ref c->PendingPlays, 0);
            for (int i = 0; i < pending && c->ActiveCount < MaxActive; i++)
                Start(c, output, frames, 0, volume);

            while (c->NextSchedule < c->ScheduleCount)
            {
                long target = TargetFrame(c, c->Schedule[c->NextSchedule]);
                if (target >= start + frames) break;
                c->NextSchedule++;
                if (target >= start) Start(c, output, frames, (int)(target - start), volume);
            }
            return length;
        }

        private static int FindFirst(OneShotNativeContext* c)
        {
            double first = c->AnchorSongPosition + c->LeadTime * c->Speed;
            int i = 0;
            while (i < c->ScheduleCount &&
                   (c->LeadTime > 0 ? c->Schedule[i] <= first : c->Schedule[i] < first)) i++;
            return i;
        }

        private static long TargetFrame(OneShotNativeContext* c, double song)
        {
            double seconds = (song - c->AnchorSongPosition) / c->Speed - c->LeadTime;
            return c->AnchorFrame + (long)Math.Round(seconds * c->SampleRate);
        }

        private static void MixActive(OneShotNativeContext* c, float* output, int frames, float volume)
        {
            int write = 0;
            for (int i = 0; i < c->ActiveCount; i++)
            {
                int frame = c->Active[i];
                Mix(c, output, frames, 0, ref frame, volume);
                if (frame < c->SampleFrames) c->Active[write++] = frame;
            }
            c->ActiveCount = write;
        }

        private static void Start(OneShotNativeContext* c, float* output, int frames,
            int offset, float volume)
        {
            int frame = 0;
            Mix(c, output, frames, offset, ref frame, volume);
            if (frame < c->SampleFrames && c->ActiveCount < MaxActive) c->Active[c->ActiveCount++] = frame;
        }

        private static void Mix(OneShotNativeContext* c, float* output, int frames,
            int offset, ref int sourceFrame, float volume)
        {
            int count = Math.Min(frames - offset, c->SampleFrames - sourceFrame);
            int source = sourceFrame * c->Channels;
            int dest = offset * c->Channels;
            for (int i = 0; i < count * c->Channels; i++)
            {
                output[dest++] += c->Sample[source] * volume;
                source++;
            }
            sourceFrame += count;
        }
    }
}
