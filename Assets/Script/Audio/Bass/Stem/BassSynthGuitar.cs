#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Audio.BASS
{
    internal sealed class BassSynthGuitar
    {
        internal const int LOWEST_NOTE  = 50;
        internal const int HIGHEST_NOTE = 86;

        private const string FOLDER               = "guitarSamples";
        private const string RELEASE_NAME         = "release";
        private const double MAX_DECODE_SECONDS   = 5.5;
        private const int    FLOAT_BYTES          = sizeof(float);
        private const int    CHUNK_FRAMES         = 4096;

        private const double SAMPLE_SECONDS       = 2.5;
        private const double BODY_START_SECONDS   = 0.05;
        private const double BODY_END_SECONDS     = 0.4;
        private const double DI_LEVEL             = 0.1;
        private const double RELEASE_LEVEL        = 0.5;
        private const double ATTACK_SECONDS       = 0.025;
        private const double LOWER_SAMPLE_PENALTY = 0.1;

        private sealed class Sample
        {
            public readonly int Note;
            public readonly SortedDictionary<int, List<(BassStemClip Clip, float Gain)>> Layers = new();
            public readonly List<BassStemClip> Releases = new();

            public Sample(int note)
            {
                Note = note;
            }

            public float MiddleGain => MiddleTakes?[0].Gain ?? 0;

            public List<(BassStemClip Clip, float Gain)>? MiddleTakes
            {
                get
                {
                    int middle = Layers.Count / 2;
                    foreach (var takes in Layers.Values)
                    {
                        if (middle-- == 0)
                        {
                            return takes;
                        }
                    }

                    return null;
                }
            }
        }

        private readonly List<Sample> _samples;

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

            return Create(notes, releases, format);
        }

        private BassSynthGuitar(List<Sample> samples)
        {
            _samples = samples;
        }

        private static BassSynthGuitar? Create(IReadOnlyList<(int Note, int Layer, float[] Mono, int Frequency)> recordings,
            IReadOnlyList<(int Note, float[] Mono, int Frequency)> releases, BassStemFormat format)
        {
            var samples = new List<Sample>();
            foreach (var (note, layer, mono, frequency) in recordings)
            {
                var clip = Clip(note, mono, frequency, format, out float gain);
                if (clip == null)
                {
                    continue;
                }

                var sample = samples.Find(candidate => candidate.Note == note);
                if (sample == null)
                {
                    sample = new Sample(note);
                    samples.Add(sample);
                }

                if (!sample.Layers.TryGetValue(layer, out var takes))
                {
                    takes = new List<(BassStemClip Clip, float Gain)>();
                    sample.Layers[layer] = takes;
                }

                takes.Add((clip, gain));
            }

            foreach (var (note, mono, frequency) in releases)
            {
                var sample = samples.Find(candidate => candidate.Note == note);
                if (sample != null)
                {
                    sample.Releases.Add(Release(note, mono, frequency, (float) (sample.MiddleGain * RELEASE_LEVEL), format));
                }
            }

            samples.Sort((left, right) => left.Note.CompareTo(right.Note));
            return samples.Count > 0 ? new BassSynthGuitar(samples) : null;
        }

        internal List<BassStemClip> Ladder(double tuningCents)
        {
            var ladder = new List<BassStemClip>(HIGHEST_NOTE - LOWEST_NOTE + 1);
            for (int note = LOWEST_NOTE; note <= HIGHEST_NOTE; note++)
            {
                var sample = Nearest(note);
                double rate = Math.Pow(2, (note - sample.Note + tuningCents / 100) / 12);
                var layers = new BassStemClip[sample.Layers.Count];
                int index = 0;
                foreach (var takes in sample.Layers.Values)
                {
                    layers[index++] = BuildLayer(takes, note, rate);
                }

                var releases = new BassStemClip[sample.Releases.Count];
                for (int release = 0; release < releases.Length; release++)
                {
                    releases[release] = sample.Releases[release].TransposedTo(note, rate);
                }

                foreach (var layer in layers)
                {
                    layer.Layers = layers;
                    layer.Releases = releases;
                    foreach (var take in layer.Takes)
                    {
                        take.Layers = layers;
                        take.Releases = releases;
                    }
                }

                ladder.Add(layers[layers.Length / 2]);
            }

            return ladder;
        }

        internal float[] DiNote(double hz, int frames, BassStemFormat format)
        {
            int note = (int) Math.Round(69 + 12 * Math.Log(hz / 440, 2));
            var sample = Nearest(Math.Clamp(note, LOWEST_NOTE, HIGHEST_NOTE));
            var clip = sample.MiddleTakes![0].Clip;
            double rate = hz / NoteHz(sample.Note);
            int sourceFrames = Math.Min(clip.FrameCount, (int) Math.Ceiling(frames * rate) + 4);
            var mono = new float[sourceFrames];
            for (int frame = 0; frame < sourceFrames; frame++)
            {
                mono[frame] = clip.Samples[frame * format.Channels];
            }

            var played = Resample(mono, (int) Math.Round(format.Frequency * rate), format.Frequency, frames);
            Array.Resize(ref played, frames);
            return played;
        }

        private static BassStemClip BuildLayer(List<(BassStemClip Clip, float Gain)> takes, int note, double rate)
        {
            var clip = takes[0].Clip.TransposedTo(note, rate);
            clip.Takes = new BassStemClip[takes.Count - 1];
            for (int take = 1; take < takes.Count; take++)
            {
                clip.Takes[take - 1] = takes[take].Clip.TransposedTo(note, rate);
            }

            return clip;
        }

        private Sample Nearest(int note)
        {
            var nearest = _samples[0];
            double nearestDistance = double.MaxValue;
            foreach (var sample in _samples)
            {
                double distance = Math.Abs(sample.Note - note) + (sample.Note < note ? LOWER_SAMPLE_PENALTY : 0);
                if (distance < nearestDistance)
                {
                    nearest = sample;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private static BassStemClip? Clip(int note, float[] mono, int frequency, BassStemFormat format, out float gain)
        {
            gain = 0;
            var resampled = Resample(mono, frequency, format.Frequency, format.FramesFor(SAMPLE_SECONDS));
            int bodyStart = format.FramesFor(BODY_START_SECONDS);
            int bodyEnd = Math.Min(format.FramesFor(BODY_END_SECONDS), resampled.Length);
            if (bodyEnd <= bodyStart)
            {
                return null;
            }

            double energy = 0;
            for (int frame = bodyStart; frame < bodyEnd; frame++)
            {
                energy += (double) resampled[frame] * resampled[frame];
            }

            double level = Math.Sqrt(energy / (bodyEnd - bodyStart));
            if (level <= 0)
            {
                return null;
            }

            gain = (float) (DI_LEVEL / level);
            var clip = new BassStemClip
            {
                Samples = Interleaved(resampled, gain, format.Channels),
                FrameCount = resampled.Length,
                IsSustain = true,
                AttackEndFrame = format.FramesFor(ATTACK_SECONDS),
                Note = note,
            };
            clip.LoopStretchedBody(clip.AttackEndFrame, format);
            return clip;
        }

        private static BassStemClip Release(int note, float[] mono, int frequency, float gain, BassStemFormat format)
        {
            var resampled = Resample(mono, frequency, format.Frequency, int.MaxValue);
            return new BassStemClip
            {
                Samples = Interleaved(resampled, gain, format.Channels),
                FrameCount = resampled.Length,
                Note = note,
            };
        }

        internal static double NoteHz(int note)
        {
            return 440 * Math.Pow(2, (note - 69) / 12.0);
        }

        private static float[] Interleaved(float[] mono, float gain, int channels)
        {
            var samples = new float[mono.Length * channels];
            for (int frame = 0; frame < mono.Length; frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    samples[frame * channels + channel] = mono[frame] * gain;
                }
            }

            return samples;
        }

        private static float[] Resample(float[] source, int fromFrequency, int toFrequency, int maxFrames)
        {
            double step = fromFrequency / (double) toFrequency;
            int frames = Math.Min(maxFrames, (int) ((source.Length - 2) / step));
            var output = new float[Math.Max(0, frames)];
            for (int frame = 0; frame < output.Length; frame++)
            {
                double position = frame * step;
                int index = (int) position;
                float fraction = (float) (position - index);
                float y0 = source[Math.Max(index - 1, 0)];
                float y1 = source[index];
                float y2 = source[index + 1];
                float y3 = source[Math.Min(index + 2, source.Length - 1)];
                float c1 = 0.5f * (y2 - y0);
                float c2 = y0 - 2.5f * y1 + 2 * y2 - 0.5f * y3;
                float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
                output[frame] = ((c3 * fraction + c2) * fraction + c1) * fraction + y1;
            }

            return output;
        }

        internal static float[]? Decode(string path, out int frequency)
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
                var mono = new float[(int) (MAX_DECODE_SECONDS * frequency)];
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
