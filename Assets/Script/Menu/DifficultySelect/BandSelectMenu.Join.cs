using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Gameplay.Visuals;
using YARG.Helpers;
using YARG.Input;
using YARG.Input.Bindings;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu : MonoBehaviour
    {
        private void SetJoinSlotCount(int count)
        {
            while (_joinSlots.Count > count)
            {
                DestroyJoinSlot(_joinSlots.OrderBy(slot => slot.transform.GetSiblingIndex()).Last());
            }

            while (_joinSlots.Count < count)
            {
                CreateJoinSlot(_band.childCount);
            }
        }

        private LayoutElement CreateJoinSlot(int siblingIndex)
        {
            var slot = Instantiate(_joinSlot, _band);
            slot.transform.SetSiblingIndex(siblingIndex);
            slot.preferredWidth = -1f;
            var card = JoinCard(slot);
            card.anchoredPosition = Vector2.zero;
            foreach (var graphic in card.GetComponentsInChildren<Graphic>())
            {
                graphic.raycastTarget = true;
            }

            card.gameObject.AddComponent<JoinSlotClick>().Clicked = () => JoinWithoutDevice(slot);
            slot.gameObject.SetActive(true);
            _joinSlots.Add(slot);
            return slot;
        }

        private void DestroyJoinSlot(LayoutElement slot)
        {
            var card = JoinCard(slot);
            _joinSlots.Remove(slot);
            _shifts.Remove(card);
            _resizes.Remove(card);
            card.DOKill();
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
                if (player is null)
                {
                    RemoveColumn(column);
                    return;
                }

                column.Player = player;
            }

            var before = CaptureCardPositions();
            _joins.Remove(column);
            column.CompleteJoin();
            OnColumnChanged();
            ShiftCards(before, delay: 0f, riseDelay: SLIDE_SECONDS);
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

            foreach (var column in _columns)
            {
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
            var (device, owned, created) = _joins[column];
            var player = column.Player;

            if (!owned)
            {
                player.DisableInputs();
                player.DeviceInfo.RemoveController(device);
            }

            DisposePlayer(player);

            if (created is not null)
            {
                PlayerContainer.RemoveProfile(created);
            }
        }

        private void DisposePlayer(YargPlayer player)
        {
            _suppressPlayerEvents = true;
            try
            {
                PlayerContainer.DisposePlayer(player);
            }
            finally
            {
                _suppressPlayerEvents = false;
            }
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
            if (profile is null)
            {
                created = CreateProfileFor(device);
                profile = created;
            }

            var player = AttachPlayer(profile, device, out bool owned);
            if (player is null)
            {
                if (created is not null)
                {
                    PlayerContainer.RemoveProfile(created);
                }

                return;
            }

            var before = CaptureCardPositions();
            var column = InsertColumn(player, before);
            _joins[column] = (device, owned, created);
            column.BeginJoin();
            OnColumnChanged();
            ShiftCards(before, delay: SLIDE_SECONDS, riseDelay: SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
        }

        private BandColumn JoinWithoutDevice(LayoutElement slot, YargProfile chosen = null)
        {
            if (IsJoinBlocked || _profilesInUse != null || !_joinSlots.Contains(slot))
            {
                return null;
            }

            var profile = chosen ?? PlayerContainer.Profiles
                .Where(p => !PlayerContainer.IsProfileTaken(p))
                .OrderBy(p => p.IsBot)
                .ThenByDescending(p => p.LastUsed)
                .FirstOrDefault();
            if (profile is null)
            {
                ShowProfilesInUse(slot);
                return null;
            }

            var player = AttachPlayer(profile, null, out _);
            if (player is null)
            {
                return null;
            }

            var before = CaptureCardPositions();
            var column = InsertColumn(player, before, slot);
            _joins[column] = (null, true, null);
            column.BeginJoin();
            OnColumnChanged();
            ShiftCards(before, delay: SLIDE_SECONDS, riseDelay: SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
            return column;
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
            if (device is null)
            {
                owned = true;
                _suppressPlayerEvents = true;
                try
                {
                    return PlayerContainer.CreatePlayerFromProfile(profile, resolveDevices: true);
                }
                finally
                {
                    _suppressPlayerEvents = false;
                }
            }

            var deviceInfo = BindingsContainer.GetDeviceInfoForProfile(profile);
            owned = deviceInfo.MatchesController(device) || deviceInfo.ContainsController(device);

            YargPlayer player;
            _suppressPlayerEvents = true;
            try
            {
                player = PlayerContainer.CreatePlayerFromProfile(profile, resolveDevices: false);
            }
            finally
            {
                _suppressPlayerEvents = false;
            }

            if (player is null)
            {
                return null;
            }

            player.DisableInputs();
            player.DeviceInfo.AddController(device);
            player.DeviceInfo.ResolveMicrophones();
            player.EnableInputs();
            return player;
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
            if (_suppressPlayerEvents || FindColumn(player) is not null)
            {
                return;
            }

            var before = CaptureCardPositions();
            var column = InsertColumn(player, before);
            column.RiseFrom(-SLIDE_DISTANCE);
            column.Open();
            OnColumnChanged();
            ShiftCards(before, delay: 0f, riseDelay: null);
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            if (_suppressPlayerEvents)
            {
                return;
            }

            var column = FindColumn(player);
            if (column is not null)
            {
                RemoveColumn(column);
            }
        }

    }
}
