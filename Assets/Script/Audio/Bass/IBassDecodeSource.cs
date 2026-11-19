#nullable enable
using System;

namespace YARG.Audio.BASS
{
    internal interface IBassDecodeSource : IDisposable
    {
        int SourceHandle { get; }

        event Action<IBassDecodeSource>? Disposed;
    }
}
