#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Audio.BASS
{
    internal static class BassGuitarSamples
    {
        private const string FOLDER         = "guitarSamples";
        private const string CABINET_FOLDER = "guitarCabinet";
        private const string RELEASE_NAME   = "release";
        private const double MAX_SECONDS    = 5.5;
        private const int    FLOAT_BYTES    = sizeof(float);
        private const int    CHUNK_FRAMES   = 4096;

        internal static BassSynthGuitar? Load(BassStemFormat format)
        {
            string folder = Path.Combine(PathHelper.StreamingAssetsPath, FOLDER);
            if (!Directory.Exists(folder))
            {
                YargLogger.LogFormatError("Guitar sample folder {0} is missing", folder);
                return null;
            }

            var notes = new List<(int Note, int Layer, float[] Mono, int Frequency)>();
            var releases = new List<(int Note, float[] Mono, int Frequency)>();
            var paths = Directory.GetFiles(folder, "*.opus");
            Array.Sort(paths, StringComparer.Ordinal);
            foreach (string path in paths)
            {
                var parts = Path.GetFileNameWithoutExtension(path).Split('_');
                if (parts.Length != 3 || !int.TryParse(parts[0], out int note))
                {
                    continue;
                }

                int layer = 0;
                bool release = parts[1] == RELEASE_NAME;
                if (!release && !int.TryParse(parts[1], out layer))
                {
                    continue;
                }

                var mono = Decode(path, out int frequency);
                if (mono == null)
                {
                    continue;
                }

                if (release)
                {
                    releases.Add((note, mono, frequency));
                }
                else
                {
                    notes.Add((note, layer, mono, frequency));
                }
            }

            return BassSynthGuitar.Create(notes, releases, format);
        }

        internal static float[]? LoadCabinet(out int frequency)
        {
            frequency = 0;
            string folder = Path.Combine(PathHelper.StreamingAssetsPath, CABINET_FOLDER);
            if (!Directory.Exists(folder))
            {
                return null;
            }

            var paths = Directory.GetFiles(folder, "*.wav");
            if (paths.Length == 0)
            {
                return null;
            }

            Array.Sort(paths, StringComparer.Ordinal);
            return Decode(paths[0], out frequency);
        }

        private static float[]? Decode(string path, out int frequency)
        {
            frequency = 0;
            int handle = Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float);
            if (handle == 0)
            {
                YargLogger.LogFormatError("Failed to open guitar sample {0}: {1}", path, Bass.LastError);
                return null;
            }

            try
            {
                Bass.ChannelGetInfo(handle, out ChannelInfo info);
                frequency = info.Frequency;
                int channels = Math.Max(1, info.Channels);
                var mono = new float[(int) (MAX_SECONDS * frequency)];
                var buffer = new float[CHUNK_FRAMES * channels];
                int written = 0;
                while (written < mono.Length)
                {
                    int bytes = Bass.ChannelGetData(handle, buffer, buffer.Length * FLOAT_BYTES | (int) DataFlags.Float);
                    if (bytes <= 0)
                    {
                        break;
                    }

                    int frames = Math.Min(bytes / FLOAT_BYTES / channels, mono.Length - written);
                    for (int frame = 0; frame < frames; frame++)
                    {
                        float sum = 0;
                        for (int channel = 0; channel < channels; channel++)
                        {
                            sum += buffer[frame * channels + channel];
                        }

                        mono[written + frame] = sum / channels;
                    }

                    written += frames;
                }

                Array.Resize(ref mono, written);
                return mono;
            }
            finally
            {
                Bass.StreamFree(handle);
            }
        }
    }
}
