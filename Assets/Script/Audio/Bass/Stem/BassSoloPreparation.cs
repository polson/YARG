#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using YARG.Audio.BASS.Effects;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Measures each solo job and fits the synth guitar's tone, space and level to the recorded solo.
    /// </summary>
    internal sealed class BassSoloPreparation
    {
        private const double PITCH_MAX_MEASURE_SECONDS = 0.4;
        private const double LEVEL_SECONDS             = 0.3;
        private const float  PITCH_MIN_VOICED          = 0.8f;
        private const float  ANCHOR_MIN_VOICED         = 0.6f;
        private const float  PITCH_AGREE_CENTS         = 35f;
        private const int    ANCHOR_NEIGHBOURS         = 2;
        private const int    ANCHOR_OUTLIER_SEMITONES  = 10;
        private const double MIN_WINDOW_SECONDS        = 0.3;
        private const double DEPARTURE_CENTS           = 80;
        private const int    DEPARTURE_READINGS        = 3;
        private const double BEND_CHECK_SECONDS        = 0.15;
        private const float  BEND_MIN_CENTS            = 30f;
        private const float  BEND_MAX_CENTS            = 400f;
        private const int    MIN_SYNTH_ANCHORS         = 8;
        private const int    CHORD_ANCHOR_NOTE         = 60;
        private const double SYNTH_NOTE_LEVEL          = 1.32;
        private const double DEFAULT_SOLO_LEVEL        = 0.1;
        private const double TONE_MIN_NOTE_SECONDS     = 0.1;
        private const double TONE_MAX_NOTE_SECONDS     = 0.35;
        private const int    MAX_TONE_NOTES            = 8;

        private const double DECAY_DEFAULT_DB_PER_SEC  = 3;
        private const double DECAY_MIN_DB_PER_SEC      = 1;
        private const double DECAY_MAX_DB_PER_SEC      = 10;

        /// <summary>
        /// The pitch measurement of one solo note: its MIDI note, frequency, how much of the clip is
        /// voiced and whether it starts with a bend.
        /// </summary>
        private readonly struct PitchReading
        {
            public readonly int    Note;
            public readonly double Hz;
            public readonly float  Voiced;
            public readonly bool   Bent;

            public PitchReading(int note, double hz, float voiced, bool bent)
            {
                Note = note;
                Hz = hz;
                Voiced = voiced;
                Bent = bent;
            }
        }

        private readonly BassStemFormat   _format;
        private readonly BassChordTrack?  _backingChords;
        private          BassSynthGuitar? _synth;
        private readonly BassCabinetSet?  _cabinets;

        public BassSoloPreparation(BassStemFormat format, BassChordTrack? backingChords, BassCabinetSet? cabinets)
        {
            _format = format;
            _backingChords = backingChords;
            _cabinets = cabinets;
        }

        public bool TryLoadSynth()
        {
            if (_synth == null)
            {
                var stopwatch = Stopwatch.StartNew();
                _synth = BassSynthGuitar.Load(_format);
                YargLogger.LogFormatInfo("[SoloSynth] guitar samples {0} in {1} ms",
                    _synth != null ? "loaded" : "failed to load", stopwatch.ElapsedMilliseconds);
            }

            return _synth != null;
        }

        internal BassSoloPhrase? Prepare(BassSoloPhraseLibrary.RenderJob job, CancellationToken token)
        {
            var clips = new List<(BassStemClip Clip, bool SingleNote, int Fret)>(job.Clips.Count);
            foreach (var (clip, key) in job.Clips)
            {
                bool singleNote = (key & (key - 1)) == 0;
                int fret = singleNote ? BassSoloPhrase.FretIndex(key) : -1;
                clips.Add((clip, singleNote, fret));
            }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            };
            var pitches = new PitchReading?[clips.Count];
            var levels = new double[clips.Count];
            var decays = new double[clips.Count];
            Parallel.For(0, clips.Count, options,
                () => (Pitch: new BassSoloPitch(_format.Frequency), Readings: new List<float>()),
                (index, state, local) =>
                {
                    if (token.IsCancellationRequested)
                    {
                        state.Stop();
                        return local;
                    }

                    var (clip, singleNote, _) = clips[index];
                    clip.AlignToOnset(_format);
                    clip.TrimToCleanEnd(_format);
                    clip.AttackEndFrame = clip.MeasureAttackEnd(_format);
                    decays[index] = clip.MeasureDecay(_format);
                    levels[index] = Level(clip);
                    if (singleNote)
                    {
                        pitches[index] = Measure(clip, local.Pitch, local.Readings);
                    }

                    return local;
                },
                _ => { });
            if (token.IsCancellationRequested)
            {
                return null;
            }

            return PrepareSynth(job, clips, pitches, levels, decays);
        }

        private BassSoloPhrase PrepareSynth(BassSoloPhraseLibrary.RenderJob job, List<(BassStemClip Clip, bool SingleNote, int Fret)> clips,
            PitchReading?[] pitches, double[] levels, double[] decays)
        {
            var tone = MatchTone(clips, pitches);
            if (tone == null)
            {
                YargLogger.LogInfo("[SoloSynth] no notes usable for a tone match, using the default amp tone");
                tone = BassLeadAmpTone.Default;
            }

            var anchors = new List<(double SongSeconds, int Note, int Fret)>();
            var tunings = new List<double>();
            for (int index = 0; index < clips.Count; index++)
            {
                if (pitches[index] is not { } pitch)
                {
                    continue;
                }

                if (pitch.Bent || pitch.Voiced < ANCHOR_MIN_VOICED
                    || pitch.Note < BassSynthGuitar.LOWEST_NOTE || pitch.Note > BassSynthGuitar.HIGHEST_NOTE)
                {
                    continue;
                }

                anchors.Add((clips[index].Clip.SongSeconds, pitch.Note, clips[index].Fret));
                tunings.Add(1200 * Math.Log(pitch.Hz / BassSynthGuitar.NoteHz(pitch.Note), 2));
            }

            int measured = anchors.Count;
            if (measured < MIN_SYNTH_ANCHORS)
            {
                AddChordAnchors(clips, anchors);
            }

            double tuningCents = tunings.SortedMedian(fallback: 0);
            var ladder = _synth!.Ladder(tuningCents);
            var windows = new List<(double SongSeconds, int Note, int Fret)>();
            var anchorNotes = new List<(double SongSeconds, int Note)>();
            SetWindows(anchors, anchorNotes, windows);

            var heard = new List<double>(Array.FindAll(levels, level => level > 0));
            double soloLevel = heard.SortedMedian(fallback: DEFAULT_SOLO_LEVEL);
            tone = MatchSpace(job, tone);
            LogTone(tone);
            double stemGain = tone.StemGain > 0 ? tone.StemGain : soloLevel / SYNTH_NOTE_LEVEL;
            double synthGain = stemGain * tone.LevelGain * tone.ReverbLevelGain;
            SetDecay(ladder, TypicalDecay(decays));
            var phrase = new BassSoloPhrase(job.StartTime, job.EndTime, ladder, windows, anchorNotes, tone, synthGain);
            int singles = clips.FindAll(clip => clip.SingleNote).Count;
            YargLogger.LogFormatInfo(
                "[SoloLevel] {0} notes ({1} single, {2} chord), stem gain {3}, tone gain {4}, reverb gain {5}, synth gain {6}, solo level {7}",
                clips.Count, singles, clips.Count - singles, Math.Round(tone.StemGain, 2),
                Math.Round(tone.LevelGain, 2), Math.Round(tone.ReverbLevelGain, 2),
                Math.Round(synthGain, 4), Math.Round(soloLevel, 4));
            YargLogger.LogFormatInfo("[SoloSynth] {0} measured pitches, {1} chord anchors, tuning {2} cents, solo level {3}",
                measured, anchors.Count - measured, Math.Round(tuningCents), Math.Round(soloLevel, 4));
            return phrase;
        }

        private BassLeadAmpTone? MatchTone(List<(BassStemClip Clip, bool SingleNote, int Fret)> clips,
            PitchReading?[] pitches)
        {
            int minFrames = _format.FramesFor(TONE_MIN_NOTE_SECONDS);
            var picked = new List<(BassStemClip Clip, double Hz)>();
            for (int index = 0; index < clips.Count; index++)
            {
                var clip = clips[index].Clip;
                if (pitches[index] is not { } pitch)
                {
                    continue;
                }

                if (pitch.Bent || pitch.Voiced < PITCH_MIN_VOICED
                    || pitch.Note < BassSynthGuitar.LOWEST_NOTE || pitch.Note > BassSynthGuitar.HIGHEST_NOTE
                    || clip.FrameCount < minFrames)
                {
                    continue;
                }

                picked.Add((clip, pitch.Hz));
            }

            picked.Sort((left, right) => right.Clip.FrameCount.CompareTo(left.Clip.FrameCount));
            if (picked.Count > MAX_TONE_NOTES)
            {
                picked.RemoveRange(MAX_TONE_NOTES, picked.Count - MAX_TONE_NOTES);
            }

            int maxFrames = _format.FramesFor(TONE_MAX_NOTE_SECONDS);
            var stem = new List<float>();
            var di = new List<float>();
            var noteFrames = new List<uint>();
            foreach (var (clip, hz) in picked)
            {
                int frames = Math.Min(clip.FrameCount, maxFrames);
                stem.AddRange(_format.Mono(clip.Samples, firstFrame: 0, frameCount: frames));
                di.AddRange(_synth!.DiNote(hz, frames, _format));
                noteFrames.Add((uint) frames);
            }

            var tone = BassLeadAmpDsp.Match(_format.Frequency, _cabinets, stem.ToArray(), di.ToArray(),
                noteFrames.ToArray());
            YargLogger.LogFormatInfo("[SoloTone] {0} on {1} notes", tone != null ? "matched" : "skipped", picked.Count);
            return tone;
        }

        private BassLeadAmpTone MatchSpace(BassSoloPhraseLibrary.RenderJob job, BassLeadAmpTone tone)
        {
            if (job.Region.Length == 0)
            {
                return BassLeadAmpDsp.MatchSpace(tone, _format.Frequency, null, Array.Empty<double>());
            }

            var region = _format.Mono(job.Region, firstFrame: 0, frameCount: job.Region.Length / _format.Channels);

            var picks = new List<double>();
            foreach (var note in job.Notes)
            {
                long frame = (long) Math.Round(note.PlaybackSeconds * _format.Frequency) - job.RegionStartFrame;
                if (frame >= 0 && frame < region.Length)
                {
                    picks.Add(frame / (double) _format.Frequency);
                }
            }

            job.Region = Array.Empty<float>();
            return BassLeadAmpDsp.MatchSpace(tone, _format.Frequency, region, picks.ToArray());
        }

        private void LogTone(BassLeadAmpTone tone)
        {
            string cabinet = _cabinets != null && tone.Cabinet < _cabinets.Count ? _cabinets.Names[tone.Cabinet] : "none";
            YargLogger.LogFormatInfo(
                "[SoloTone] cabinet {0}, drive {1}, bands {2} dB, wah {3}, echo {4}, reverb wet {5} room {6}, level gain {7}, reverb level gain {8}",
                cabinet, Math.Round(tone.Drive, 2),
                string.Join(" ", Array.ConvertAll(tone.BandsDb, band => Math.Round(band, 1))),
                tone.HasWah ? $"{Math.Round(tone.WahStartHz)} to {Math.Round(tone.WahEndHz)} Hz" : "off",
                tone.EchoLevel > 0 ? $"{Math.Round(tone.EchoSeconds, 2)} s at {Math.Round(tone.EchoLevel, 2)}" : "off",
                Math.Round(tone.ReverbWet, 2), Math.Round(tone.ReverbRoom, 2), Math.Round(tone.LevelGain, 2),
                Math.Round(tone.ReverbLevelGain, 2));
        }

        private void AddChordAnchors(List<(BassStemClip Clip, bool SingleNote, int Fret)> clips,
            List<(double SongSeconds, int Note, int Fret)> anchors)
        {
            if (_backingChords == null)
            {
                return;
            }

            foreach (var (clip, singleNote, fret) in clips)
            {
                int root = singleNote ? _backingChords.RootAt(clip.SongSeconds) : -1;
                if (root >= 0)
                {
                    anchors.Add((clip.SongSeconds, CHORD_ANCHOR_NOTE + root, fret));
                }
            }
        }

        private static void SetDecay(List<BassStemClip> ladder, double decay)
        {
            foreach (var clip in ladder)
            {
                clip.DecayDbPerSecond = decay;
                foreach (var take in clip.Takes)
                {
                    take.DecayDbPerSecond = decay;
                }
            }
        }

        private static double TypicalDecay(IEnumerable<double> measured)
        {
            var rates = new List<double>();
            foreach (double rate in measured)
            {
                if (!double.IsNaN(rate))
                {
                    rates.Add(rate);
                }
            }

            double typical = rates.SortedMedian(fallback: DECAY_DEFAULT_DB_PER_SEC);
            return Math.Clamp(typical, DECAY_MIN_DB_PER_SEC, DECAY_MAX_DB_PER_SEC);
        }

        private PitchReading? Measure(BassStemClip clip, BassSoloPitch detector, List<float> readings)
        {
            int bodyFrames = clip.FrameCount - clip.AttackEndFrame;
            int measureFrames = Math.Min(bodyFrames, _format.FramesFor(PITCH_MAX_MEASURE_SECONDS));
            var mono = _format.Mono(clip.Samples, firstFrame: clip.AttackEndFrame, frameCount: measureFrames);
            detector.Detect(mono, readings);

            var voiced = readings.FindAll(pitch => pitch > 0f);
            if (voiced.Count == 0)
            {
                return null;
            }

            float median = voiced.SortedMedian(fallback: 0f);
            int heldReadings = HeldReadings(readings, median);
            int agreeing = 0;
            for (int index = 0; index < heldReadings; index++)
            {
                float pitch = readings[index];
                if (pitch <= 0f)
                {
                    continue;
                }

                double rawCents = 1200 * Math.Log(pitch / median, 2);
                double cents = Math.Abs(rawCents);
                if (cents <= PITCH_AGREE_CENTS)
                {
                    agreeing++;
                }
            }

            if (agreeing == 0)
            {
                return null;
            }

            return new PitchReading(BassSynthGuitar.NearestNote(median), median, (float) agreeing / heldReadings,
                StartsWithBend(readings, median, (double) mono.Length / _format.Frequency));
        }

        private static int HeldReadings(List<float> readings, float hz)
        {
            int departureRun = 0;
            for (int index = 0; index < readings.Count; index++)
            {
                if (Holds(readings[index], hz))
                {
                    departureRun = 0;
                    continue;
                }

                departureRun++;
                if (departureRun >= DEPARTURE_READINGS)
                {
                    return index - departureRun + 1;
                }
            }

            return readings.Count;
        }

        private static bool Holds(float reading, double hz)
        {
            if (reading <= 0f)
            {
                return false;
            }

            double rawCents = 1200 * Math.Log(reading / hz, 2);
            return Math.Abs(rawCents) <= DEPARTURE_CENTS;
        }

        private static bool StartsWithBend(List<float> readings, float settledHz, double measuredSeconds)
        {
            int startReadings = (int) Math.Ceiling(BEND_CHECK_SECONDS / measuredSeconds * readings.Count);
            for (int index = 0; index < Math.Min(startReadings, readings.Count); index++)
            {
                if (readings[index] <= 0f)
                {
                    continue;
                }

                double cents = Math.Abs(1200 * Math.Log(readings[index] / settledHz, 2));
                if (cents >= BEND_MIN_CENTS && cents <= BEND_MAX_CENTS)
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetWindows(List<(double SongSeconds, int Note, int Fret)> anchors,
            List<(double SongSeconds, int Note)> anchorNotes, List<(double SongSeconds, int Note, int Fret)> windows)
        {
            anchors.Sort((left, right) => left.SongSeconds.CompareTo(right.SongSeconds));
            foreach (var anchor in anchors)
            {
                anchorNotes.Add((anchor.SongSeconds, anchor.Note));
            }

            var neighbours = new List<int>(ANCHOR_NEIGHBOURS * 2 + 1);
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                var anchor = anchors[anchorIndex];
                neighbours.Clear();
                int first = Math.Max(0, anchorIndex - ANCHOR_NEIGHBOURS);
                int last = Math.Min(anchors.Count - 1, anchorIndex + ANCHOR_NEIGHBOURS);
                for (int neighbour = first; neighbour <= last; neighbour++)
                {
                    neighbours.Add(anchors[neighbour].Note);
                }

                int medianNote = neighbours.SortedMedian(fallback: 0);
                if (Math.Abs(anchor.Note - medianNote) >= ANCHOR_OUTLIER_SEMITONES)
                {
                    continue;
                }

                if (windows.Count > 0 && anchor.SongSeconds - windows[^1].SongSeconds < MIN_WINDOW_SECONDS)
                {
                    continue;
                }

                int fret = anchor.Fret >= 0 ? anchor.Fret : BassSoloPhrase.FRET_COUNT / 2;
                if (windows.Count > 0)
                {
                    var previous = windows[^1];
                    if (previous.Note == anchor.Note && previous.Fret == fret)
                    {
                        continue;
                    }
                }

                windows.Add((anchor.SongSeconds, anchor.Note, fret));
            }
        }

        private double Level(BassStemClip clip)
        {
            int channels = _format.Channels;
            int frames = Math.Min(clip.FrameCount, _format.FramesFor(LEVEL_SECONDS));
            double energy = 0;
            for (int sample = 0; sample < frames * channels; sample++)
            {
                energy += (double) clip.Samples[sample] * clip.Samples[sample];
            }

            return frames > 0 ? Math.Sqrt(energy / (frames * channels)) : 0;
        }
    }
}
