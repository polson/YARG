#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using YARG.Audio.BASS.Effects;
using YARG.Core.Chart;
using YARG.Core.Logging;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Turns the engine's solo phrases into the phrases the stem soundboard plays, fitting the
    /// synth guitar's tone, space and level to the recorded solo.
    /// </summary>
    internal sealed class BassSoloPhraseLibrary
    {
        private const double REGION_PREROLL_SECONDS    = 0.1;
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
        private const double MAX_SPACE_SECONDS         = 40;
        private const double MAX_ANALYSIS_SECONDS      = 3;

        private const double DECAY_DEFAULT_DB_PER_SEC  = 3;
        private const double DECAY_MIN_DB_PER_SEC      = 1;
        private const double DECAY_MAX_DB_PER_SEC      = 10;

        private sealed class RenderJob
        {
            public readonly BassSoloPhrase Phrase;
            public readonly List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> Notes;
            public readonly List<(BassStemClip Clip, int Key)> Clips = new();
            public          float[]                            Region = Array.Empty<float>();
            public          long                               RegionStartFrame;

            public RenderJob(BassSoloPhrase phrase,
                List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)> notes)
            {
                Phrase = phrase;
                Notes = notes;
            }
        }

        // Lock order: the replay lock or a soundboard lock is always taken before this one.
        private readonly object           _lock = new();
        private readonly BassStemFormat   _format;
        private          List<RenderJob>  _requests  = new();
        private          List<RenderJob>  _pending   = new();
        private readonly List<BassSoloPhrase> _published = new();
        private readonly BassChordTrack?  _chords;
        private          double           _requestedSoloTime;
        private readonly Func<BassSynthGuitar?>? _loadSynth;
        private          BassSynthGuitar? _synth;
        private readonly BassCabinetSet?  _cabinets;

        public BassSoloPhraseLibrary(BassStemFormat format, BassChordTrack? chords, Func<BassSynthGuitar?>? loadSynth,
            BassCabinetSet? cabinets)
        {
            _format = format;
            _chords = chords;
            _loadSynth = loadSynth;
            _cabinets = cabinets;
        }

        public void Request(List<GuitarNote> notes, Func<double, double> playbackTime)
        {
            var phrases = SoloPhrases(notes);
            var requests = new List<RenderJob>(phrases.Count);
            foreach (var phrase in phrases)
            {
                var phraseNotes = new List<(int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain)>();
                foreach (var note in notes)
                {
                    if (note.Tick < phrase.StartTick || note.Tick > phrase.EndTick)
                    {
                        continue;
                    }

                    phraseNotes.Add((note.NoteMask, note.Time, playbackTime(note.Time),
                        ClipDuration(note), HasSustain(note)));
                }

                if (phraseNotes.Count == 0)
                {
                    continue;
                }

                var solo = new BassSoloPhrase(phraseNotes[0].SongSeconds, phraseNotes[^1].SongSeconds);
                requests.Add(new RenderJob(solo, phraseNotes));
            }

            lock (_lock)
            {
                _requests = requests;
            }

            _chords?.Request(requests.ConvertAll(request => (request.Phrase.StartTime, request.Phrase.EndTime)), playbackTime);
        }

        private static List<(uint StartTick, uint EndTick)> SoloPhrases(List<GuitarNote> notes)
        {
            var phrases = new List<(uint StartTick, uint EndTick)>();
            int start = -1;
            for (int index = 0; index < notes.Count; index++)
            {
                var note = notes[index];
                if (!note.IsSolo)
                {
                    start = -1;
                    continue;
                }

                if (start < 0)
                {
                    start = index;
                }

                bool ends = note.IsSoloEnd || index + 1 == notes.Count || !notes[index + 1].IsSolo;
                if (ends)
                {
                    phrases.Add((notes[start].Tick, note.Tick));
                    start = -1;
                }
            }

            return phrases;
        }

        public BassSoloPhrase? Select(double soloTime, double songTime)
        {
            lock (_lock)
            {
                _requestedSoloTime = soloTime;
                var phrase = Find(soloTime);
                phrase?.Select(songTime, _chords);
                return phrase;
            }
        }

        public void Forget()
        {
            lock (_lock)
            {
                _requests = new List<RenderJob>();
                _pending = new List<RenderJob>();
                _published.Clear();
            }
        }

        public void PrepareJobs(List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            lock (_lock)
            {
                _pending = BuildJobs(_requests, jobs);
            }
        }

        public void Publish(CancellationToken token)
        {
            List<RenderJob> pending;
            lock (_lock)
            {
                pending = _pending;
                _pending = new List<RenderJob>();
                _published.Clear();
            }

            if (_loadSynth != null && _synth == null)
            {
                var stopwatch = Stopwatch.StartNew();
                _synth = _loadSynth();
                YargLogger.LogFormatInfo("[SoloSynth] guitar samples {0} in {1} ms",
                    _synth != null ? "loaded" : "failed to load", stopwatch.ElapsedMilliseconds);
            }

            if (_synth == null)
            {
                return;
            }

            while (pending.Count > 0)
            {
                double requestedSoloTime;
                lock (_lock)
                {
                    requestedSoloTime = _requestedSoloTime;
                }

                var job = TakeNext(pending, requestedSoloTime);
                if (!Prepare(job, token))
                {
                    return;
                }

                lock (_lock)
                {
                    _published.Add(job.Phrase);
                }
            }
        }

        private List<RenderJob> BuildJobs(List<RenderJob> requests,
            List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            foreach (var job in requests)
            {
                foreach (var note in job.Notes)
                {
                    AddNoteWindow(job, note, jobs);
                }

                AddRegionJob(job, jobs, _format.FramesFor(MAX_SPACE_SECONDS));
            }

            return requests;
        }

        private void AddNoteWindow(RenderJob job,
            (int Key, double SongSeconds, double PlaybackSeconds, double DurationSeconds, bool IsSustain) note,
            List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            long chartFrame = (long) Math.Round(note.PlaybackSeconds * _format.Frequency);
            int frameCount = _format.FramesFor(Math.Min(note.DurationSeconds, MAX_ANALYSIS_SECONDS));
            if (chartFrame < 0 || frameCount <= 0)
            {
                return;
            }

            int leadFrames = (int) Math.Min(_format.FramesFor(BassStemClip.ONSET_LEAD_SECONDS), chartFrame);
            long startFrame = chartFrame - leadFrames;
            frameCount += leadFrames;

            var clip = new BassStemClip
            {
                SongSeconds = note.SongSeconds,
                StartFrame = startFrame,
                Samples = new float[frameCount * _format.Channels],
                FrameCount = frameCount,
                IsSustain = note.IsSustain,
                ChartFrame = leadFrames,
            };
            job.Clips.Add((clip, note.Key));
            jobs.Add((startFrame, frameCount, clip.Samples));
        }

        private void AddRegionJob(RenderJob job, List<(long StartFrame, int FrameCount, float[] Samples)> jobs,
            int maxFrames)
        {
            long firstFrame = long.MaxValue;
            long endFrame = 0;
            foreach (var (clip, _) in job.Clips)
            {
                firstFrame = Math.Min(firstFrame, clip.StartFrame);
                endFrame = Math.Max(endFrame, clip.StartFrame + clip.FrameCount);
            }

            if (endFrame <= firstFrame)
            {
                return;
            }

            job.RegionStartFrame = Math.Max(0, firstFrame - _format.FramesFor(REGION_PREROLL_SECONDS));
            int frameCount = (int) Math.Min(endFrame - job.RegionStartFrame, maxFrames);
            job.Region = new float[frameCount * _format.Channels];
            jobs.Add((job.RegionStartFrame, frameCount, job.Region));
        }

        private bool Prepare(RenderJob job, CancellationToken token)
        {
            var clips = new List<(BassStemClip Clip, bool SingleNote, int Fret)>(job.Clips.Count);
            foreach (var (clip, key) in job.Clips)
            {
                bool singleNote = (key & (key - 1)) == 0;
                int fret = singleNote ? ChartFret(key) : -1;
                clips.Add((clip, singleNote, fret));
            }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            };
            var pitches = new (int Note, double Hz, float Voiced, double Spread, double Seconds, bool Bent)?[clips.Count];
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
                return false;
            }

            PrepareSynth(job, clips, pitches, levels, decays);
            return true;
        }

        private void PrepareSynth(RenderJob job, List<(BassStemClip Clip, bool SingleNote, int Fret)> clips,
            (int Note, double Hz, float Voiced, double Spread, double Seconds, bool Bent)?[] pitches, double[] levels,
            double[] decays)
        {
            var phrase = job.Phrase;
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
                if (pitches[index] is not { } pitch || pitch.Bent || pitch.Voiced < ANCHOR_MIN_VOICED
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

            tunings.Sort();
            double tuningCents = tunings.Count > 0 ? tunings[tunings.Count / 2] : 0;
            phrase.Ladder.AddRange(_synth!.Ladder(tuningCents));
            SetWindows(phrase, anchors);

            var heard = Array.FindAll(levels, level => level > 0);
            Array.Sort(heard);
            double soloLevel = heard.Length > 0 ? heard[heard.Length / 2] : DEFAULT_SOLO_LEVEL;
            phrase.Tone = MatchSpace(job, tone);
            LogTone(phrase.Tone);
            double stemGain = phrase.Tone.StemGain > 0 ? phrase.Tone.StemGain : soloLevel / SYNTH_NOTE_LEVEL;
            phrase.SynthGain = stemGain * phrase.Tone.LevelGain * phrase.Tone.ReverbLevelGain;
            SetDecay(phrase, TypicalDecay(decays));
            int singles = clips.FindAll(clip => clip.SingleNote).Count;
            YargLogger.LogFormatInfo(
                "[SoloLevel] {0} notes ({1} single, {2} chord), stem gain {3}, tone gain {4}, reverb gain {5}, synth gain {6}, solo level {7}",
                clips.Count, singles, clips.Count - singles, Math.Round(phrase.Tone.StemGain, 2),
                Math.Round(phrase.Tone.LevelGain, 2), Math.Round(phrase.Tone.ReverbLevelGain, 2),
                Math.Round(phrase.SynthGain, 4), Math.Round(soloLevel, 4));
            YargLogger.LogFormatInfo("[SoloSynth] {0} measured pitches, {1} chord anchors, tuning {2} cents, solo level {3}",
                measured, anchors.Count - measured, Math.Round(tuningCents), Math.Round(soloLevel, 4));
        }

        private BassLeadAmpTone? MatchTone(List<(BassStemClip Clip, bool SingleNote, int Fret)> clips,
            (int Note, double Hz, float Voiced, double Spread, double Seconds, bool Bent)?[] pitches)
        {
            int minFrames = _format.FramesFor(TONE_MIN_NOTE_SECONDS);
            var picked = new List<(BassStemClip Clip, double Hz)>();
            for (int index = 0; index < clips.Count; index++)
            {
                var clip = clips[index].Clip;
                if (pitches[index] is not { } pitch || pitch.Bent || pitch.Voiced < PITCH_MIN_VOICED
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

            int channels = _format.Channels;
            int maxFrames = _format.FramesFor(TONE_MAX_NOTE_SECONDS);
            var stem = new List<float>();
            var di = new List<float>();
            var noteFrames = new List<uint>();
            foreach (var (clip, hz) in picked)
            {
                int frames = Math.Min(clip.FrameCount, maxFrames);
                for (int frame = 0; frame < frames; frame++)
                {
                    float sum = 0;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        sum += clip.Samples[frame * channels + channel];
                    }

                    stem.Add(sum / channels);
                }

                di.AddRange(_synth!.DiNote(hz, frames, _format));
                noteFrames.Add((uint) frames);
            }

            var tone = BassLeadAmpDsp.Match(_format.Frequency, _cabinets, stem.ToArray(), di.ToArray(),
                noteFrames.ToArray());
            YargLogger.LogFormatInfo("[SoloTone] {0} on {1} notes", tone != null ? "matched" : "skipped", picked.Count);
            return tone;
        }

        private BassLeadAmpTone MatchSpace(RenderJob job, BassLeadAmpTone tone)
        {
            if (job.Region.Length == 0)
            {
                return BassLeadAmpDsp.MatchSpace(tone, _format.Frequency, null, Array.Empty<double>());
            }

            int channels = _format.Channels;
            var region = new float[job.Region.Length / channels];
            for (int frame = 0; frame < region.Length; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += job.Region[frame * channels + channel];
                }

                region[frame] = sum / channels;
            }

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
            if (_chords == null)
            {
                return;
            }

            foreach (var (clip, singleNote, fret) in clips)
            {
                int root = singleNote ? _chords.RootAt(clip.SongSeconds) : -1;
                if (root >= 0)
                {
                    anchors.Add((clip.SongSeconds, CHORD_ANCHOR_NOTE + root, fret));
                }
            }
        }

        private static void SetDecay(BassSoloPhrase phrase, double decay)
        {
            foreach (var clip in phrase.Ladder)
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

            if (rates.Count == 0)
            {
                return DECAY_DEFAULT_DB_PER_SEC;
            }

            rates.Sort();
            return Math.Clamp(rates[rates.Count / 2], DECAY_MIN_DB_PER_SEC, DECAY_MAX_DB_PER_SEC);
        }

        private (int Note, double Hz, float Voiced, double Spread, double Seconds, bool Bent)? Measure(BassStemClip clip, BassSoloPitch detector,
            List<float> readings)
        {
            int channels = _format.Channels;
            int attackFrames = clip.AttackEndFrame;
            int bodyFrames = clip.FrameCount - attackFrames;
            var mono = new float[Math.Min(bodyFrames, _format.FramesFor(PITCH_MAX_MEASURE_SECONDS))];
            for (int frame = 0; frame < mono.Length; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += clip.Samples[(attackFrames + frame) * channels + channel];
                }

                mono[frame] = sum / channels;
            }

            detector.Detect(mono, readings);

            var voiced = readings.FindAll(pitch => pitch > 0f);
            if (voiced.Count == 0)
            {
                return null;
            }

            voiced.Sort();
            float median = voiced[voiced.Count / 2];
            int heldReadings = HeldReadings(readings, median);
            int agreeing = 0;
            double spread = 0;
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
                    spread += cents;
                }
            }

            if (agreeing == 0)
            {
                return null;
            }

            int heldFrames = heldReadings < readings.Count ? heldReadings * detector.HopFrames : bodyFrames;
            int note = (int) Math.Round(69 + 12 * Math.Log(median / 440.0, 2));
            return (note, median, (float) agreeing / heldReadings, spread / agreeing, (double) heldFrames / _format.Frequency,
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

        private static void SetWindows(BassSoloPhrase phrase, List<(double SongSeconds, int Note, int Fret)> anchors)
        {
            anchors.Sort((left, right) => left.SongSeconds.CompareTo(right.SongSeconds));
            foreach (var anchor in anchors)
            {
                phrase.Anchors.Add((anchor.SongSeconds, anchor.Note));
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

                neighbours.Sort();
                if (Math.Abs(anchor.Note - neighbours[neighbours.Count / 2]) >= ANCHOR_OUTLIER_SEMITONES)
                {
                    continue;
                }

                if (phrase.Windows.Count > 0 && anchor.SongSeconds - phrase.Windows[^1].SongSeconds < MIN_WINDOW_SECONDS)
                {
                    continue;
                }

                int fret = anchor.Fret >= 0 ? anchor.Fret : BassSoloPhrase.FRET_COUNT / 2;
                if (phrase.Windows.Count > 0 && phrase.Windows[^1].Note == anchor.Note && phrase.Windows[^1].Fret == fret)
                {
                    continue;
                }

                phrase.Windows.Add((anchor.SongSeconds, anchor.Note, fret));
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

        private static int ChartFret(int key)
        {
            for (int fret = 0; fret < BassSoloPhrase.FRET_COUNT; fret++)
            {
                if (key == 1 << fret)
                {
                    return fret;
                }
            }

            return -1;
        }

        private BassSoloPhrase? Find(double soloTime)
        {
            foreach (var phrase in _published)
            {
                if (phrase.Covers(soloTime))
                {
                    return phrase;
                }
            }

            return null;
        }

        /// <summary>
        /// The two derived properties of a chart note that the stem layer needs: how long its clip runs
        /// and whether it holds.
        /// </summary>
        private static double ClipDuration(GuitarNote note)
        {
            return note.NextNote != null ? note.NextNote.Time - note.Time : double.MaxValue;
        }

        private static bool HasSustain(GuitarNote note)
        {
            foreach (var child in note.AllNotes)
            {
                if (child.IsSustain)
                {
                    return true;
                }
            }

            return false;
        }

        private static RenderJob TakeNext(List<RenderJob> queue, double soloTime)
        {
            int index = Math.Max(queue.FindIndex(job => job.Phrase.Covers(soloTime)), 0);
            var job = queue[index];
            queue.RemoveAt(index);
            return job;
        }
    }
}
