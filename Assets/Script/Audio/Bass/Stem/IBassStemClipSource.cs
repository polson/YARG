#nullable enable
namespace YARG.Audio.BASS
{
    internal interface IBassStemClipSource
    {
        bool TryGetClip(int key, out BassStemClip clip);
        bool TryGetFretClip(int key, out BassStemClip clip);
        bool TryGetAnyClip(out BassStemClip clip);
    }
}
