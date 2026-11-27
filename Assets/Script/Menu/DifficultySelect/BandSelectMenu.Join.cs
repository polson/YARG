using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Input.Bindings;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu
    {
        internal readonly struct JoinSlotEntry
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

        private void SetJoinSlotCount(int count)
        {
            while (_joinSlots.Count > count)
            {
                DestroyJoinSlot(FindJoinSlotBySiblingOrder(earliest: false));
            }

            while (_joinSlots.Count < count)
            {
                CreateJoinSlot(_band.childCount);
            }
        }

        private LayoutElement EarliestJoinSlot()
        {
            return FindJoinSlotBySiblingOrder(earliest: true);
        }

        private LayoutElement FindJoinSlotBySiblingOrder(bool earliest)
        {
            LayoutElement best = null;
            int bestIndex = earliest ? int.MaxValue : -1;
            foreach (var entry in _joinSlots)
            {
                var slot = entry.Slot;
                int siblingIndex = slot.transform.GetSiblingIndex();
                if (earliest ? siblingIndex < bestIndex : siblingIndex > bestIndex)
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
            foreach (var graphic in card.GetComponentsInChildren<Graphic>())
            {
                graphic.raycastTarget = true;
            }

            card.gameObject.AddComponent<JoinSlotClick>().Clicked = () => JoinWithoutDevice(slot);
            slot.gameObject.SetActive(true);

            var label = slot.GetComponentInChildren<TextMeshProUGUI>(true);
            _joinSlots.Add(new JoinSlotEntry(slot, card, label));
            return slot;
        }

        private void DestroyJoinSlot(LayoutElement slot)
        {
            if (slot == null)
            {
                return;
            }

            var card = JoinCard(slot);
            int index = IndexOfJoinSlot(slot);
            if (index >= 0)
            {
                _joinSlots.RemoveAt(index);
            }

            _animator.KillTweens(card);
            slot.gameObject.SetActive(false);
            Destroy(slot.gameObject);
        }

        private int IndexOfJoinSlot(LayoutElement slot)
        {
            for (int i = 0; i < _joinSlots.Count; i++)
            {
                if (_joinSlots[i].Slot == slot)
                {
                    return i;
                }
            }

            return -1;
        }

        private static RectTransform JoinCard(LayoutElement slot)
        {
            return (RectTransform) slot.transform.GetChild(0);
        }

        internal void ChooseJoinProfile(BandColumn column, YargProfile profile)
        {
            if (column == _profilesInUse)
            {
                ChooseProfileForPendingSlot(profile);
                return;
            }

            if (profile != column.Player.Profile)
            {
                if (!_joiner.TryReattachProfile(column, profile, out var player))
                {
                    RemoveColumn(column);
                    return;
                }

                column.Player = player;
            }

            CaptureCardPositions();
            _joiner.CompleteJoin(column);
            column.CompleteJoin();
            OnColumnChanged();
            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: 0f,
                riseDelay: SLIDE_SECONDS);
            PlayerContainer.SaveProfiles();
            BindingsContainer.SaveBindings();
            RefreshProfilePickers(column);
        }

        private void ChooseProfileForPendingSlot(YargProfile profile)
        {
            var slot = _profilesInUseSlot;
            CloseProfilesInUse();
            var joined = JoinWithoutDevice(slot, profile);
            if (joined != null)
            {
                ChooseJoinProfile(joined, profile);
            }
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
            _joiner.ReleaseJoin(column);
            RemoveColumn(column);
        }

        internal void Disconnect(BandColumn column)
        {
            if (column.IsJoining)
            {
                CancelJoin(column);
                return;
            }

            _joiner.DisposePlayer(column.Player);
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

            if (_joiner.TryAttachWithDevice(device, out var player, out var join))
            {
                FinishJoin(player, join, targetSlot: null);
            }
        }

        private BandColumn JoinWithoutDevice(LayoutElement slot, YargProfile chosen = null)
        {
            if (IsJoinBlocked || _profilesInUse != null || IndexOfJoinSlot(slot) < 0)
            {
                return null;
            }

            var profile = chosen ?? BandPlayerJoiner.FindAvailableProfile();
            if (profile == null)
            {
                ShowProfilesInUse(slot);
                return null;
            }

            if (!_joiner.TryAttachWithoutDevice(profile, out var player, out var join))
            {
                return null;
            }

            return FinishJoin(player, join, slot);
        }

        private BandColumn FinishJoin(YargPlayer player, BandPlayerJoiner.PendingJoin join, LayoutElement targetSlot)
        {
            CaptureCardPositions();
            var column = InsertColumn(player, _cardSnapshotBuffer, targetSlot);
            _joiner.TrackJoin(column, join);
            column.BeginJoin();
            OnColumnChanged();
            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: SLIDE_SECONDS,
                riseDelay: SLIDE_SECONDS * 2f);

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

        private void OnPlayerAdded(YargPlayer player)
        {
            if (_joiner.IsSuppressingPlayerEvents || FindColumn(player) != null)
            {
                return;
            }

            CaptureCardPositions();
            var column = InsertColumn(player, _cardSnapshotBuffer);
            column.RiseFrom(-SLIDE_DISTANCE);
            column.Open();
            OnColumnChanged();
            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: 0f, riseDelay: null);
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            _seatTracker.Remove(player);

            if (_joiner.IsSuppressingPlayerEvents)
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
