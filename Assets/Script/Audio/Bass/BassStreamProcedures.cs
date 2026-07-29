using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ManagedBass;

namespace YARG.Audio.BASS
{
    public class BassStreamProcedures : FileProcedures
    {
        public const int DiagnosticReadDelayMilliseconds = 2000;
        public static volatile bool DiagnosticReadDelayEnabled;

        private static long _readCount;
        private static long _gcOverlapReadCount;
        private static long _maximumReadTicks;
        private static int _activeReads;
        private static int _lastReadThreadId;

        private readonly Stream _stream;
        private readonly long _start;
        private readonly long _length;

        public BassStreamProcedures(Stream stream)
        {
            _stream = stream;
            _start = stream.Position;
            _length = stream.Length - _start;

            Close = (IntPtr) => _stream.Close();
            Length = (IntPtr) => _length;
            Read = (IntPtr Buffer, int Length, IntPtr User) =>
            {
                Interlocked.Exchange(ref _lastReadThreadId, Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref _readCount);
                Interlocked.Increment(ref _activeReads);
                int gen0Collections = GC.CollectionCount(0);
                int gen1Collections = GC.CollectionCount(1);
                int gen2Collections = GC.CollectionCount(2);
                long start = Stopwatch.GetTimestamp();
                try
                {
                    if (DiagnosticReadDelayEnabled)
                    {
                        Thread.Sleep(DiagnosticReadDelayMilliseconds);
                    }

                    unsafe
                    {
                        return _stream.Read(new Span<byte>((byte*) Buffer, Length));
                    }
                }
                catch
                {
                    return 0;
                }
                finally
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    Interlocked.Decrement(ref _activeReads);
                    UpdateMaximum(ref _maximumReadTicks, elapsed);

                    if (GC.CollectionCount(0) != gen0Collections ||
                        GC.CollectionCount(1) != gen1Collections ||
                        GC.CollectionCount(2) != gen2Collections)
                    {
                        Interlocked.Increment(ref _gcOverlapReadCount);
                    }
                }
            };

            Seek = (long Offset, IntPtr User) =>
            {
                try
                {
                    _stream.Seek(Offset + _start, SeekOrigin.Begin);
                    return true;
                }
                catch
                {
                    return false;
                }
            };
        }

        public static string GetDiagnostics()
        {
            return $"reads={Interlocked.Read(ref _readCount)}, " +
                $"active={Volatile.Read(ref _activeReads)}, " +
                $"last-read-thread={Volatile.Read(ref _lastReadThreadId)}, " +
                $"gc-overlap-reads={Interlocked.Read(ref _gcOverlapReadCount)}, " +
                $"max-read-ms={Interlocked.Read(ref _maximumReadTicks) * 1000.0 / Stopwatch.Frequency:0.###}";
        }

        private static void UpdateMaximum(ref long target, long value)
        {
            long previous;
            do
            {
                previous = Interlocked.Read(ref target);
                if (value <= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }
    }
}
