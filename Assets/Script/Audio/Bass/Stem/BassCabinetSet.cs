#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// The cabinet impulse responses the lead amp matches against.
    /// </summary>
    internal sealed class BassCabinetSet
    {
        internal const int MAX_CABINETS = 8;

        private const string FOLDER = "guitarCabinet";

        public readonly float[]  Samples;
        public readonly uint[]   Frames;
        public readonly float[]  Rates;
        public readonly string[] Names;

        public int Count => Names.Length;

        internal static BassCabinetSet? Load()
        {
            string folder = Path.Combine(PathHelper.StreamingAssetsPath, FOLDER);
            if (!Directory.Exists(folder))
            {
                return null;
            }

            var paths = Directory.GetFiles(folder, "*.wav");
            Array.Sort(paths, StringComparer.Ordinal);
            var samples = new List<float>();
            var frames = new List<uint>();
            var rates = new List<float>();
            var names = new List<string>();
            foreach (string path in paths)
            {
                if (names.Count == MAX_CABINETS)
                {
                    YargLogger.LogFormatWarning("Ignoring guitar cabinets past the first {0}", MAX_CABINETS);
                    break;
                }

                var mono = BassSynthGuitar.Decode(path, out int frequency);
                if (mono == null || mono.Length == 0)
                {
                    continue;
                }

                samples.AddRange(mono);
                frames.Add((uint) mono.Length);
                rates.Add(frequency);
                names.Add(Path.GetFileNameWithoutExtension(path));
            }

            return names.Count > 0
                ? new BassCabinetSet(samples.ToArray(), frames.ToArray(), rates.ToArray(), names.ToArray())
                : null;
        }

        private BassCabinetSet(float[] samples, uint[] frames, float[] rates, string[] names)
        {
            Samples = samples;
            Frames = frames;
            Rates = rates;
            Names = names;
        }
    }
}
