using System;
using System.Collections.Generic;
using System.Linq;
using YARG.Core.Audio;

namespace YARG.Audio
{
    /// <summary>
    /// Controls volume for a single mixer group
    /// </summary>
    public class MixerGroupController
    {
        private readonly StemMixer _mixer;
        private readonly SongStem[] _stems;

        internal MixerGroupController(StemMixer mixer, SongStem[] stems)
        {
            _mixer = mixer;
            _stems = stems;
        }

        /// <summary>
        /// Sets the volume for this mixer group
        /// </summary>
        public void SetVolume(double volume)
        {
            foreach (var stem in _stems)
            {
                _mixer[stem]?.SetVolume(volume);
            }
        }

        public void SetPitch(float whammyFactor)
        {
            foreach (var stem in _stems)
            {
                _mixer[stem]?.SetWhammyPitch(whammyFactor);
            }
        }

        public void SetReverb(bool active)
        {
            foreach (var stem in _stems)
            {
                _mixer[stem]?.SetReverb(active);
            }
        }
    }

    /// <summary>
    /// Controls individual stem volumes on a mixer
    /// </summary>
    public class MixerController
    {
        private readonly StemMixer                                    _mixer;
        private readonly Dictionary<MixerGroup, MixerGroupController> _mixerGroups = new();
        public           bool                                         IsMultiTrack => _mixer.Channels.Count > 1;

        private static readonly Dictionary<MixerGroup, SongStem[]> StemMapping = new()
        {
            { MixerGroup.Guitar, new[] { SongStem.Guitar } },
            { MixerGroup.Bass, new[] { SongStem.Bass, SongStem.Rhythm } },
            { MixerGroup.Keys, new[] { SongStem.Keys } },
            { MixerGroup.Drums, new[] { SongStem.Drums, SongStem.Drums1, SongStem.Drums2, SongStem.Drums3, SongStem.Drums4 } },
            { MixerGroup.Vocals, new[] { SongStem.Vocals, SongStem.Vocals1, SongStem.Vocals2 } },
            { MixerGroup.Song, new[] { SongStem.Song } }
        };

        public MixerController(StemMixer mixer)
        {
            _mixer = mixer;
        }

        public void AddMixerGroup(MixerGroup group)
        {
            if (_mixerGroups.ContainsKey(group))
            {
                return;
            }

            var availableStems = StemMapping[group].Where(stem => _mixer[stem] != null);
            // Bass takes priority over Rhythm - only use the first available
            if (group == MixerGroup.Bass)
            {
                availableStems = availableStems.Take(1);
            }
            var stems = availableStems.ToArray();
            _mixerGroups.Add(group, new MixerGroupController(_mixer, stems));
        }

        /// <summary>
        /// Gets the controller for a specific mixer group
        /// </summary>
        public MixerGroupController GetGroupController(MixerGroup group)
        {
            return _mixerGroups.TryGetValue(group, out var controller) ? controller : null;
        }

        /// <summary>
        /// Sets the volume for the specified mixer group
        /// </summary>
        public void SetVolume(MixerGroup group, double volume)
        {
            if (_mixerGroups.TryGetValue(group, out var controller))
            {
                controller.SetVolume(volume);
            }
        }
    }
}