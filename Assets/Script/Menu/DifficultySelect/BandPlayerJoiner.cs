using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.InputSystem;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Helpers;
using YARG.Input.Bindings;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandPlayerJoiner
    {
        private static readonly GameMode[] NEW_PROFILE_MODES =
        {
            GameMode.FiveFretGuitar,
            GameMode.FourLaneDrums,
            GameMode.FiveLaneDrums,
            GameMode.EliteDrums,
            GameMode.SixFretGuitar,
            GameMode.ProKeys,
            GameMode.ProGuitar,
            GameMode.Vocals,
        };

        internal readonly struct PendingJoin
        {
            public readonly InputDevice Device;
            public readonly bool Owned;
            public readonly YargProfile Created;

            public PendingJoin(InputDevice device, bool owned, YargProfile created)
            {
                Device = device;
                Owned = owned;
                Created = created;
            }
        }

        private readonly Dictionary<BandColumn, PendingJoin> _joins = new();

        public bool IsSuppressingPlayerEvents { get; private set; }

        public bool TryAttachWithDevice(InputDevice device, out YargPlayer player, out PendingJoin join)
        {
            var profile = FindProfileForDevice(device);
            YargProfile created = null;
            if (profile == null)
            {
                created = CreateProfileFor(device);
                profile = created;
            }

            player = AttachPlayer(profile, device, out bool owned);
            if (player == null)
            {
                if (created != null)
                {
                    PlayerContainer.RemoveProfile(created);
                }

                join = default;
                return false;
            }

            join = new PendingJoin(device, owned, created);
            return true;
        }

        public bool TryAttachWithoutDevice(YargProfile profile, out YargPlayer player, out PendingJoin join)
        {
            player = AttachPlayer(profile, null, out _);
            if (player == null)
            {
                join = default;
                return false;
            }

            join = new PendingJoin(null, owned: true, created: null);
            return true;
        }

        public bool TryReattachProfile(BandColumn column, YargProfile profile, out YargPlayer player)
        {
            var device = _joins[column].Device;
            ReleaseJoin(column);

            player = AttachPlayer(profile, device, out _);
            return player != null;
        }

        public void TrackJoin(BandColumn column, PendingJoin join)
        {
            _joins[column] = join;
        }

        public void CompleteJoin(BandColumn column)
        {
            _joins.Remove(column);
        }

        public void UntrackJoin(BandColumn column)
        {
            _joins.Remove(column);
        }

        public void ReleaseJoin(BandColumn column)
        {
            var join = _joins[column];
            var player = column.Player;

            if (!join.Owned)
            {
                player.DisableInputs();
                player.DeviceInfo.RemoveController(join.Device);
            }

            DisposePlayer(player);

            if (join.Created != null)
            {
                PlayerContainer.RemoveProfile(join.Created);
            }
        }

        public void DisposePlayer(YargPlayer player)
        {
            RunWithSuppressedPlayerEvents(() => PlayerContainer.DisposePlayer(player));
        }

        public List<BandColumn> SnapshotJoiningColumns()
        {
            return new List<BandColumn>(_joins.Keys);
        }

        public static YargProfile FindAvailableProfile()
        {
            return PlayerContainer.Profiles
                .Where(p => !PlayerContainer.IsProfileTaken(p))
                .OrderBy(p => p.IsBot)
                .ThenByDescending(p => p.LastUsed)
                .FirstOrDefault();
        }

        private static YargProfile FindProfileForDevice(InputDevice device)
        {
            return PlayerContainer.GetProfileForDevice(device) ?? PlayerContainer.Profiles
                .Where(p => !p.IsBot && !PlayerContainer.IsProfileTaken(p))
                .OrderByDescending(p => p.LastUsed)
                .FirstOrDefault();
        }

        private YargPlayer AttachPlayer(YargProfile profile, InputDevice device, out bool owned)
        {
            if (device == null)
            {
                owned = true;
                return CreatePlayerSuppressed(profile, resolveDevices: true);
            }

            var deviceInfo = BindingsContainer.GetDeviceInfoForProfile(profile);
            owned = deviceInfo.MatchesController(device) || deviceInfo.ContainsController(device);

            var player = CreatePlayerSuppressed(profile, resolveDevices: false);
            if (player == null)
            {
                return null;
            }

            player.DisableInputs();
            player.DeviceInfo.AddController(device);
            player.DeviceInfo.ResolveMicrophones();
            player.EnableInputs();
            return player;
        }

        private YargPlayer CreatePlayerSuppressed(YargProfile profile, bool resolveDevices)
        {
            YargPlayer player = null;
            RunWithSuppressedPlayerEvents(() =>
                player = PlayerContainer.CreatePlayerFromProfile(profile, resolveDevices));
            return player;
        }

        private void RunWithSuppressedPlayerEvents(Action action)
        {
            IsSuppressingPlayerEvents = true;
            try
            {
                action();
            }
            finally
            {
                IsSuppressingPlayerEvents = false;
            }
        }

        private static YargProfile CreateProfileFor(InputDevice device)
        {
            var family = LayoutHelper.InputDeviceToControllerFamily(device);
            var mode = NEW_PROFILE_MODES.FirstOrDefault(m =>
                BindingsContainer.GetBindingSetsForControllerInMode(family, m).Count > 0);

            var instrument = mode.PossibleInstruments()[0];
            var profile = new YargProfile
            {
                Name = ProfilesMenu.GetUniqueProfileName("New Profile"),
                NoteSpeed = 5,
                HighwayLength = 1,
                GameMode = mode,
                CurrentInstrument = instrument,
                PreferredInstrument = instrument,
            };

            PlayerContainer.AddProfile(profile);
            return profile;
        }
    }
}
