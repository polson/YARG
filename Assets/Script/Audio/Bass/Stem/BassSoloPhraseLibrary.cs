#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using YARG.Core.Chart;

namespace YARG.Audio.BASS
{
    /// <summary>
    /// Owns the thread-safe queue of solo phrases the stem soundboard selects from: the main thread
    /// requests them, the pre-render task prepares them, and selection reads the published set.
    /// </summary>
    internal sealed class BassSoloPhraseLibrary
    {
        private const double REGION_PREROLL_SECONDS = 0.1;
        private const double MAX_SPACE_SECONDS      = 40;
        private const double MAX_ANALYSIS_SECONDS   = 3;

        /// <summary>
        /// One chart note the solo preparation renders: its fret key, chart time, playback time, clip
        /// length and whether it holds.
        /// </summary>
        internal readonly struct SoloNote
        {
            public readonly int    Key;
            public readonly double SongSeconds;
            public readonly double PlaybackSeconds;
            public readonly double DurationSeconds;
            public readonly bool   IsSustain;

            public SoloNote(int key, double songSeconds, double playbackSeconds, double durationSeconds,
                bool isSustain)
            {
                Key = key;
                SongSeconds = songSeconds;
                PlaybackSeconds = playbackSeconds;
                DurationSeconds = durationSeconds;
                IsSustain = isSustain;
            }
        }

        /// <summary>
        /// The decode, analysis and publish work for one solo phrase.
        /// </summary>
        internal sealed class RenderJob
        {
            public readonly double                             StartTime;
            public readonly double                             EndTime;
            public readonly List<SoloNote>                     Notes;
            public readonly List<(BassStemClip Clip, int Key)> Clips = new();
            public          float[]                            Region = Array.Empty<float>();
            public          long                               RegionStartFrame;

            public RenderJob(List<SoloNote> notes)
            {
                Notes = notes;
                StartTime = notes[0].SongSeconds;
                EndTime = notes[^1].SongSeconds;
            }

            public bool Covers(double soloTime)
            {
                return soloTime >= StartTime && soloTime <= EndTime;
            }
        }

        // Lock order: the replay lock is taken before this one, and selection takes this one before
        // the backing chord track.
        private readonly object               _lock = new();
        private readonly BassStemFormat       _format;
        private readonly BassChordTrack?      _backingChords;
        private readonly BassSoloPreparation  _preparation;
        private          List<RenderJob>      _requests = new();
        private          List<RenderJob>      _pending = new();
        private readonly List<BassSoloPhrase> _published = new();
        private          double               _requestedSoloTime;

        public BassSoloPhraseLibrary(BassStemFormat format, BassChordTrack? backingChords, BassCabinetSet? cabinets)
        {
            _format = format;
            _backingChords = backingChords;
            _preparation = new BassSoloPreparation(format, backingChords, cabinets);
        }

        public void RequestPhrases(List<GuitarNote> notes, Func<double, double> playbackTime)
        {
            var requests = CreateRequests(notes, playbackTime);
            lock (_lock)
            {
                _requests = requests;
            }

            _backingChords?.RequestRegions(requests.ConvertAll(request => (request.StartTime, request.EndTime)),
                playbackTime);
        }

        public BassSoloPhrase? SelectPhrase(double soloTime, double songTime)
        {
            lock (_lock)
            {
                _requestedSoloTime = soloTime;
                var phrase = Find(soloTime);
                phrase?.SetPosition(songTime, _backingChords);
                return phrase;
            }
        }

        public void Clear()
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
                foreach (var request in _requests)
                {
                    foreach (var note in request.Notes)
                    {
                        AddNoteWindow(request, note, jobs);
                    }

                    AddRegionJob(request, jobs);
                }

                _pending = _requests;
            }
        }

        public void Publish(CancellationToken token)
        {
            List<RenderJob> pending;
            lock (_lock)
            {
                pending = _pending;
                _pending = new List<RenderJob>();
                _requests = new List<RenderJob>();
                _published.Clear();
            }

            if (!_preparation.TryLoadSynth())
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

                var phrase = _preparation.Prepare(TakeNext(pending, requestedSoloTime), token);
                if (phrase == null)
                {
                    return;
                }

                lock (_lock)
                {
                    _published.Add(phrase);
                }
            }
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

        private static RenderJob TakeNext(List<RenderJob> queue, double soloTime)
        {
            int index = queue.FindIndex(job => job.Covers(soloTime));
            if (index < 0)
            {
                index = 0;
            }

            var job = queue[index];
            queue.RemoveAt(index);
            return job;
        }

        private static List<RenderJob> CreateRequests(List<GuitarNote> notes, Func<double, double> playbackTime)
        {
            var spans = notes.SoloSpans();
            var requests = new List<RenderJob>(spans.Count);
            foreach (var (startTick, endTick) in spans)
            {
                var soloNotes = new List<SoloNote>();
                foreach (var note in notes)
                {
                    if (note.Tick < startTick || note.Tick > endTick)
                    {
                        continue;
                    }

                    soloNotes.Add(new SoloNote(note.NoteMask, note.Time, playbackTime(note.Time),
                        note.ClipDuration(), note.HasSustain()));
                }

                if (soloNotes.Count > 0)
                {
                    requests.Add(new RenderJob(soloNotes));
                }
            }

            return requests;
        }

        private void AddNoteWindow(RenderJob request, SoloNote note,
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
            request.Clips.Add((clip, note.Key));
            jobs.Add((startFrame, frameCount, clip.Samples));
        }

        private void AddRegionJob(RenderJob request, List<(long StartFrame, int FrameCount, float[] Samples)> jobs)
        {
            long firstFrame = long.MaxValue;
            long endFrame = 0;
            foreach (var (clip, _) in request.Clips)
            {
                firstFrame = Math.Min(firstFrame, clip.StartFrame);
                endFrame = Math.Max(endFrame, clip.StartFrame + clip.FrameCount);
            }

            if (endFrame <= firstFrame)
            {
                return;
            }

            request.RegionStartFrame = Math.Max(0, firstFrame - _format.FramesFor(REGION_PREROLL_SECONDS));
            int frameCount = (int) Math.Min(endFrame - request.RegionStartFrame, _format.FramesFor(MAX_SPACE_SECONDS));
            request.Region = new float[frameCount * _format.Channels];
            jobs.Add((request.RegionStartFrame, frameCount, request.Region));
        }
    }
}
