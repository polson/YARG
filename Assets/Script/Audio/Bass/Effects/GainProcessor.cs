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
    internal struct GainNativeContext
    {
        // Stored as bits so control-thread writes and audio-thread reads can be atomic.
        public int GainBits;
    }

    [BurstCompile]
    internal static unsafe class GainProcessor
    {
        public static GainNativeContext* Create(float gain)
        {
            var context = (GainNativeContext*) UnsafeUtility.Malloc(
                sizeof(GainNativeContext), 16, Allocator.Persistent);
            if (context == null)
            {
                return null;
            }

            SetGain(context, gain);
            return context;
        }

        public static void Destroy(GainNativeContext* context)
        {
            if (context != null)
            {
                UnsafeUtility.Free(context, Allocator.Persistent);
            }
        }

        public static void SetGain(GainNativeContext* context, float gain)
        {
            Interlocked.Exchange(ref context->GainBits, math.asint(gain));
        }

        [BurstCompile(CompileSynchronously = true)]
        [MonoPInvokeCallback(typeof(BassNativeDspProcedure))]
        public static void ProcessAudio(int dspHandle, int channelHandle, void* buffer, int length, void* user)
        {
            var context = (GainNativeContext*) user;
            if (context == null || buffer == null || length <= 0)
            {
                return;
            }

            int gainBits = Interlocked.CompareExchange(ref context->GainBits, 0, 0);
            float gain = math.asfloat(gainBits);
            Process((float*) buffer, length / sizeof(float), gain);
        }

        private static void Process(float* samples, int sampleCount, float gain)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] *= gain;
            }
        }
    }
}