using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core.Extensions;
using YARG.Helpers;
using YARG.Input.Bindings;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu
    {
        private readonly struct JoinSlotEntry
        {
            public readonly LayoutElement Slot;
            public readonly RectTransform Card;
            public readonly TextMeshProUGUI Label;

            public JoinSlotEntry(LayoutElement slot, RectTransform card, TextMeshProUGUI label)
            {
                Slot = slot;
                Card = card;
                Label = label;
            }
        }

        private readonly struct PendingJoin
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

        private void SetJoinSlotCount(int count)
        {
            while (_joinSlots.Count > count)
            {
                DestroyJoinSlot(LatestJoinSlot());
            }

            while (_joinSlots.Count < count)
            {
                CreateJoinSlot(_band.childCount);
            }
        }

        private LayoutElement EarliestJoinSlot()
        {
            LayoutElement best = null;
            int bestIndex = int.MaxValue;
            for (int i = 0; i < _joinSlots.Count; i++)
            {
                var slot = _joinSlots[i].Slot;
                int siblingIndex = slot.transform.GetSiblingIndex();
                if (siblingIndex < bestIndex)
                {
                    bestIndex = siblingIndex;
                    best = slot;
                }
            }

            return best;
        }

        private LayoutElement LatestJoinSlot()
        {
            LayoutElement best = null;
            int bestIndex = -1;
            for (int i = 0; i < _joinSlots.Count; i++)
            {
                var slot = _joinSlots[i].Slot;
                int siblingIndex = slot.transform.GetSiblingIndex();
                if (siblingIndex > bestIndex)
                {
                    bestIndex = siblingIndex;
                    best = slot;
                }
            }

            return best;
        }

        private LayoutElement CreateJoinSlot(int siblingIndex)
        {
            var slot = Instantiate(_joinSlot, _band);
            slot.transform.SetSiblingIndex(siblingIndex);
            slot.preferredWidth = -1f;

            var card = JoinCard(slot);
            card.anchoredPosition = Vector2.zero;
            var graphics = card.GetComponentsInChildren<Graphic>();
            for (int i = 0; i < graphics.Length; i++)
            {
                graphics[i].raycastTarget = true;
            }

            card.gameObject.AddComponent<JoinSlotClick>().Clicked = () => JoinWithoutDevice(slot);
            slot.gameObject.SetActive(true);

            var label = slot.GetComponentInChildren<TextMeshProUGUI>(true);
            _joinSlots.Add(new JoinSlotEntry(slot, card, label));
            _joinCards.Add(card);
            return slot;
        }

        private void DestroyJoinSlot(LayoutElement slot)
        {
            if (slot == null)
            {
                return;
            }

            var card = JoinCard(slot);
            for (int i = 0; i < _joinSlots.Count; i++)
            {
                if (_joinSlots[i].Slot == slot)
                {
                    _joinSlots.RemoveAt(i);
                    break;
                }
            }

            _joinCards.Remove(card);
            _animator.KillTweens(card);
            slot.gameObject.SetActive(false);
            Destroy(slot.gameObject);
        }

        private static RectTransform JoinCard(LayoutElement slot)
        {
            return (RectTransform) slot.transform.GetChild(0);
        }

        internal void ChooseJoinProfile(BandColumn column, YargProfile profile)
        {
            if (column == _profilesInUse)
            {
                var slot = _profilesInUseSlot;
                CloseProfilesInUse();
                var joined = JoinWithoutDevice(slot, profile);
                if (joined != null)
                {
                    ChooseJoinProfile(joined, profile);
                }

                return;
            }

            if (profile != column.Player.Profile)
            {
                var device = _joins[column].Device;
                ReleaseJoin(column);

                var player = AttachPlayer(profile, device, out _);
                if (player == null)
                {
                    RemoveColumn(column);
                    return;
                }

                column.Player = player;
            }

            CaptureCardPositions();
            _joins.Remove(column);
            column.CompleteJoin();
            OnColumnChanged();
            _animator.ShiftCards(_band, _columns, _joinCards, _cardSnapshotBuffer, delay: 0f,
                riseDelay: SLIDE_SECONDS);
            PlayerContainer.SaveProfiles();
            BindingsContainer.SaveBindings();
            RefreshProfilePickers(column);
        }

        private void RefreshProfilePickers(BandColumn changed)
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.Rebuild();
            }

            for (int i = 0; i < _columns.Count; i++)
            {
                var column = _columns[i];
                if (column != changed && column.State == BandColumnState.ChooseProfile)
                {
                    column.Rebuild();
                }
            }
        }

        internal void CancelJoin(BandColumn column)
        {
            ReleaseJoin(column);
            RemoveColumn(column);
        }

        private void ReleaseJoin(BandColumn column)
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

        private void DisposePlayer(YargPlayer player)
        {
            RunWithSuppressedPlayerEvents(() => PlayerContainer.DisposePlayer(player));
        }

        internal void Disconnect(BandColumn column)
        {
            if (column.IsJoining)
            {
                CancelJoin(column);
                return;
            }

            DisposePlayer(column.Player);
            RemoveColumn(column);
        }

        private bool OnUnclaimedDevicePressed(InputDevice device)
        {
            if (PlayerContainer.IsControllerTaken(device))
            {
                return false;
            }

            if (IsJoinBlocked)
            {
                return true;
            }

            if (!_pendingJoins.Contains(device))
            {
                _pendingJoins.Add(device);
            }

            return false;
        }

        private void Join(InputDevice device)
        {
            if (IsFull || PlayerContainer.IsControllerTaken(device))
            {
                return;
            }

            var profile = PlayerContainer.GetProfileForDevice(device) ?? PlayerContainer.Profiles
                .Where(p => !p.IsBot && !PlayerContainer.IsProfileTaken(p))
                .OrderByDescending(p => p.LastUsed)
                .FirstOrDefault();

            YargProfile created = null;
            if (profile == null)
            {
                created = CreateProfileFor(device);
                profile = created;
            }

            var player = AttachPlayer(profile, device, out bool owned);
            if (player == null)
            {
                if (created != null)
                {
                    PlayerContainer.RemoveProfile(created);
                }

                return;
            }

            FinishJoin(player, new PendingJoin(device, owned, created), targetSlot: null);
        }

        private BandColumn JoinWithoutDevice(LayoutElement slot, YargProfile chosen = null)
        {
            if (IsJoinBlocked || _profilesInUse != null || !ContainsJoinSlot(slot))
            {
                return null;
            }

            var profile = chosen ?? PlayerContainer.Profiles
                .Where(p => !PlayerContainer.IsProfileTaken(p))
                .OrderBy(p => p.IsBot)
                .ThenByDescending(p => p.LastUsed)
                .FirstOrDefault();
            if (profile == null)
            {
                ShowProfilesInUse(slot);
                return null;
            }

            var player = AttachPlayer(profile, null, out _);
            if (player == null)
            {
                return null;
            }

            return FinishJoin(player, new PendingJoin(null, owned: true, created: null), slot);
        }

        private BandColumn FinishJoin(YargPlayer player, PendingJoin join, LayoutElement targetSlot)
        {
            CaptureCardPositions();
            var column = InsertColumn(player, _cardSnapshotBuffer, targetSlot);
            _joins[column] = join;
            column.BeginJoin();
            OnColumnChanged();
            _animator.ShiftCards(_band, _columns, _joinCards, _cardSnapshotBuffer, delay: SLIDE_SECONDS,
                riseDelay: SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
            return column;
        }

        private bool ContainsJoinSlot(LayoutElement slot)
        {
            for (int i = 0; i < _joinSlots.Count; i++)
            {
                if (_joinSlots[i].Slot == slot)
                {
                    return true;
                }
            }

            return false;
        }

        private void ShowProfilesInUse(LayoutElement slot)
        {
            var column = Instantiate(_columnPrefab, slot.transform);
            var rect = (RectTransform) column.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            column.Initialize(this, null);
            column.SetWidth(slot.preferredWidth);
            column.ShowProfilesInUse();

            _profilesInUse = column;
            _profilesInUseSlot = slot;

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
        }

        internal void CloseProfilesInUse()
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.DismissAndDestroy();
            }

            _profilesInUse = null;
            _profilesInUseSlot = null;
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
            _suppressPlayerEvents = true;
            try
            {
                action();
            }
            finally
            {
                _suppressPlayerEvents = false;
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

        private void OnPlayerAdded(YargPlayer player)
        {
            if (_suppressPlayerEvents || FindColumn(player) != null)
            {
                return;
            }

            CaptureCardPositions();
            var column = InsertColumn(player, _cardSnapshotBuffer);
            column.RiseFrom(-SLIDE_DISTANCE);
            column.Open();
            OnColumnChanged();
            _animator.ShiftCards(_band, _columns, _joinCards, _cardSnapshotBuffer, delay: 0f, riseDelay: null);
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            _seatTracker.Remove(player);

            if (_suppressPlayerEvents)
            {
                return;
            }

            var column = FindColumn(player);
            if (column != null)
            {
                RemoveColumn(column);
            }
        }
    }
}
