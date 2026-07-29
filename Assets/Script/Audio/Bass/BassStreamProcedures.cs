using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ManagedBass;

namespace YARG.Audio.BASS
{
    public class BassStreamProcedures : FileProcedures
    {
        public const int DiagnosticReadDelayMilliseconds = 2000;
        public static volatile bool DiagnosticReadDelayEnabled;

        private static long _readCount;
        private static long _totalBytesRead;
        private static long _gcOverlapReadCount;
        private static long _maximumReadTicks;
        private static long _lastReadStartTicks;
        private static long _lastReadEndTicks;
        private static long _lastReadDurationTicks;
        private static int _activeReads;
        private static int _lastReadThreadId;
        private static long _seekCount;
        private static long _lastSeekOffset;
        private static readonly ConcurrentDictionary<int, BassStreamProcedures> _streams = new();

        private readonly Stream _stream;
        private readonly long _start;
        private readonly long _length;
        private int _streamHandle;

        public BassStreamProcedures(Stream stream)
        {
            _stream = stream;
            _start = stream.Position;
            _length = stream.Length - _start;

            Close = (IntPtr) =>
            {
                int streamHandle = Interlocked.Exchange(ref _streamHandle, 0);
                if (streamHandle != 0)
                {
                    _streams.TryRemove(streamHandle, out _);
                }
                _stream.Close();
            };
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
                Interlocked.Exchange(ref _lastReadStartTicks, start);
                int bytesRead = 0;
                try
                {
                    if (DiagnosticReadDelayEnabled)
                    {
                        Thread.Sleep(DiagnosticReadDelayMilliseconds);
                    }

                    unsafe
                    {
                        bytesRead = _stream.Read(new Span<byte>((byte*) Buffer, Length));
                    }
                }
                catch
                {
                    bytesRead = 0;
                }
                finally
                {
                    long end = Stopwatch.GetTimestamp();
                    long elapsed = end - start;
                    Interlocked.Add(ref _totalBytesRead, bytesRead);
                    Interlocked.Decrement(ref _activeReads);
                    Interlocked.Exchange(ref _lastReadEndTicks, end);
                    Interlocked.Exchange(ref _lastReadDurationTicks, elapsed);
                    UpdateMaximum(ref _maximumReadTicks, elapsed);

                    if (GC.CollectionCount(0) != gen0Collections ||
                        GC.CollectionCount(1) != gen1Collections ||
                        GC.CollectionCount(2) != gen2Collections)
                    {
                        Interlocked.Increment(ref _gcOverlapReadCount);
                    }
                }
                return bytesRead;
            };

            Seek = (long Offset, IntPtr User) =>
            {
                try
                {
                    Interlocked.Increment(ref _seekCount);
                    Interlocked.Exchange(ref _lastSeekOffset, Offset);
                    _stream.Seek(Offset + _start, SeekOrigin.Begin);
                    return true;
                }
                catch
                {
                    return false;
                }
            };
        }

        public void RegisterStream(int streamHandle)
        {
            Interlocked.Exchange(ref _streamHandle, streamHandle);
            _streams[streamHandle] = this;
        }

        public static string GetDiagnostics()
        {
            long now = Stopwatch.GetTimestamp();
            long lastStart = Interlocked.Read(ref _lastReadStartTicks);
            long lastEnd = Interlocked.Read(ref _lastReadEndTicks);
            var diagnostics = new StringBuilder();
            diagnostics.AppendFormat(
                "reads={0}, bytes={1}, active={2}, last-read-thread={3}, " +
                "last-start-ago-ms={4:0.###}, last-end-ago-ms={5:0.###}, " +
                "last-read-ms={6:0.###}, gc-overlap-reads={7}, max-read-ms={8:0.###}, " +
                "seeks={9}, last-seek-offset={10}",
                Interlocked.Read(ref _readCount),
                Interlocked.Read(ref _totalBytesRead),
                Volatile.Read(ref _activeReads),
                Volatile.Read(ref _lastReadThreadId),
                AgeMilliseconds(now, lastStart),
                AgeMilliseconds(now, lastEnd),
                TicksToMilliseconds(Interlocked.Read(ref _lastReadDurationTicks)),
                Interlocked.Read(ref _gcOverlapReadCount),
                TicksToMilliseconds(Interlocked.Read(ref _maximumReadTicks)),
                Interlocked.Read(ref _seekCount),
                Interlocked.Read(ref _lastSeekOffset));

            foreach (int streamHandle in _streams.Keys)
            {
                long current = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Current);
                long asyncBuffer = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.AsyncBuffer);
                diagnostics.AppendFormat(
                    "; source={0}, state={1}, file-current={2}, async-buffer={3} B",
                    streamHandle,
                    Bass.ChannelIsActive(streamHandle),
                    current,
                    asyncBuffer);
            }
            return diagnostics.ToString();
        }

        public static void ResetDiagnostics()
        {
            Interlocked.Exchange(ref _readCount, 0);
            Interlocked.Exchange(ref _totalBytesRead, 0);
            Interlocked.Exchange(ref _gcOverlapReadCount, 0);
            Interlocked.Exchange(ref _maximumReadTicks, 0);
            Interlocked.Exchange(ref _lastReadStartTicks, 0);
            Interlocked.Exchange(ref _lastReadEndTicks, 0);
            Interlocked.Exchange(ref _lastReadDurationTicks, 0);
            Interlocked.Exchange(ref _lastReadThreadId, 0);
            Interlocked.Exchange(ref _seekCount, 0);
            Interlocked.Exchange(ref _lastSeekOffset, 0);
        }

        private static double TicksToMilliseconds(long ticks) =>
            ticks > 0 ? ticks * 1000.0 / Stopwatch.Frequency : 0;

        private static double AgeMilliseconds(long now, long timestamp) =>
            timestamp > 0 ? TicksToMilliseconds(now - timestamp) : 0;

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
